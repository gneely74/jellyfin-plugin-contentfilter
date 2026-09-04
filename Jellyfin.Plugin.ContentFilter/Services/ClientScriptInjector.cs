using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Handles injection of the client-side playback filter script into Jellyfin Web.
/// </summary>
public static class ClientScriptInjector
{
    private static ILogger? _logger;
    private static bool _initialized;

    /// <summary>
    /// Input payload structure received from FileTransformation.
    /// </summary>
    public sealed class TransformationInput
    {
        /// <summary>
        /// Gets or sets the file contents.
        /// </summary>
        public string Contents { get; set; } = string.Empty;
    }

    /// <summary>
    /// Initializes script injection hooks with FileTransformation or other injectors.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public static void Initialize(ILogger logger)
    {
        _logger = logger;
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        RegisterWithFileTransformation();
    }

    private static void RegisterWithFileTransformation()
    {
        try
        {
            var ftAssembly = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.OrdinalIgnoreCase) == true);

            if (ftAssembly is null)
            {
                _logger?.LogInformation("ContentFilter: FileTransformation plugin not detected; web injection fallback active.");
                return;
            }

            var pluginInterfaceType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterfaceType is null)
            {
                _logger?.LogWarning("ContentFilter: FileTransformation PluginInterface type not found.");
                return;
            }

            var registerMethod = pluginInterfaceType.GetMethod("RegisterTransformation");
            if (registerMethod is null)
            {
                _logger?.LogWarning("ContentFilter: FileTransformation RegisterTransformation method not found.");
                return;
            }

            // Prepare payload using JObject via reflection from Newtonsoft.Json
            var newtonsoftAssembly = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(a => a.GetName().Name?.Equals("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase) == true);

            var jobjectType = newtonsoftAssembly?.GetType("Newtonsoft.Json.Linq.JObject");
            if (jobjectType is null)
            {
                _logger?.LogWarning("ContentFilter: Newtonsoft.Json.Linq.JObject type not found.");
                return;
            }

            var payload = Activator.CreateInstance(jobjectType);
            var parseMethod = jobjectType.GetMethod("Parse", [typeof(string)]);

            var json = $$"""
            {
                "id": "A62B2473-77E1-45C1-8470-57FB95A85394",
                "fileNamePattern": "index\\.html",
                "callbackAssembly": "{{typeof(ClientScriptInjector).Assembly.FullName}}",
                "callbackClass": "{{typeof(ClientScriptInjector).FullName}}",
                "callbackMethod": "{{nameof(TransformIndexHtml)}}"
            }
            """;

            var jobj = parseMethod?.Invoke(null, [json]);
            if (jobj is not null)
            {
                registerMethod.Invoke(null, [jobj]);
                _logger?.LogInformation("ContentFilter: Successfully registered client script transformation with FileTransformation plugin.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ContentFilter: Exception while registering transformation with FileTransformation plugin.");
        }
    }

    /// <summary>
    /// Callback method invoked by FileTransformation when index.html is served.
    /// </summary>
    /// <param name="input">The transformation input containing index.html contents.</param>
    /// <returns>The transformed HTML content with client.js injected.</returns>
    public static string TransformIndexHtml(TransformationInput input)
    {
        if (input?.Contents is null)
        {
            return string.Empty;
        }

        const string scriptTag = "<script src=\"../ContentFilter/client.js\" defer></script>";
        if (input.Contents.Contains(scriptTag, StringComparison.Ordinal))
        {
            return input.Contents;
        }

        var bodyClose = input.Contents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose >= 0)
        {
            return input.Contents.Insert(bodyClose, $"{scriptTag}\n");
        }

        return input.Contents + "\n" + scriptTag;
    }
}

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
        [System.Text.Json.Serialization.JsonPropertyName("contents")]
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

            var parseMethod = jobjectType.GetMethod("Parse", [typeof(string)]);

            var json = $$"""
            {
                "id": "A62B2473-77E1-45C1-8470-57FB95A85394",
                "fileNamePattern": "index.html",
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
    /// Accepts object to safely handle cross-assembly serialization models.
    /// </summary>
    /// <param name="input">The transformation input containing index.html contents.</param>
    /// <returns>The transformed HTML content with client.js injected.</returns>
    public static string TransformIndexHtml(object? input)
    {
        if (input is null)
        {
            return string.Empty;
        }

        string? contents = null;
        if (input is string str)
        {
            contents = str;
        }
        else
        {
            var prop = input.GetType().GetProperty("Contents", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop is not null)
            {
                contents = prop.GetValue(input) as string;
            }
        }

        if (string.IsNullOrEmpty(contents))
        {
            return string.Empty;
        }

        const string scriptTag = "<script src=\"../ContentFilter/client.js\" defer></script>";
        if (contents.Contains(scriptTag, StringComparison.Ordinal))
        {
            return contents;
        }

        var headClose = contents.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headClose >= 0)
        {
            return contents.Insert(headClose, $"{scriptTag}\n");
        }

        var bodyClose = contents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose >= 0)
        {
            return contents.Insert(bodyClose, $"{scriptTag}\n");
        }

        return contents + "\n" + scriptTag;
    }
}

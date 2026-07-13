using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ContentFilter.Models;

/// <summary>
/// Represents an Ollama generation request.
/// </summary>
public sealed class OllamaGenerateRequest
{
    /// <summary>
    /// Gets or sets the model identifier.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the prompt text.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional base64-encoded images.
    /// </summary>
    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether streaming is enabled.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    /// <summary>
    /// Gets or sets the response format.
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = "json";
}

/// <summary>
/// Represents an Ollama generation response.
/// </summary>
public sealed class OllamaGenerateResponse
{
    /// <summary>
    /// Gets or sets the response content.
    /// </summary>
    [JsonPropertyName("response")]
    public string Response { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether generation completed.
    /// </summary>
    [JsonPropertyName("done")]
    public bool Done { get; set; }
}

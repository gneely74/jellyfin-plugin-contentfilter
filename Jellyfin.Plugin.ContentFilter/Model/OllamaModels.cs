using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ContentFilter.Models;

/// <summary>OpenAI-compatible chat/completions request (works with Ollama /v1 and oMLX).</summary>
public sealed class VisionChatRequest
{
    /// <summary>Gets or sets the model identifier.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the list of chat messages.</summary>
    [JsonPropertyName("messages")]
    public List<VisionChatMessage> Messages { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether to stream the response.</summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    /// <summary>Gets or sets the response format (e.g. json_object).</summary>
    [JsonPropertyName("response_format")]
    public VisionResponseFormat ResponseFormat { get; set; } = new();
}

/// <summary>A single chat message in the request.</summary>
public sealed class VisionChatMessage
{
    /// <summary>Gets or sets the role (e.g. "user").</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>Gets or sets the list of content parts (text and/or images).</summary>
    [JsonPropertyName("content")]
    public List<VisionContentPart> Content { get; set; } = [];
}

/// <summary>A text or image_url content part.</summary>
public sealed class VisionContentPart
{
    /// <summary>Gets or sets the part type: "text" or "image_url".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the text value for text-type parts.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>Gets or sets the image URL for image_url-type parts.</summary>
    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisionImageUrl? ImageUrl { get; set; }
}

/// <summary>Image URL wrapper for content parts.</summary>
public sealed class VisionImageUrl
{
    /// <summary>Gets or sets the image URL or base64 data URI.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Response format specifier.</summary>
public sealed class VisionResponseFormat
{
    /// <summary>Gets or sets the format type, e.g. "json_object".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "json_object";
}

/// <summary>OpenAI-compatible chat/completions response.</summary>
public sealed class VisionChatResponse
{
    /// <summary>Gets or sets the list of completion choices.</summary>
    [JsonPropertyName("choices")]
    public List<VisionChatChoice>? Choices { get; set; }
}

/// <summary>A single choice in the response.</summary>
public sealed class VisionChatChoice
{
    /// <summary>Gets or sets the assistant message for this choice.</summary>
    [JsonPropertyName("message")]
    public VisionResponseMessage? Message { get; set; }
}

/// <summary>The assistant message returned by the model.</summary>
public sealed class VisionResponseMessage
{
    /// <summary>Gets or sets the text content of the assistant reply.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

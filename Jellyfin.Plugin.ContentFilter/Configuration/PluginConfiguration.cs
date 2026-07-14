using MediaBrowser.Model.Plugins;
namespace Jellyfin.Plugin.ContentFilter.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
    }

    // --- Group-level enable ---

    /// <summary>Gets or sets a value indicating whether language-related filtering is enabled.</summary>
    public bool LanguageEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether sexual reference filtering is enabled.</summary>
    public bool SexualReferencesEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether sex and nudity filtering is enabled.</summary>
    public bool SexAndNudityEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether violence filtering is enabled.</summary>
    public bool ViolenceEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether substances filtering is enabled.</summary>
    public bool SubstancesEnabled { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether medical filtering is enabled.</summary>
    public bool MedicalEnabled { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether structural filtering is enabled.</summary>
    public bool StructuralEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the set of individual filter items that have been explicitly disabled.
    /// Each entry is formatted as <c>{CategoryKey}:{term}</c>, e.g. <c>Language.GeneralProfanity:ass</c>.
    /// An empty list means all items within enabled groups are active.
    /// </summary>
    public List<string> DisabledFilterItems { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether content filtering is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the Ollama base URL.
    /// </summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>
    /// Gets or sets the Ollama model used for vision analysis.
    /// </summary>
    public string OllamaVisionModel { get; set; } = "llava";

    /// <summary>
    /// Gets or sets the Ollama model used for text analysis.
    /// </summary>
    public string OllamaTextModel { get; set; } = "llama3.2";

    /// <summary>
    /// Gets or sets the number of frames analyzed per second.
    /// </summary>
    public double ScanFramesPerSecond { get; set; } = 0.5;

    /// <summary>
    /// Gets or sets a value indicating whether audio should be analyzed during scanning.
    /// </summary>
    public bool ScanAnalyzeAudio { get; set; } = true;

    /// <summary>
    /// Gets or sets the preferred audio language code (ISO 639-1, e.g. "en", "fr").
    /// Used to select the right audio stream and to find WhisperSubs-generated subtitle files.
    /// </summary>
    public string PreferredAudioLanguage { get; set; } = "en";

    /// <summary>
    /// Gets or sets the maximum number of seconds of video to scan per item when debug logging is enabled.
    /// A value of 0 disables the limit (full scan). Only respected when the server is at debug log level.
    /// </summary>
    public int DebugScanMaxSeconds { get; set; } = 0;
}

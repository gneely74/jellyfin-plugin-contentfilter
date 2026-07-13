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

    /// <summary>
    /// Gets or sets a value indicating whether language-related filtering is enabled.
    /// </summary>
    public bool LanguageEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the action to apply for language-related cues.
    /// </summary>
    public string LanguageAction { get; set; } = "mute";

    /// <summary>
    /// Gets or sets a value indicating whether sexual reference filtering is enabled.
    /// </summary>
    public bool SexualReferencesEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the action to apply for sexual reference cues.
    /// </summary>
    public string SexualReferencesAction { get; set; } = "skip";

    /// <summary>
    /// Gets or sets a value indicating whether sex and nudity filtering is enabled.
    /// </summary>
    public bool SexAndNudityEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the action to apply for sex and nudity cues.
    /// </summary>
    public string SexAndNudityAction { get; set; } = "skip";

    /// <summary>
    /// Gets or sets a value indicating whether violence filtering is enabled.
    /// </summary>
    public bool ViolenceEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the action to apply for violence cues.
    /// </summary>
    public string ViolenceAction { get; set; } = "skip";

    /// <summary>
    /// Gets or sets a value indicating whether substances filtering is enabled.
    /// </summary>
    public bool SubstancesEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the action to apply for substance-related cues.
    /// </summary>
    public string SubstancesAction { get; set; } = "skip";

    /// <summary>
    /// Gets or sets a value indicating whether medical event filtering is enabled.
    /// </summary>
    public bool MedicalEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the action to apply for medical event cues.
    /// </summary>
    public string MedicalAction { get; set; } = "skip";

    /// <summary>
    /// Gets or sets a value indicating whether structural timestamp filtering is enabled.
    /// </summary>
    public bool StructuralEnabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the action to apply for structural cues.
    /// </summary>
    public string StructuralAction { get; set; } = "skip";

    /// <summary>
    /// Gets or sets a value indicating whether content scanning is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the Ollama base URL.
    /// </summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

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
}

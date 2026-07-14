using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ContentFilter.Configuration;
using Jellyfin.Plugin.ContentFilter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ContentFilter.Services;

/// <summary>
/// Client for image-based visual content analysis via an Ollama server.
/// </summary>
public class OllamaClient
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<OllamaClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaClient"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public OllamaClient(ILogger<OllamaClient> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Sends a minimal request to verify the vision API is reachable and the model is loaded.
    /// </summary>
    /// <param name="model">Model name to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the model responded successfully.</returns>
    public async Task<bool> CheckReadyAsync(string model, CancellationToken ct)
    {
        var request = new VisionChatRequest
        {
            Model = model,
            Stream = false,
            Messages =
            [
                new VisionChatMessage
                {
                    Role = "user",
                    Content = [new VisionContentPart { Type = "text", Text = "ping" }]
                }
            ]
        };

        var baseUrl = GetVisionBaseUrl();
        var endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute), "v1/chat/completions");

        const int AttemptTimeoutSeconds = 20;  // per-ping request timeout
        const int RetryIntervalSeconds = 5;
        const int TimeoutSeconds = 180;          // total wait up to 3 min
        var deadline = DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds);
        var attempt = 0;
        var elapsed = 0;

        _logger.LogInformation("Vision API: pinging model '{Model}' (will wait up to {Timeout}s for it to load)...", model, TimeoutSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            elapsed = (int)(DateTimeOffset.UtcNow - (deadline - TimeSpan.FromSeconds(TimeoutSeconds))).TotalSeconds;

            try
            {
                // Short per-attempt timeout so we don't silently hang if the model is still loading
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(AttemptTimeoutSeconds));

                using var client = _httpClientFactory.CreateClient(nameof(OllamaClient));
                using var response = await client
                    .PostAsJsonAsync(endpoint, request, JsonSerializerOptions, attemptCts.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Vision API: model '{Model}' is ready (after {Elapsed}s).", model, elapsed);
                    return true;
                }

                _logger.LogInformation(
                    "Vision API: model '{Model}' not ready ({Status}) — {Elapsed}s elapsed, retrying...",
                    model, (int)response.StatusCode, elapsed);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-attempt timeout — model still loading
                _logger.LogInformation(
                    "Vision API: model '{Model}' still loading — {Elapsed}s elapsed, retrying...",
                    model, elapsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation(
                    "Vision API: attempt {Attempt} failed ({Type}) — {Elapsed}s elapsed, retrying...",
                    attempt, ex.GetType().Name, elapsed);
            }

            await Task.Delay(TimeSpan.FromSeconds(RetryIntervalSeconds), ct).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Vision API: model '{Model}' did not become ready within {Timeout}s — skipping visual analysis.",
            model, TimeoutSeconds);
        return false;
    }

    /// <summary>
    /// Analyzes a JPEG frame and returns detected content categories and an optional description.
    /// </summary>
    /// <param name="jpeg">Frame image data.</param>
    /// <param name="model">Ollama model name.</param>
    /// <param name="visualDescriptions">Visual sub-category prompts keyed by category.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detected sub-categories and description.</returns>
    public async Task<(HashSet<string> DetectedSubCategories, string Description)> AnalyzeFrameAsync(
        byte[] jpeg,
        string model,
        IEnumerable<KeyValuePair<string, string[]>> visualDescriptions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(visualDescriptions);

        var descriptions = visualDescriptions.ToArray();
        if (descriptions.Length == 0)
        {
            return ([], string.Empty);
        }

        var prompt = BuildPrompt(descriptions);
        var request = new VisionChatRequest
        {
            Model = model,
            Stream = false,
            Messages =
            [
                new VisionChatMessage
                {
                    Role = "user",
                    Content =
                    [
                        new VisionContentPart { Type = "text", Text = prompt },
                        new VisionContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new VisionImageUrl
                            {
                                Url = $"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}"
                            }
                        }
                    ]
                }
            ]
        };

        var knownCategories = FilterDictionary.Categories.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseUrl = GetVisionBaseUrl();
        var endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute), "v1/chat/completions");
        _logger.LogDebug("Vision API: POST {Endpoint} model={Model}", endpoint, model);

        try
        {
            using var client = _httpClientFactory.CreateClient(nameof(OllamaClient));

            // No per-frame timeout — inference can take minutes. Job CancellationToken is the only limit.
            using var response = await client.PostAsJsonAsync(endpoint, request, JsonSerializerOptions, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("Vision API: response body length={Length} bytes", body.Length);

            var payload = JsonSerializer.Deserialize<VisionChatResponse>(body, JsonSerializerOptions);
            var content = payload?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Vision API: no content in response (length={Length})", body.Length);
                return ([], string.Empty);
            }

            return ParseResponse(content, knownCategories);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Job explicitly cancelled — propagate.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ollama: frame analysis failed ({Type}): {Message} — skipping frame",
                ex.GetType().Name, ex.Message);
            return ([], string.Empty);
        }
    }

    private static string BuildPrompt(IEnumerable<KeyValuePair<string, string[]>> visualDescriptions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze this video frame. Return STRICT JSON only.");
        sb.AppendLine("List ONLY the categories below that are CLEARLY and EXPLICITLY visible in this frame.");
        sb.AppendLine("If you are not certain, do NOT include the category. Omit any category that does not clearly apply.");
        sb.AppendLine();
        sb.AppendLine("Available categories (include only if clearly present):");

        foreach (var (key, descriptions) in visualDescriptions)
        {
            sb.Append("  ");
            sb.Append(key);
            sb.Append(": ");
            sb.AppendLine(string.Join("; ", descriptions));
        }

        sb.AppendLine();
        sb.AppendLine("Return JSON in this exact format:");
        sb.AppendLine("{\"categories\":[\"Category.Name\"],\"description\":\"one sentence objective description of what is visible\"}");
        sb.AppendLine("If nothing applies: {\"categories\":[],\"description\":\"brief description\"}");
        return sb.ToString();
    }

    // Confirmation keywords per group — at least one must appear in a substantive description.
    // If none appear, the category was almost certainly hallucinated by the model.
    // Only visual groups are listed here; word-list groups (Substances, ContextualDialogue) are
    // handled by transcript matching and never reach this code path.
    private static readonly IReadOnlyDictionary<string, string[]> CategoryConfirmationKeywords =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Violence"] =
            [
                "violence", "violent", "gore", "blood", "fight", "gun", "weapon",
                "hit", "punch", "stab", "wound", "kill", "shot", "injur",
                "attack", "dead", "beat", "assault", "threaten", "hostage",
                "explosion", "bomb", "knife", "bullet", "shoot", "danger",
                "brutal", "brawl", "slap", "choke", "strangle", "murder",
                "disturbing", "scary", "graphic",
            ],
            ["SexAndNudity"] =
            [
                "nudity", "naked", "nude", "sex", "intimate", "intimacy",
                "kiss", "bare ", "topless", "shirtless", "underwear", "lingerie",
                "revealing", "suggestive", "erotic", "exposing", "cleavage",
            ],
            ["SexualReferences"] =
            [
                "gesture", "vulgar", "obscene",
            ],
        };

    // Descriptions shorter than this are given the benefit of the doubt — llava may have
    // been too brief to describe all scene content.
    private const int MinDescriptionLengthForConfirmation = 50;

    // Keywords per group used to detect when a description negates a detected category.
    // Only visual groups are listed; word-list groups never appear in vision output.
    private static readonly IReadOnlyDictionary<string, string[]> CategoryNegationKeywords =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Violence"]         = ["violence", "violent", "gore"],
            ["Medical"]          = ["medical"],
            ["Structural"]       = ["structural", "timestamp", "credits"],
            ["SexAndNudity"]     = ["nudity", "naked", "sexual activ", "physical intimacy"],
            ["SexualReferences"] = ["gesture", "vulgar"],
        };

    // Phrases that globally mean "nothing from the category list applies".
    private static readonly string[] GlobalNegations =
    [
        "does not contain any",
        "no other categories",
        "none of the categories",
        "none of the other",
        "no content that falls into the categor",
        "none of the provided categories",
        "image does not contain any",
    ];

    // Compound negation phrases that are safe to search for as context-window prefixes.
    private static readonly string[] ContextNegationMarkers =
    [
        "no visible ", "no signs of ", "no sign of ", "no indication",
        "no explicit ", "no clear ", "no overt ", "no evidence",
        "not visible ", "not present ",
        "has not ", "is not ", "are not ", "does not ", "do not ",
        "cannot ", "without ",
        " no ", // catches "there is no [list including keyword]" patterns
        "nor ",  // catches "nor are there any depictions of"
    ];

    private static (HashSet<string> DetectedSubCategories, string Description) ParseResponse(string response, HashSet<string> knownCategories)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(response);
        }
        catch (JsonException)
        {
            // llava generated invalid/truncated JSON — skip this frame.
            return ([], string.Empty);
        }

        var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var description = string.Empty;

        using (json)
        {
            var root = json.RootElement;

            if (root.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
            {
                description = descProp.GetString() ?? string.Empty;
            }

            // New whitelist format: {"categories":["Cat.Name",...], "description":"..."}
            if (root.TryGetProperty("categories", out var catsProp) && catsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in catsProp.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var name = element.GetString();
                        if (!string.IsNullOrWhiteSpace(name) && knownCategories.Contains(name))
                        {
                            detected.Add(name);
                        }
                    }
                }
            }
            else
            {
                // Fallback: old boolean-per-field format in case model ignores new prompt
                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("description") || property.NameEquals("categories"))
                    {
                        continue;
                    }

                    var isTrue = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.String when string.Equals(property.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase) => true,
                        _ => false
                    };

                    if (isTrue && knownCategories.Contains(property.Name))
                    {
                        detected.Add(property.Name);
                    }
                }
            }
        }

        // Pass 1 — drop categories that the description explicitly negates.
        // llava frequently lists a category while writing "no signs of X" in its description.
        if (!string.IsNullOrWhiteSpace(description))
        {
            detected.RemoveWhere(cat => IsDescriptionNegatingCategory(description, cat));
        }

        // Pass 2 — require positive evidence.
        // If the description is substantive but contains zero keywords that could support a
        // category, the model almost certainly hallucinated it.
        if (!string.IsNullOrWhiteSpace(description))
        {
            detected.RemoveWhere(cat => !IsDescriptionCorroboratingCategory(description, cat));
        }

        return (detected, description);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the description contains at least one keyword that
    /// positively supports the given category, or when the description is too short to judge.
    /// A substantive description that mentions none of a category's confirmation keywords most
    /// likely means the model hallucinated that category.
    /// </summary>
    private static bool IsDescriptionCorroboratingCategory(string description, string category)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length < MinDescriptionLengthForConfirmation)
        {
            // Too brief to judge — keep the category.
            return true;
        }

        var group = FilterDictionary.GetGroup(category);
        if (!CategoryConfirmationKeywords.TryGetValue(group, out var keywords) || keywords.Length == 0)
        {
            // No confirmation list defined for this group — keep by default.
            return true;
        }

        var d = description.ToLowerInvariant();
        foreach (var kw in keywords)
        {
            if (d.Contains(kw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the description text clearly negates the given category,
    /// meaning the model's prose contradicts its own categories array.
    /// </summary>
    private static bool IsDescriptionNegatingCategory(string description, string category)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var d = description.ToLowerInvariant();

        // Global phrases that say nothing from the list applies.
        foreach (var g in GlobalNegations)
        {
            if (d.Contains(g, StringComparison.Ordinal))
            {
                return true;
            }
        }

        var group = FilterDictionary.GetGroup(category);
        if (!CategoryNegationKeywords.TryGetValue(group, out var keywords))
        {
            return false;
        }

        foreach (var kw in keywords)
        {
            // Direct prefix check: "no [keyword]", "no visible [keyword]", etc.
            // Covers the common case where the denial sits right before the word.
            foreach (var marker in ContextNegationMarkers)
            {
                if (d.Contains(marker + kw, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // Context-window check: scan for keyword occurrences and look back up to 60
            // characters (within the same sentence) for a negation marker.
            var idx = d.IndexOf(kw, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var ctxStart = Math.Max(0, idx - 60);
                var ctx = d[ctxStart..idx];

                // Don't cross a sentence boundary.
                var boundary = ctx.LastIndexOfAny(['.', '!', '?', ';', '\n']);
                if (boundary >= 0)
                {
                    ctx = ctx[(boundary + 1)..];
                }

                foreach (var marker in ContextNegationMarkers)
                {
                    if (ctx.Contains(marker, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                idx = d.IndexOf(kw, idx + 1, StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static string GetVisionBaseUrl()
    {
        var url = Plugin.Instance?.Configuration.OllamaBaseUrl;
        return string.IsNullOrWhiteSpace(url) ? "http://localhost:8000" : url;
    }

}

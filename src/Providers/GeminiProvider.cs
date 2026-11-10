using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayNext.Providers
{
    /// <summary>Google Gemini through a Google AI Studio API key (free tier available).</summary>
    public class GeminiProvider : IRecommendationProvider
    {
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";
        private const int MaxModelsToTry = 5;
        public const string DefaultModel = "gemini-3.6-flash";
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>API key → model that last worked, so later requests go straight to it.</summary>
        private static readonly Dictionary<string, string> workingModelCache = new Dictionary<string, string>();

        private readonly string apiKey;
        private readonly string configuredModel;
        private string usedModel;

        public GeminiProvider(string apiKey, string model)
        {
            this.apiKey = apiKey ?? string.Empty;
            configuredModel = (model ?? string.Empty).Trim();
            if (configuredModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                configuredModel = configuredModel.Substring("models/".Length);
            }
        }

        public string DisplayName => "Gemini";
        public string ModelName => usedModel ?? (configuredModel.Length > 0 ? configuredModel : "auto");

        // ---------- DTOs ----------
        private class ErrorEnvelope { public ErrorBody Error { get; set; } }
        private class ErrorBody { public int Code { get; set; } public string Message { get; set; } public string Status { get; set; } }
        private class ModelList { public List<ModelInfo> Models { get; set; } public string NextPageToken { get; set; } }
        private class ModelInfo { public string Name { get; set; } public List<string> SupportedGenerationMethods { get; set; } }
        private class GenerateResponse { public List<Candidate> Candidates { get; set; } public PromptFeedback PromptFeedback { get; set; } }
        private class PromptFeedback { public string BlockReason { get; set; } }
        private class Candidate { public Content Content { get; set; } public string FinishReason { get; set; } }
        private class Content { public List<Part> Parts { get; set; } }
        private class Part { public string Text { get; set; } public bool? Thought { get; set; } }

        /// <summary>This model can't be used with this key right now (not found, no free quota, no access). Try another one.</summary>
        private class ModelUnavailableException : ProviderException
        {
            public ModelUnavailableException(string message) : base(message) { }
        }

        private static void RequireKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ProviderException("No Gemini API key yet. Get a free one at aistudio.google.com/apikey and paste it in PlayNext's settings.");
            }
        }

        private static HttpRequestMessage NewRequest(HttpMethod method, string url, string key)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Add("x-goog-api-key", key);
            return req;
        }

        private static string ErrorMessage(string body)
        {
            if (Serialization.TryFromJson<ErrorEnvelope>(body, out var env) && env?.Error?.Message != null)
            {
                return Http.Shorten(env.Error.Message, 300);
            }
            return Http.Shorten(body, 300);
        }

        private static ProviderException ToFriendlyError(int status, string body, string model)
        {
            var message = ErrorMessage(body);
            if (status == 400 && message.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0)
                return new ProviderException("Gemini rejected the API key. Check it in PlayNext's settings (aistudio.google.com/apikey).");
            if (status == 404 || (status == 400 && (message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0)))
                return new ModelUnavailableException($"Model \"{model}\" isn't available: {message}");
            if (status == 403)
                return new ModelUnavailableException($"No access to \"{model}\" with this key: {message}");
            if (status == 429)
                return new ModelUnavailableException($"\"{model}\" hit its free-tier limit: {message}");
            if (status >= 500)
                return new ModelUnavailableException($"\"{model}\" is having trouble right now ({status}).");
            return new ProviderException($"Gemini error {status}: {message}");
        }

        // ---------- Models ----------

        private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

        public static async Task<List<string>> ListModelsAsync(string key)
        {
            RequireKey(key);
            var names = new List<string>();
            string pageToken = null;
            do
            {
                var url = BaseUrl + "models?pageSize=1000" + (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : string.Empty);
                using (var req = NewRequest(HttpMethod.Get, url, key))
                {
                    var reply = await Http.SendAsync(req, ListTimeout, CancellationToken.None, "Listing Gemini models").ConfigureAwait(false);
                    var body = reply.Body;
                    if (!reply.Ok)
                    {
                        var err = ToFriendlyError(reply.Status, body, "(model list)");
                        throw new ProviderException(err is ModelUnavailableException ? "Couldn't list Gemini models: " + ErrorMessage(body) : err.Message);
                    }
                    var list = Serialization.FromJson<ModelList>(body);
                    foreach (var m in list?.Models ?? new List<ModelInfo>())
                    {
                        if (m.SupportedGenerationMethods == null || !m.SupportedGenerationMethods.Contains("generateContent")) continue;
                        var name = m.Name?.StartsWith("models/") == true ? m.Name.Substring(7) : m.Name;
                        if (!string.IsNullOrEmpty(name) && name.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)) names.Add(name);
                    }
                    pageToken = string.IsNullOrEmpty(list?.NextPageToken) ? null : list.NextPageToken;
                }
            } while (pageToken != null);
            return names.Distinct().OrderBy(n => n).ToList();
        }

        private static readonly string[] Unsuitable = { "image", "tts", "live", "audio", "embedding", "native", "vision", "exp", "learnlm", "robotics", "computer-use", "8b", "pro" };

        private static double Version(string model)
        {
            var match = Regex.Match(model, @"gemini-(\d+(?:\.\d+)?)");
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static bool Has(string model, string part) => model.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Models to try, best first: stable Flash (newest first), "-latest" aliases, stable Flash-Lite, then previews.
        /// Google lists models a free key can't always use, so callers fall through this list on quota/access errors.
        /// </summary>
        public static List<string> RankModels(IEnumerable<string> models)
        {
            var usable = models.Where(m => !Unsuitable.Any(u => Has(m, u))).Distinct().ToList();
            Func<string, int> group = m =>
            {
                bool flash = Has(m, "flash"), lite = Has(m, "lite"), preview = Has(m, "preview"), latest = m.EndsWith("latest");
                if (flash && !lite && !preview && !latest) return 0;
                if (flash && !lite && latest) return 1;
                if (flash && lite && !preview) return 2;
                if (flash && !lite) return 3;
                if (flash) return 4;
                return 5;
            };
            var ranked = usable.OrderBy(group).ThenByDescending(Version).ThenBy(m => m.Length).ToList();
            // PlayNext's default: start with this model when the key can use it.
            if (ranked.Remove(DefaultModel)) ranked.Insert(0, DefaultModel);
            return ranked;
        }

        public static string PickDefaultModel(IEnumerable<string> models) => RankModels(models).FirstOrDefault();

        private async Task<List<string>> CandidateModelsAsync()
        {
            List<string> ranked;
            try
            {
                ranked = RankModels(await ListModelsAsync(apiKey).ConfigureAwait(false));
            }
            catch (ProviderException) when (configuredModel.Length > 0)
            {
                // Can't list models, but we know which one to use.
                return new List<string> { configuredModel };
            }
            if (configuredModel.Length > 0)
            {
                // The chosen model goes first; others are only a backup if it fails.
                ranked.Remove(configuredModel);
                ranked.Insert(0, configuredModel);
                return ranked.Take(MaxModelsToTry).ToList();
            }
            lock (workingModelCache)
            {
                if (workingModelCache.TryGetValue(apiKey, out var known))
                {
                    ranked.Remove(known);
                    ranked.Insert(0, known);
                }
            }
            if (ranked.Count == 0) throw new ProviderException("Couldn't find a usable Gemini model for this key. Set one manually in settings.");
            return ranked.Take(MaxModelsToTry).ToList();
        }

        // ---------- Generate ----------

        private static readonly object PickSchema = new
        {
            type = "OBJECT",
            properties = new
            {
                id = new { type = "INTEGER" },
                reason = new { type = "STRING" },
                mainHours = new { type = "NUMBER" },
                fullHours = new { type = "NUMBER" }
            },
            required = new[] { "id", "reason", "mainHours", "fullHours" },
            propertyOrdering = new[] { "id", "reason", "mainHours", "fullHours" }
        };

        // playerProfile comes first so it streams in before the long lists.
        private static readonly object ResponseSchema = new
        {
            type = "OBJECT",
            properties = new
            {
                playerProfile = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        headline = new { type = "STRING" },
                        summary = new { type = "STRING" },
                        traits = new { type = "ARRAY", items = new { type = "STRING" } }
                    },
                    required = new[] { "headline", "summary", "traits" },
                    propertyOrdering = new[] { "headline", "summary", "traits" }
                },
                lists = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            category = new { type = "STRING" },
                            recommendations = new { type = "ARRAY", items = PickSchema }
                        },
                        required = new[] { "category", "recommendations" },
                        propertyOrdering = new[] { "category", "recommendations" }
                    }
                }
            },
            required = new[] { "playerProfile", "lists" },
            propertyOrdering = new[] { "playerProfile", "lists" }
        };

        public Task<AiAnswer> GenerateAsync(string systemPrompt, string userPrompt, IProgress<string> progress, CancellationToken token)
        {
            return RunWithFallbackAsync(systemPrompt, userPrompt, 32768, GenerateFirst, GenerateIdle, GenerateTotal, MaxModelsToTry, progress, token);
        }

        // Answers are streamed, so the idle limit is "no data at all for this long" (thinking time included),
        // and the total limit caps one model attempt. Slow-but-working models are no longer cut off.
        // A model that sends nothing at all within GenerateFirst is skipped; once it's answering (thinking
        // summaries count), it may pause up to GenerateIdle between pieces.
        internal static TimeSpan GenerateFirst = TimeSpan.FromSeconds(25);
        internal static TimeSpan GenerateIdle = TimeSpan.FromSeconds(45);
        internal static TimeSpan GenerateTotal = TimeSpan.FromMinutes(6);
        internal static TimeSpan TestFirst = TimeSpan.FromSeconds(20);
        internal static TimeSpan TestIdle = TimeSpan.FromSeconds(20);
        internal static TimeSpan TestTotal = TimeSpan.FromSeconds(60);

        private async Task<AiAnswer> RunWithFallbackAsync(string systemPrompt, string userPrompt, int maxTokens, TimeSpan first, TimeSpan idle, TimeSpan total,
            int maxModels, IProgress<string> progress, CancellationToken token)
        {
            RequireKey(apiKey);
            progress?.Report("Connecting to Google and listing the models your key can use…");
            var candidates = (await CandidateModelsAsync().ConfigureAwait(false)).Take(maxModels).ToList();
            logger.Info("PlayNext: Gemini models to try: " + string.Join(", ", candidates));
            var failures = new List<string>();
            foreach (var model in candidates)
            {
                token.ThrowIfCancellationRequested();
                var prefix = failures.Count == 0 ? string.Empty : $"{failures.Count} model(s) didn't work. ";
                progress?.Report($"{prefix}Sent to {model}, waiting for it to start…");
                try
                {
                    var answer = await CallAsync(model, systemPrompt, userPrompt, maxTokens, first, idle, total, true, true, prefix, progress, token).ConfigureAwait(false);
                    usedModel = model;
                    lock (workingModelCache) workingModelCache[apiKey] = model;
                    return answer;
                }
                catch (Exception e) when (e is ModelUnavailableException || e is ProviderTimeoutException)
                {
                    logger.Warn("PlayNext: " + e.Message);
                    failures.Add(e.Message);
                    lock (workingModelCache)
                    {
                        if (workingModelCache.TryGetValue(apiKey, out var known) && known == model) workingModelCache.Remove(apiKey);
                    }
                }
            }

            var detail = string.Join("\n• ", failures);
            if (configuredModel.Length > 0)
            {
                throw new ProviderException($"Neither your chosen model ({configuredModel}) nor the backups worked right now.\n• {detail}\n\nGoogle's free tier is sometimes overloaded or rate limited. Wait a minute and try again, or pick another model in settings.");
            }
            throw new ProviderException($"None of the Gemini models worked with this key right now.\n• {detail}\n\nGoogle's free tier is sometimes overloaded or rate limited. Wait a minute and try again; daily limits reset at midnight Pacific time.");
        }

        private static int CountOccurrences(System.Text.StringBuilder sb, string what)
        {
            var text = sb.ToString();
            int count = 0, i = 0;
            while ((i = text.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { count++; i += what.Length; }
            return count;
        }

        private static bool SupportsThinkingLevel(string model) => Version(model) >= 3;

        private async Task<AiAnswer> CallAsync(string model, string systemPrompt, string userPrompt, int maxTokens, TimeSpan first, TimeSpan idle, TimeSpan total,
            bool useSchema, bool useThinkingConfig, string prefix, IProgress<string> progress, CancellationToken token)
        {
            var config = new Dictionary<string, object>
            {
                ["responseMimeType"] = "application/json",
                ["maxOutputTokens"] = maxTokens
            };
            if (useSchema) config["responseSchema"] = ResponseSchema;
            if (useThinkingConfig)
            {
                // Ranking a list doesn't need deep reasoning; less thinking = faster answers. includeThoughts streams
                // short thought summaries, which shows the model is alive while it thinks.
                if (SupportsThinkingLevel(model)) config["thinkingConfig"] = new { thinkingLevel = "low", includeThoughts = true };
                else if (Version(model) >= 2.5 && Has(model, "flash")) config["thinkingConfig"] = new { thinkingBudget = 1024, includeThoughts = true };
            }

            var body = new
            {
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
                generationConfig = config
            };

            var answer = new System.Text.StringBuilder();
            string finishReason = null, blockReason = null;
            bool sawThought = false;
            var lastReport = DateTime.MinValue;

            var url = BaseUrl + "models/" + Uri.EscapeDataString(model) + ":streamGenerateContent?alt=sse";
            using (var req = NewRequest(HttpMethod.Post, url, apiKey))
            {
                req.Content = Http.Json(body);
                var reply = await Http.StreamLinesAsync(req, first, idle, total, token, $"Asking {model}", line =>
                {
                    if (!line.StartsWith("data:")) return;
                    var json = line.Substring(5).Trim();
                    if (json.Length == 0 || json == "[DONE]") return;
                    if (!Serialization.TryFromJson<GenerateResponse>(json, out var chunk) || chunk == null) return;
                    if (!string.IsNullOrEmpty(chunk.PromptFeedback?.BlockReason)) blockReason = chunk.PromptFeedback.BlockReason;
                    var candidate = chunk.Candidates?.FirstOrDefault();
                    if (!string.IsNullOrEmpty(candidate?.FinishReason)) finishReason = candidate.FinishReason;
                    foreach (var part in candidate?.Content?.Parts ?? new List<Part>())
                    {
                        if (part.Thought == true) sawThought = true;
                        else if (!string.IsNullOrEmpty(part.Text)) answer.Append(part.Text);
                    }
                    if (progress != null && (DateTime.Now - lastReport).TotalMilliseconds > 400)
                    {
                        lastReport = DateTime.Now;
                        var listsDone = CountOccurrences(answer, "\"category\"");
                        progress.Report(answer.Length > 0
                            ? (listsDone > 0
                                ? $"{prefix}{model} is writing your lists… (on category {listsDone})"
                                : $"{prefix}{model} is describing your playstyle…")
                            : sawThought ? $"{prefix}{model} is thinking…" : $"{prefix}{model} started answering…");
                    }
                }).ConfigureAwait(false);

                if (!reply.Ok)
                {
                    if (reply.Status == 400)
                    {
                        var msg = ErrorMessage(reply.Body);
                        if (useThinkingConfig && (Has(msg, "thinking") || Has(msg, "includeThoughts")))
                            return await CallAsync(model, systemPrompt, userPrompt, maxTokens, first, idle, total, useSchema, false, prefix, progress, token).ConfigureAwait(false);
                        if (useSchema && (Has(msg, "schema") || Has(msg, "propertyOrdering")))
                            return await CallAsync(model, systemPrompt, userPrompt, maxTokens, first, idle, total, false, useThinkingConfig, prefix, progress, token).ConfigureAwait(false);
                    }
                    throw ToFriendlyError(reply.Status, reply.Body, model);
                }
            }

            logger.Info($"PlayNext: {model} finished ({finishReason ?? "no finish reason"}, {answer.Length} chars, thoughts: {sawThought}).");
            if (!string.IsNullOrEmpty(blockReason))
            {
                throw new ProviderException("Gemini declined the request (" + blockReason + "). Try again.");
            }
            var text = answer.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                if (finishReason == "MAX_TOKENS")
                    throw new ProviderException("Gemini ran out of output space before finishing. Try a shorter list.");
                throw new ModelUnavailableException($"\"{model}\" returned an empty answer ({finishReason ?? "no reason given"}).");
            }
            return Http.ParseAnswer(text);
        }

        public async Task<string> TestAsync(IProgress<string> progress)
        {
            await RunWithFallbackAsync(
                "Reply with JSON only.",
                "Return {\"playerProfile\":{\"headline\":\"ok\",\"summary\":\"ok\",\"traits\":[]},\"lists\":[{\"category\":\"Test\",\"recommendations\":[{\"id\":1,\"reason\":\"test\"}]}]}",
                4096, TestFirst, TestIdle, TestTotal, 3, progress, CancellationToken.None).ConfigureAwait(false);
            return $"Gemini works (model: {usedModel}).";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK.Data;

namespace PlayNext.Providers
{
    /// <summary>
    /// Anthropic Claude via the Messages API (raw HTTPS). The official C# SDK isn't used because Playnite
    /// loads add-ons into its own .NET Framework 4.6.2 process, where the SDK's newer System.Text.Json
    /// dependencies can't be redirected and would clash with Playnite's own copies.
    /// </summary>
    public class ClaudeProvider : IRecommendationProvider
    {
        private const string Url = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2026-10-21";
        private const string FallbackBeta = "server-side-fallback-2026-07-01";

        private readonly string apiKey;
        private readonly string model;

        public ClaudeProvider(string apiKey, string model)
        {
            this.apiKey = apiKey ?? string.Empty;
            this.model = string.IsNullOrWhiteSpace(model) ? "claude-opus-5-5" : model.Trim();
        }

        public string DisplayName => "Claude";
        public string ModelName => model;

        private class ErrorEnvelope { public ErrorBody Error { get; set; } }
        private class ErrorBody { public string Type { get; set; } public string Message { get; set; } }
        private class MessageResponse { public List<Block> Content { get; set; } public string Stop_reason { get; set; } }
        private class Block { public string Type { get; set; } public string Text { get; set; } }

        // Server-side refusal fallbacks ("default" mode) are available for these models on the Claude API.
        private bool SupportsDefaultFallback =>
            model.StartsWith("claude-opus-5", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("claude-sonnet-5-5", StringComparison.OrdinalIgnoreCase) ||
            model.StartsWith("claude-fable-5", StringComparison.OrdinalIgnoreCase);

        private static readonly object PickSchema = new
        {
            type = "object",
            properties = new
            {
                id = new { type = "integer" },
                reason = new { type = "string" },
                mainHours = new { type = "number" },
                fullHours = new { type = "number" }
            },
            required = new[] { "id", "reason", "mainHours", "fullHours" },
            additionalProperties = false
        };

        private static readonly object JsonSchema = new
        {
            type = "object",
            properties = new
            {
                playerProfile = new
                {
                    type = "object",
                    properties = new
                    {
                        headline = new { type = "string" },
                        summary = new { type = "string" },
                        traits = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "headline", "summary", "traits" },
                    additionalProperties = false
                },
                lists = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string" },
                            recommendations = new { type = "array", items = PickSchema }
                        },
                        required = new[] { "category", "recommendations" },
                        additionalProperties = false
                    }
                }
            },
            required = new[] { "playerProfile", "lists" },
            additionalProperties = false
        };

        public Task<AiAnswer> GenerateAsync(string systemPrompt, string userPrompt, IProgress<string> progress, CancellationToken token)
        {
            progress?.Report($"Waiting for Claude ({model})…");
            return CallAsync(systemPrompt, userPrompt, 20000, SupportsDefaultFallback, token);
        }

        private async Task<AiAnswer> CallAsync(string systemPrompt, string userPrompt, int maxTokens, bool withFallback, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ProviderException("No Claude API key yet. Create one at console.anthropic.com and paste it in PlayNext's settings.");
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["max_tokens"] = maxTokens,
                // The library listing is the big, stable part of the prompt; caching it makes
                // switching categories within a few minutes much cheaper.
                ["system"] = new object[]
                {
                    new { type = "text", text = systemPrompt, cache_control = new { type = "ephemeral" } }
                },
                ["messages"] = new object[] { new { role = "user", content = userPrompt } },
                ["output_config"] = new
                {
                    effort = "medium",
                    format = new { type = "json_schema", schema = JsonSchema }
                }
            };
            if (withFallback) body["fallbacks"] = "default";

            using (var req = new HttpRequestMessage(HttpMethod.Post, Url))
            {
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", ApiVersion);
                if (withFallback) req.Headers.Add("anthropic-beta", FallbackBeta);
                req.Content = Http.Json(body);

                var reply = await Http.SendAsync(req, TimeSpan.FromSeconds(maxTokens > 2000 ? 300 : 60), token, $"Asking {model}").ConfigureAwait(false);
                {
                    var text = reply.Body;
                    if (!reply.Ok)
                    {
                        var status = reply.Status;
                        string message = text;
                        if (Serialization.TryFromJson<ErrorEnvelope>(text, out var env) && env?.Error?.Message != null) message = env.Error.Message;

                        // If the account/model doesn't accept the fallback option, retry once without it.
                        if (withFallback && status == 400 && message.IndexOf("fallback", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return await CallAsync(systemPrompt, userPrompt, maxTokens, false, token).ConfigureAwait(false);
                        }
                        if (status == 401) throw new ProviderException("Claude rejected the API key. Check it in PlayNext's settings.");
                        if (status == 403) throw new ProviderException("This Claude API key isn't allowed to do that: " + Http.Shorten(message, 200));
                        if (status == 404) throw new ProviderException($"Claude model \"{model}\" wasn't found. Pick another model in settings.");
                        if (status == 429) throw new ProviderException("Claude rate limit reached. Wait a minute and try again.");
                        if (status == 529 || status >= 500) throw new ProviderException("Claude is overloaded or having trouble right now. Try again shortly.");
                        throw new ProviderException($"Claude error {status}: {Http.Shorten(message, 300)}");
                    }

                    var parsed = Serialization.FromJson<MessageResponse>(text);
                    if (parsed?.Stop_reason == "refusal")
                    {
                        throw new ProviderException("Claude declined this request. Try again or switch models.");
                    }
                    if (parsed?.Stop_reason == "max_tokens")
                    {
                        throw new ProviderException("Claude ran out of output space before finishing. Try a shorter list.");
                    }
                    var answerText = string.Concat((parsed?.Content ?? new List<Block>()).Where(b => b.Type == "text").Select(b => b.Text));
                    return Http.ParseAnswer(answerText);
                }
            }
        }

        public async Task<string> TestAsync(IProgress<string> progress)
        {
            progress?.Report($"Connecting to Claude ({model})…");
            await CallAsync("Reply with JSON only.", "Return a playerProfile with headline, summary \"ok\" and no traits, and one list with category \"Test\" holding one recommendation with id 1 and reason \"test\".", 1024, false, CancellationToken.None).ConfigureAwait(false);
            return $"Claude works (model: {model}).";
        }
    }
}

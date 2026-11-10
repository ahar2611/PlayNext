using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayNext.Providers
{
    public class AiPick
    {
        public int Id { get; set; }
        public string Reason { get; set; }
        /// <summary>Estimated hours for the main story (0 = no real ending).</summary>
        public double? MainHours { get; set; }
        /// <summary>Estimated hours to do everything (completionist).</summary>
        public double? FullHours { get; set; }
    }

    public class AiList
    {
        public string Category { get; set; }
        public List<AiPick> Recommendations { get; set; } = new List<AiPick>();
    }

    public class AiProfile
    {
        public string Headline { get; set; }
        public string Summary { get; set; }
        public List<string> Traits { get; set; } = new List<string>();
    }

    /// <summary>The AI's whole answer: a playstyle profile and one ranked list per category.</summary>
    public class AiAnswer
    {
        public AiProfile PlayerProfile { get; set; }
        public List<AiList> Lists { get; set; } = new List<AiList>();
    }

    public interface IRecommendationProvider
    {
        string DisplayName { get; }
        string ModelName { get; }
        Task<AiAnswer> GenerateAsync(string systemPrompt, string userPrompt, IProgress<string> progress, CancellationToken token);
        Task<string> TestAsync(IProgress<string> progress);
    }

    /// <summary>Thrown for problems the user can fix (bad key, quota, model name) so the UI can show the message as-is.</summary>
    public class ProviderException : Exception
    {
        public ProviderException(string message) : base(message) { }
    }

    /// <summary>The service didn't answer in time. Gemini treats this like an unavailable model and tries the next one.</summary>
    public class ProviderTimeoutException : ProviderException
    {
        public ProviderTimeoutException(string message) : base(message) { }
    }

    internal static class Http
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static HttpClient client;

        public static HttpClient Client
        {
            get
            {
                if (client == null)
                {
                    // Playnite runs on .NET Framework 4.6.2, which doesn't enable TLS 1.2 by default; Google and Anthropic require it.
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    // .NET Framework sends "Expect: 100-continue" on POSTs and waits for the server; skip that round trip.
                    ServicePointManager.Expect100Continue = false;
                    // Per-request timeouts are applied in SendAsync; this is only a last-resort cap.
                    client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    client.DefaultRequestHeaders.ExpectContinue = false;
                }
                return client;
            }
        }

        public class Reply
        {
            public int Status { get; set; }
            public string Body { get; set; }
            public bool Ok => Status >= 200 && Status < 300;
        }

        /// <summary>
        /// Sends a request with its own timeout, logs it (never the key), and turns network failures
        /// into readable <see cref="ProviderException"/>s instead of hanging or showing raw exceptions.
        /// </summary>
        public static async Task<Reply> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken userToken, string what)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var target = request.RequestUri.Host + request.RequestUri.AbsolutePath;
            logger.Info($"Play Next: {what}: {request.Method} {target} (timeout {timeout.TotalSeconds:0}s)…");
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(userToken))
            {
                timeoutCts.CancelAfter(timeout);
                try
                {
                    using (var resp = await Client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        logger.Info($"Play Next: {what}: HTTP {(int)resp.StatusCode} after {watch.Elapsed.TotalSeconds:0.0}s ({body.Length} chars).");
                        return new Reply { Status = (int)resp.StatusCode, Body = body };
                    }
                }
                catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
                {
                    logger.Warn($"Play Next: {what}: no answer after {watch.Elapsed.TotalSeconds:0}s.");
                    throw new ProviderTimeoutException($"{what}: {request.RequestUri.Host} didn't answer within {timeout.TotalSeconds:0} seconds. " +
                        "Check that a firewall, antivirus or VPN isn't blocking Playnite from the internet, then try again.");
                }
                catch (HttpRequestException e)
                {
                    var root = e.GetBaseException();
                    logger.Error(e, $"Play Next: {what}: connection to {request.RequestUri.Host} failed.");
                    var hint = root.Message.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 || root.Message.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0
                        ? " (secure connection failed; make sure Windows is up to date)"
                        : string.Empty;
                    throw new ProviderException($"{what}: couldn't connect to {request.RequestUri.Host}{hint}. {root.Message}");
                }
            }
        }

        /// <summary>
        /// Sends a request and reads the response line by line as it arrives (server-sent events).
        /// Gives up only if nothing arrives for <paramref name="idle"/>, or the whole thing exceeds <paramref name="total"/>.
        /// On an HTTP error the full body is returned instead of being streamed.
        /// </summary>
        /// <param name="firstData">How long to wait for the very first piece of the answer (a silent model is skipped quickly).</param>
        /// <param name="idle">Once data is flowing, the longest allowed pause between pieces.</param>
        public static async Task<Reply> StreamLinesAsync(HttpRequestMessage request, TimeSpan firstData, TimeSpan idle, TimeSpan total, CancellationToken userToken,
            string what, Action<string> onLine)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var host = request.RequestUri.Host;
            logger.Info($"Play Next: {what}: {request.Method} {host}{request.RequestUri.AbsolutePath} (streaming, first data within {firstData.TotalSeconds:0}s, pauses up to {idle.TotalSeconds:0}s, max {total.TotalSeconds:0}s)…");
            int lines = 0;
            bool gotHeaders = false;
            using (var totalCts = CancellationTokenSource.CreateLinkedTokenSource(userToken))
            using (var idleCts = new CancellationTokenSource())
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token, idleCts.Token))
            {
                totalCts.CancelAfter(total);
                idleCts.CancelAfter(firstData);
                try
                {
                    using (var resp = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false))
                    // .NET Framework stream reads ignore cancellation tokens, so cancel by closing the response.
                    using (linked.Token.Register(() => { try { resp.Dispose(); } catch { } }))
                    {
                        gotHeaders = true;
                        logger.Info($"Play Next: {what}: HTTP {(int)resp.StatusCode} headers after {watch.Elapsed.TotalSeconds:0.0}s.");
                        if (!resp.IsSuccessStatusCode)
                        {
                            var errorBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            return new Reply { Status = (int)resp.StatusCode, Body = errorBody };
                        }
                        using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                        {
                            while (true)
                            {
                                // Race every read against the timeout so a stalled connection can never hang us,
                                // even if closing the response doesn't interrupt the pending read.
                                var readTask = reader.ReadLineAsync();
                                var stopTask = Task.Delay(Timeout.Infinite, linked.Token);
                                if (await Task.WhenAny(readTask, stopTask).ConfigureAwait(false) != readTask)
                                {
                                    _ = readTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                                    throw new OperationCanceledException(linked.Token);
                                }
                                var line = await readTask.ConfigureAwait(false);
                                if (line == null) break;
                                idleCts.CancelAfter(idle);
                                lines++;
                                onLine(line);
                            }
                        }
                        linked.Token.ThrowIfCancellationRequested();
                        logger.Info($"Play Next: {what}: stream finished after {watch.Elapsed.TotalSeconds:0.0}s ({lines} lines).");
                        return new Reply { Status = (int)resp.StatusCode, Body = string.Empty };
                    }
                }
                catch (Exception e) when (linked.IsCancellationRequested && !(e is ProviderException))
                {
                    if (userToken.IsCancellationRequested) throw new OperationCanceledException(userToken);
                    string why;
                    if (!idleCts.IsCancellationRequested) why = $"was still going after {total.TotalSeconds:0} seconds";
                    else if (lines == 0) why = $"didn't respond within {firstData.TotalSeconds:0} seconds";
                    else why = $"stopped sending data for {idle.TotalSeconds:0} seconds";
                    logger.Warn($"Play Next: {what}: {why} (headers: {gotHeaders}, lines: {lines}, {watch.Elapsed.TotalSeconds:0}s).");
                    throw new ProviderTimeoutException($"{what}: {why}.");
                }
                catch (HttpRequestException e)
                {
                    var root = e.GetBaseException();
                    logger.Error(e, $"Play Next: {what}: connection to {host} failed.");
                    throw new ProviderException($"{what}: couldn't connect to {host}. {root.Message}");
                }
                catch (System.IO.IOException e)
                {
                    logger.Error(e, $"Play Next: {what}: connection to {host} dropped.");
                    throw new ProviderTimeoutException($"{what}: the connection to {host} dropped ({e.GetBaseException().Message}).");
                }
            }
        }

        public static StringContent Json(object body)
        {
            return new StringContent(Serialization.ToJson(body), Encoding.UTF8, "application/json");
        }

        /// <summary>Parses the model's JSON text, tolerating code fences or stray text around the object.</summary>
        public static AiAnswer ParseAnswer(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ProviderException("The AI returned an empty answer. Try again.");
            }
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                throw new ProviderException("The AI answer wasn't in the expected format. Try again.");
            }
            var json = text.Substring(start, end - start + 1);
            if (!Serialization.TryFromJson<AiAnswer>(json, out var answer) || answer?.Lists == null)
            {
                throw new ProviderException("The AI answer couldn't be read. Try again.");
            }
            return answer;
        }

        public static string Shorten(string s, int max = 400)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayNext.Providers
{
    /// <summary>Times from HowLongToBeat, in hours (0 = no data for that category).</summary>
    public class HltbEntry
    {
        public int HltbId { get; set; }
        public string MatchedName { get; set; }
        public double Main { get; set; }
        public double Plus { get; set; }
        public double Full { get; set; }
        public bool NotFound { get; set; }
        /// <summary>Read from the HowLongToBeat Playnite add-on's saved data rather than looked up by PlayNext.</summary>
        public bool FromAddon { get; set; }
        public DateTime Fetched { get; set; }
        public bool HasData => !NotFound && (Main > 0 || Full > 0 || Plus > 0);
        public string Url => HltbId > 0 ? "https://howlongtobeat.com/game/" + HltbId : null;
    }

    /// <summary>
    /// Looks up real completion times on howlongtobeat.com. The site has no official API, so this follows
    /// what its own web page does (find the search endpoint in its scripts, get a session token, search),
    /// caches every result on disk, and gives up quietly (falling back to AI estimates) if anything changes.
    /// </summary>
    public class HowLongToBeatClient
    {
        private const string BaseUrl = "https://howlongtobeat.com/";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Id of Lacro59's "HowLongToBeat" Playnite add-on; if it's installed, its saved times are used first.</summary>
        public const string HltbAddonId = "e08cd51f-9c9a-4ee3-a094-fde03b55492f";
        private readonly string addonDataDir;

        private readonly string cachePath;
        private readonly Dictionary<Guid, HltbEntry> cache;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        private string searchPath;
        private string authToken, authKey, authValue;
        private DateTime unavailableUntil = DateTime.MinValue;

        public HowLongToBeatClient(string dataDir)
        {
            cachePath = Path.Combine(dataDir, "howlongtobeat-cache.json");
            cache = LoadCache();
            try
            {
                var extensionsData = Directory.GetParent(dataDir)?.FullName;
                if (extensionsData != null) addonDataDir = Path.Combine(extensionsData, HltbAddonId, "HowLongToBeat");
                logger.Info("PlayNext: HowLongToBeat add-on data " + (Directory.Exists(addonDataDir) ? "found at " + addonDataDir : "not found; using online lookups."));
            }
            catch (Exception e)
            {
                logger.Warn(e, "PlayNext: couldn't locate HowLongToBeat add-on data.");
            }
        }

        // ---------- Cache ----------

        private Dictionary<Guid, HltbEntry> LoadCache()
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    var loaded = Serialization.FromJsonFile<Dictionary<Guid, HltbEntry>>(cachePath);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "PlayNext: couldn't read the HowLongToBeat cache.");
            }
            return new Dictionary<Guid, HltbEntry>();
        }

        private void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                Dictionary<Guid, HltbEntry> copy;
                lock (cache) copy = new Dictionary<Guid, HltbEntry>(cache);
                File.WriteAllText(cachePath, Serialization.ToJson(copy));
            }
            catch (Exception e)
            {
                logger.Warn(e, "PlayNext: couldn't save the HowLongToBeat cache.");
            }
        }

        // The HowLongToBeat add-on's per-game file: { "Items": [ { "Name", "Id", "Url", "GameHltbData": { ...seconds... } } ] }
        private class AddonFile { public List<AddonItem> Items { get; set; } }
        private class AddonItem
        {
            public string Name { get; set; }
            public string Id { get; set; }
            public AddonTimes GameHltbData { get; set; }
        }
        private class AddonTimes
        {
            public long MainStoryClassic { get; set; }
            public long MainStory { get; set; }
            public long MainExtraClassic { get; set; }
            public long MainExtra { get; set; }
            public long CompletionistClassic { get; set; }
            public long Completionist { get; set; }
        }

        /// <summary>Times the HowLongToBeat add-on already saved for this game, if any.</summary>
        public HltbEntry ReadFromAddon(Guid gameId)
        {
            if (addonDataDir == null) return null;
            try
            {
                var path = Path.Combine(addonDataDir, gameId + ".json");
                if (!File.Exists(path)) return null;
                var file = Serialization.FromJsonFile<AddonFile>(path);
                var item = file?.Items?.FirstOrDefault(i => i?.GameHltbData != null);
                if (item == null) return null;
                double Hours(long classic, long other) => Math.Round((classic > 0 ? classic : other) / 3600.0, 1);
                var t = item.GameHltbData;
                var entry = new HltbEntry
                {
                    MatchedName = item.Name,
                    HltbId = int.TryParse(item.Id, out var id) ? id : 0,
                    Main = Hours(t.MainStoryClassic, t.MainStory),
                    Plus = Hours(t.MainExtraClassic, t.MainExtra),
                    Full = Hours(t.CompletionistClassic, t.Completionist),
                    Fetched = File.GetLastWriteTime(path),
                    FromAddon = true
                };
                return entry.HasData ? entry : null;
            }
            catch (Exception e)
            {
                logger.Warn(e, $"PlayNext: couldn't read HowLongToBeat add-on data for {gameId}.");
                return null;
            }
        }

        /// <summary>
        /// Known times for a game: the HowLongToBeat add-on's data first, then PlayNext's own cache
        /// if it's fresh enough (found: 60 days, not found: 7 days).
        /// </summary>
        public HltbEntry GetCached(Guid gameId)
        {
            var fromAddon = ReadFromAddon(gameId);
            if (fromAddon != null) return fromAddon;
            lock (cache)
            {
                if (!cache.TryGetValue(gameId, out var e)) return null;
                var maxAge = e.NotFound ? TimeSpan.FromDays(7) : TimeSpan.FromDays(60);
                return DateTime.Now - e.Fetched < maxAge ? e : null;
            }
        }

        public void ClearCache()
        {
            lock (cache) cache.Clear();
            SaveCache();
        }

        public bool IsUnavailable => DateTime.Now < unavailableUntil;

        // ---------- Lookup ----------

        /// <summary>Finds a game's times. Returns null if HowLongToBeat can't be reached right now.</summary>
        public async Task<HltbEntry> LookupAsync(Guid gameId, string name, int? year, CancellationToken token)
        {
            var cached = GetCached(gameId);
            if (cached != null) return cached;
            if (IsUnavailable || string.IsNullOrWhiteSpace(name)) return null;

            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                cached = GetCached(gameId);
                if (cached != null) return cached;

                var entry = await FindAsync(name, year, token).ConfigureAwait(false);
                if (entry == null) return null; // service problem, not "not found"
                entry.Fetched = DateTime.Now;
                lock (cache) cache[gameId] = entry;
                SaveCache();
                logger.Info(entry.NotFound
                    ? $"PlayNext: HowLongToBeat has no match for \"{name}\"."
                    : $"PlayNext: HowLongToBeat \"{name}\" → \"{entry.MatchedName}\" ({entry.Main:0.#}h / {entry.Plus:0.#}h / {entry.Full:0.#}h).");
                // Be polite to the site: space out lookups a little.
                await Task.Delay(250, token).ConfigureAwait(false);
                return entry;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                logger.Warn(e, $"PlayNext: HowLongToBeat lookup failed for \"{name}\"; using AI estimates for a while.");
                unavailableUntil = DateTime.Now.AddMinutes(10);
                ResetSession();
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<HltbEntry> FindAsync(string name, int? year, CancellationToken token)
        {
            var full = CleanForSearch(name, false);
            var results = await SearchAsync(full, token).ConfigureAwait(false);
            var best = PickBest(results, name, year);
            if (best == null)
            {
                var stripped = CleanForSearch(name, true);
                if (!string.Equals(stripped, full, StringComparison.OrdinalIgnoreCase) && stripped.Length > 1)
                {
                    results = await SearchAsync(stripped, token).ConfigureAwait(false);
                    best = PickBest(results, name, year);
                }
            }
            if (best == null)
            {
                // Last try: drop a trailing word ("... Remake Intergrade", "... Online").
                var words = CleanForSearch(name, true).Split(' ');
                if (words.Length >= 3)
                {
                    results = await SearchAsync(string.Join(" ", words.Take(words.Length - 1)), token).ConfigureAwait(false);
                    best = PickBest(results, name, year);
                }
            }
            return best ?? new HltbEntry { NotFound = true };
        }

        // ---------- Session (endpoint + token) ----------

        private void ResetSession()
        {
            searchPath = null;
            authToken = authKey = authValue = null;
        }

        private static HttpRequestMessage Get(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Referer", BaseUrl);
            return req;
        }

        private async Task EnsureSessionAsync(CancellationToken token)
        {
            if (searchPath != null && authToken != null) return;

            if (searchPath == null)
            {
                searchPath = await DiscoverSearchPathAsync(token).ConfigureAwait(false) ?? "api/s";
                logger.Info("PlayNext: HowLongToBeat search endpoint: " + searchPath);
            }

            var ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            using (var req = Get($"{BaseUrl}{searchPath}/init?t={ms}"))
            {
                var reply = await Http.SendAsync(req, RequestTimeout, token, "HowLongToBeat session").ConfigureAwait(false);
                if (!reply.Ok) throw new InvalidOperationException($"HowLongToBeat session request returned {reply.Status}.");
                var json = Serialization.FromJson<Dictionary<string, object>>(reply.Body) ?? new Dictionary<string, object>();
                authToken = json.TryGetValue("token", out var t) ? t?.ToString() : null;
                foreach (var kv in json)
                {
                    var lower = kv.Key.ToLowerInvariant();
                    if (lower.Contains("key")) authKey = kv.Value?.ToString();
                    else if (lower.Contains("val")) authValue = kv.Value?.ToString();
                }
                if (string.IsNullOrEmpty(authToken)) throw new InvalidOperationException("HowLongToBeat didn't return a session token.");
            }
        }

        /// <summary>The search endpoint moves around; find the POST "/api/..." call in the site's scripts.</summary>
        private async Task<string> DiscoverSearchPathAsync(CancellationToken token)
        {
            string html;
            using (var req = Get(BaseUrl))
            {
                var reply = await Http.SendAsync(req, RequestTimeout, token, "HowLongToBeat home page").ConfigureAwait(false);
                if (!reply.Ok) throw new InvalidOperationException($"HowLongToBeat home page returned {reply.Status}.");
                html = reply.Body;
            }

            var scripts = Regex.Matches(html, @"src=""(/_next/static/chunks/[^""]+?\.js)""")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct()
                // The app/page bundles hold the search code; look at those first.
                .OrderBy(s => s.Contains("/pages/") ? 0 : 1)
                .Take(40)
                .ToList();
            var postFetch = new Regex(@"fetch\s*\(\s*[""'`]/api/([a-zA-Z0-9_/]+)[^""'`]*[""'`]\s*,\s*\{[^}]*method:\s*[""']POST[""'][^}]*\}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (var script in scripts)
            {
                using (var req = Get(BaseUrl.TrimEnd('/') + script))
                {
                    var reply = await Http.SendAsync(req, RequestTimeout, token, "HowLongToBeat script").ConfigureAwait(false);
                    if (!reply.Ok) continue;
                    var match = postFetch.Match(reply.Body);
                    if (match.Success) return "api/" + match.Groups[1].Value.TrimEnd('/');
                }
            }
            return null;
        }

        // ---------- Search ----------

        private class SearchResponse { public List<SearchGame> Data { get; set; } }
        private class SearchGame
        {
            public int Game_id { get; set; }
            public string Game_name { get; set; }
            public string Game_alias { get; set; }
            public double? Comp_main { get; set; }
            public double? Comp_plus { get; set; }
            public double? Comp_100 { get; set; }
            public int? Release_world { get; set; }
        }

        private async Task<List<SearchGame>> SearchAsync(string terms, CancellationToken token, bool retried = false)
        {
            await EnsureSessionAsync(token).ConfigureAwait(false);

            var payload = new Dictionary<string, object>
            {
                ["searchType"] = "games",
                ["searchTerms"] = terms.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries),
                ["searchPage"] = 1,
                ["size"] = 20,
                ["searchOptions"] = new
                {
                    games = new
                    {
                        userId = 0,
                        platform = "",
                        sortCategory = "popular",
                        rangeCategory = "main",
                        rangeTime = new { min = 0, max = 0 },
                        gameplay = new { perspective = "", flow = "", genre = "", difficulty = "" },
                        rangeYear = new { max = "", min = "" },
                        modifier = "hide_dlc"
                    },
                    users = new { sortCategory = "postcount" },
                    lists = new { sortCategory = "follows" },
                    filter = "",
                    sort = 0,
                    randomizer = 0
                },
                ["useCache"] = true
            };
            if (!string.IsNullOrEmpty(authKey) && authValue != null) payload[authKey] = authValue;

            using (var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + searchPath))
            {
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "*/*");
                req.Headers.TryAddWithoutValidation("Referer", BaseUrl);
                req.Headers.TryAddWithoutValidation("Origin", BaseUrl);
                req.Headers.TryAddWithoutValidation("x-auth-token", authToken ?? "");
                req.Headers.TryAddWithoutValidation("x-hp-key", authKey ?? "");
                req.Headers.TryAddWithoutValidation("x-hp-val", authValue ?? "");
                req.Content = new StringContent(Serialization.ToJson(payload), Encoding.UTF8, "application/json");

                var reply = await Http.SendAsync(req, RequestTimeout, token, "HowLongToBeat search").ConfigureAwait(false);
                if ((reply.Status == 401 || reply.Status == 403 || reply.Status == 404) && !retried)
                {
                    // Token expired or the endpoint moved: start a fresh session once.
                    ResetSession();
                    return await SearchAsync(terms, token, true).ConfigureAwait(false);
                }
                if (!reply.Ok) throw new InvalidOperationException($"HowLongToBeat search returned {reply.Status}.");
                var parsed = Serialization.FromJson<SearchResponse>(reply.Body);
                return parsed?.Data ?? new List<SearchGame>();
            }
        }

        // ---------- Matching ----------

        private static readonly Regex EditionWords = new Regex(
            @"\b(game of the year|goty|definitive|complete|deluxe|ultimate|enhanced|remastered|director'?s cut|anniversary|special|gold|premium|standard|collector'?s|digital|legendary|royal|final cut)\b(\s+edition)?|\bedition\b",
            RegexOptions.IgnoreCase);

        public static string CleanForSearch(string name, bool stripEditions)
        {
            var s = name ?? string.Empty;
            s = Regex.Replace(s, @"[™®©]", "");
            s = Regex.Replace(s, @"\([^)]*\)|\[[^\]]*\]", " ");
            if (stripEditions)
            {
                s = EditionWords.Replace(s, " ");
                s = Regex.Replace(s, @"[-–—:]\s*$", "");
            }
            s = Regex.Replace(s, @"[:\-–—_/]", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static string Normalize(string name)
        {
            var s = CleanForSearch(name, false).ToLowerInvariant();
            s = s.Replace("&", " and ");
            s = Regex.Replace(s, @"[^\p{L}\p{N} ]", "");
            // Treat roman numerals and digits the same ("Final Fantasy VII" = "Final Fantasy 7").
            s = Regex.Replace(s, @"\b(x{0,3})(ix|iv|v?i{0,3})\b", m =>
            {
                if (m.Value.Length == 0) return m.Value;
                var n = RomanToInt(m.Value);
                return n > 0 ? n.ToString(CultureInfo.InvariantCulture) : m.Value;
            });
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static int RomanToInt(string roman)
        {
            var map = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10 };
            int total = 0;
            for (int i = 0; i < roman.Length; i++)
            {
                if (!map.TryGetValue(roman[i], out var v)) return 0;
                var next = i + 1 < roman.Length && map.TryGetValue(roman[i + 1], out var nv) ? nv : 0;
                total += v < next ? -v : v;
            }
            return total;
        }

        public static double Similarity(string a, string b)
        {
            a = Normalize(a);
            b = Normalize(b);
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a == b) return 1;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
        }

        private static HltbEntry PickBest(List<SearchGame> results, string name, int? year)
        {
            if (results == null || results.Count == 0) return null;
            var strippedName = CleanForSearch(name, true);
            var scored = results.Select((g, index) =>
            {
                var sim = new[]
                {
                    Similarity(name, g.Game_name), Similarity(strippedName, g.Game_name),
                    string.IsNullOrWhiteSpace(g.Game_alias) ? 0 : g.Game_alias.Split(',').Max(alias => Similarity(name, alias.Trim()))
                }.Max();
                double score = sim;
                if (year.HasValue && g.Release_world.HasValue && g.Release_world.Value > 0)
                {
                    var gap = Math.Abs(year.Value - g.Release_world.Value);
                    score += gap == 0 ? 0.08 : gap == 1 ? 0.04 : gap > 5 ? -0.08 : 0;
                }
                score -= index * 0.005; // results are sorted by popularity
                return new { Game = g, Sim = sim, Score = score };
            })
            .Where(x => x.Sim >= 0.78)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
            if (scored == null) return null;
            var best = scored.Game;
            return new HltbEntry
            {
                HltbId = best.Game_id,
                MatchedName = best.Game_name,
                Main = Math.Round((best.Comp_main ?? 0) / 3600.0, 1),
                Plus = Math.Round((best.Comp_plus ?? 0) / 3600.0, 1),
                Full = Math.Round((best.Comp_100 ?? 0) / 3600.0, 1)
            };
        }
    }
}

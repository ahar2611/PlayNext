using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;
using PlayNext.Providers;

namespace PlayNext
{
    public class RecommendationService
    {
        public const string OverallCategory = "Overall";
        /// <summary>Every category is generated with this many picks, so switching between Top 10/15/25 is instant.</summary>
        public const int MaxCount = 25;
        /// <summary>Lists keep a few spares beyond 25, so "Not interested" doesn't leave the Top 25 short.</summary>
        public const int StoredCount = MaxCount + 5;
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly IPlayniteAPI api;
        private readonly string cachePath;

        /// <summary>The latest generated profile + lists (null until the first generation).</summary>
        public GenerationBatch Batch { get; private set; }

        /// <summary>The prompts from the most recent AI request (for debugging).</summary>
        internal string LastPrompts { get; private set; }

        /// <summary>Real completion times from howlongtobeat.com (cached).</summary>
        public HowLongToBeatClient Hltb { get; }

        public RecommendationService(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            Hltb = new HowLongToBeatClient(dataDir);
            cachePath = Path.Combine(dataDir, "recommendations-v3.json");
            Batch = LoadCache();
        }

        // ---------- Cache ----------

        private GenerationBatch LoadCache()
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    var loaded = Serialization.FromJsonFile<GenerationBatch>(cachePath);
                    if (loaded?.Lists != null)
                    {
                        if (loaded.Lengths == null) loaded.Lengths = new Dictionary<Guid, GameLength>();
                        loaded.Lists = new Dictionary<string, RecommendationResult>(loaded.Lists, StringComparer.OrdinalIgnoreCase);
                        return loaded;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "PlayNext: couldn't read cached recommendations.");
            }
            return null;
        }

        private void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                if (Batch == null)
                {
                    if (File.Exists(cachePath)) File.Delete(cachePath);
                }
                else
                {
                    File.WriteAllText(cachePath, Serialization.ToJson(Batch, true));
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "PlayNext: couldn't save recommendations.");
            }
        }

        public RecommendationResult GetList(string category)
        {
            if (Batch == null) return null;
            return Batch.Lists.TryGetValue(category ?? OverallCategory, out var r) ? r : null;
        }

        public void ClearCache()
        {
            Batch = null;
            SaveCache();
        }

        /// <summary>A fresh analysis of the current library (stats, genre mix) for the playstyle page.</summary>
        public LibraryAnalyzer CurrentAnalyzer()
        {
            return LibraryAnalyzer.Build(api);
        }

        public GameLength GetLength(Guid gameId)
        {
            return Batch?.Lengths != null && Batch.Lengths.TryGetValue(gameId, out var l) ? l : null;
        }

        // ---------- Providers ----------

        public IRecommendationProvider CreateProvider(PlayNextSettings s)
        {
            switch (s.Provider)
            {
                case AiProvider.Gemini: return new GeminiProvider(s.GeminiApiKey, s.GeminiModel);
                case AiProvider.Claude: return new ClaudeProvider(s.ClaudeApiKey, s.ClaudeModel);
                default: return new BuiltInProvider();
            }
        }

        private class BuiltInProvider : IRecommendationProvider
        {
            public string DisplayName => "Built-in";
            public string ModelName => "offline scoring";
            public Task<AiAnswer> GenerateAsync(string s, string u, IProgress<string> p, CancellationToken t) => throw new NotSupportedException();
            public Task<string> TestAsync(IProgress<string> p) => Task.FromResult("Built-in mode works offline. No key needed.");
        }

        // ---------- Generation ----------

        /// <summary>Category name + definition (null for Overall), in display order.</summary>
        private static List<KeyValuePair<string, CategoryDefinition>> AllCategories(PlayNextSettings settings)
        {
            var list = new List<KeyValuePair<string, CategoryDefinition>> { new KeyValuePair<string, CategoryDefinition>(OverallCategory, null) };
            list.AddRange(settings.Categories
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new KeyValuePair<string, CategoryDefinition>(c.Name.Trim(), c)));
            return list;
        }

        /// <summary>
        /// Makes the playstyle profile and a list for every category in one go (one AI request).
        /// Must be called on the UI thread (reads the Playnite DB).
        /// </summary>
        public Task<GenerationBatch> GenerateAllAsync(PlayNextSettings settings, IProgress<string> progress, CancellationToken token)
        {
            return GenerateAllAsync(LibraryAnalyzer.Build(api), settings, progress, token, null);
        }

        internal async Task<GenerationBatch> GenerateAllAsync(LibraryAnalyzer analyzer, PlayNextSettings settings,
            IProgress<string> progress, CancellationToken token, IRecommendationProvider providerOverride)
        {
            var count = MaxCount;
            var categories = AllCategories(settings);
            var eligible = analyzer.Eligible(settings).ToList();
            if (eligible.Count == 0)
            {
                throw new ProviderException("No games to recommend. Check the library filters in PlayNext's settings.");
            }

            var batch = new GenerationBatch { GeneratedAt = DateTime.Now, RequestedCount = count };

            if (settings.Provider == AiProvider.BuiltIn && providerOverride == null)
            {
                batch.ProviderName = "Built-in";
                batch.Model = "offline scoring";
                batch.Profile = analyzer.LocalProfile();
                foreach (var cat in categories)
                {
                    var keywords = cat.Value == null ? new List<string>() : LibraryAnalyzer.ParseKeywords(cat.Value.Keywords);
                    var ranked = eligible
                        .Select(g => new { Game = g, Score = analyzer.Score(g, keywords, out var reason), Reason = reason })
                        .Where(x => keywords.Count == 0 || LibraryAnalyzer.MatchesKeywords(x.Game, keywords, out _))
                        .OrderByDescending(x => x.Score)
                        .ToList();
                    var reasons = ranked.ToDictionary(x => x.Game.Id, x => x.Reason);
                    batch.Lists[cat.Key] = new RecommendationResult
                    {
                        Category = cat.Key,
                        GeneratedAt = batch.GeneratedAt,
                        RequestedCount = count,
                        ProviderName = batch.ProviderName,
                        Model = batch.Model,
                        Items = DiversifySeries(ranked.Select(x => x.Game).ToList(), StoredCount)
                            .Select(g => new StoredRecommendation { GameId = g.Id, Reason = reasons[g.Id] })
                            .ToList()
                    };
                }
            }
            else
            {
                var provider = providerOverride ?? CreateProvider(settings);
                var candidates = PickCandidates(analyzer, eligible, categories, settings.MaxGamesSentToAi);
                var systemPrompt = BuildSystemPrompt(analyzer, candidates, settings);
                var userPrompt = BuildUserPrompt(categories, count);
                LastPrompts = systemPrompt + "\n----- USER -----\n" + userPrompt;
                logger.Info($"PlayNext: asking {provider.DisplayName} for {categories.Count} categories with {candidates.Count} of {eligible.Count} games ({systemPrompt.Length + userPrompt.Length} chars).");
                var answer = await provider.GenerateAsync(systemPrompt, userPrompt, progress, token);

                batch.ProviderName = provider.DisplayName;
                batch.Model = provider.ModelName;
                batch.Profile = new PlayerProfile
                {
                    Headline = answer.PlayerProfile?.Headline?.Trim(),
                    Summary = answer.PlayerProfile?.Summary?.Trim(),
                    Traits = (answer.PlayerProfile?.Traits ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList()
                };

                var byShortId = candidates.ToDictionary(g => g.ShortId);
                foreach (var aiList in answer.Lists ?? new List<AiList>())
                {
                    var match = categories.FirstOrDefault(c => SameName(c.Key, aiList.Category));
                    if (match.Key == null || batch.Lists.ContainsKey(match.Key)) continue;
                    var result = new RecommendationResult
                    {
                        Category = match.Key,
                        GeneratedAt = batch.GeneratedAt,
                        RequestedCount = count,
                        ProviderName = batch.ProviderName,
                        Model = batch.Model
                    };
                    var seen = new HashSet<Guid>();
                    foreach (var pick in aiList.Recommendations ?? new List<AiPick>())
                    {
                        if (!byShortId.TryGetValue(pick.Id, out var game) || !seen.Add(game.Id)) continue;
                        result.Items.Add(new StoredRecommendation { GameId = game.Id, Reason = (pick.Reason ?? string.Empty).Trim() });
                        if (!batch.Lengths.ContainsKey(game.Id) && (pick.MainHours.HasValue || pick.FullHours.HasValue))
                        {
                            batch.Lengths[game.Id] = new GameLength
                            {
                                Main = CleanHours(pick.MainHours),
                                Full = Math.Max(CleanHours(pick.FullHours), CleanHours(pick.MainHours))
                            };
                        }
                        if (result.Items.Count >= count) break;
                    }
                    batch.Lists[match.Key] = result;
                }
                // Categories the AI skipped get an empty list, so the window says "no picks" rather than "not generated".
                foreach (var cat in categories.Where(c => !batch.Lists.ContainsKey(c.Key)))
                {
                    batch.Lists[cat.Key] = new RecommendationResult
                    {
                        Category = cat.Key, GeneratedAt = batch.GeneratedAt, RequestedCount = count,
                        ProviderName = batch.ProviderName, Model = batch.Model
                    };
                }
                if (batch.Lists.Values.All(l => l.Items.Count == 0))
                {
                    throw new ProviderException("The AI didn't return any games from your library. Try again.");
                }
                var aiCounts = string.Join(", ", batch.Lists.Select(kv => $"{kv.Key} {kv.Value.Items.Count}"));
                TopUpLists(batch, analyzer, eligible, categories);
                logger.Info("PlayNext: AI picks per list: " + aiCounts);
                logger.Info("PlayNext: got lists for " + string.Join(", ", batch.Lists.Select(kv => $"{kv.Key} ({kv.Value.Items.Count})")));
            }

            Batch = batch;
            SaveCache();
            return batch;
        }

        /// <summary>
        /// Fills lists the AI left short using the offline ranking: Overall from the whole library, a category
        /// only from games whose genres/tags match it (so Horror never gets padded with non-horror games).
        /// </summary>
        private static void TopUpLists(GenerationBatch batch, LibraryAnalyzer analyzer, List<GameSnapshot> eligible,
            List<KeyValuePair<string, CategoryDefinition>> categories)
        {
            foreach (var cat in categories)
            {
                if (!batch.Lists.TryGetValue(cat.Key, out var list) || list.Items.Count >= StoredCount) continue;
                var keywords = cat.Value == null ? new List<string>() : LibraryAnalyzer.ParseKeywords(cat.Value.Keywords);
                if (cat.Value != null && keywords.Count == 0) continue; // no way to tell what fits offline
                var have = new HashSet<Guid>(list.Items.Select(i => i.GameId));
                var extra = eligible
                    .Where(g => !have.Contains(g.Id) && (keywords.Count == 0 || LibraryAnalyzer.MatchesKeywords(g, keywords, out _)))
                    .Select(g => new { Game = g, Score = analyzer.Score(g, keywords, out var reason), Reason = reason })
                    .OrderByDescending(x => x.Score)
                    .Take(StoredCount - list.Items.Count)
                    .ToList();
                foreach (var x in extra) list.Items.Add(new StoredRecommendation { GameId = x.Game.Id, Reason = x.Reason });
            }
        }

        private static double CleanHours(double? h) =>
            !h.HasValue || double.IsNaN(h.Value) || h.Value < 0 || h.Value > 2000 ? -1 : Math.Round(h.Value, h.Value < 10 ? 1 : 0);

        private static string Normalize(string name) =>
            new string((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static bool SameName(string a, string b) => Normalize(a) == Normalize(b) && Normalize(a).Length > 0;

        /// <summary>
        /// For big libraries, choose which games the AI sees: each category's best keyword matches first
        /// (so every list has material), then the best-scoring games overall.
        /// </summary>
        private static List<GameSnapshot> PickCandidates(LibraryAnalyzer analyzer, List<GameSnapshot> eligible,
            List<KeyValuePair<string, CategoryDefinition>> categories, int max)
        {
            if (eligible.Count <= max) return eligible;
            var overall = eligible
                .Select(g => new { Game = g, Score = analyzer.Score(g, null, out _) })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Game)
                .ToList();
            var chosen = new List<GameSnapshot>();
            var seen = new HashSet<Guid>();
            var perCategory = Math.Max(10, max / (2 * Math.Max(1, categories.Count)));
            foreach (var cat in categories.Where(c => c.Value != null))
            {
                var keywords = LibraryAnalyzer.ParseKeywords(cat.Value.Keywords);
                if (keywords.Count == 0) continue;
                foreach (var g in overall.Where(g => LibraryAnalyzer.MatchesKeywords(g, keywords, out _)).Take(perCategory))
                {
                    if (seen.Add(g.Id)) chosen.Add(g);
                }
            }
            foreach (var g in overall)
            {
                if (chosen.Count >= max) break;
                if (seen.Add(g.Id)) chosen.Add(g);
            }
            return chosen.Take(max).ToList();
        }

        /// <summary>Avoid a list that's half one franchise: at most two entries per series in built-in mode.</summary>
        private static List<GameSnapshot> DiversifySeries(List<GameSnapshot> ordered, int count)
        {
            var perSeries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var output = new List<GameSnapshot>();
            foreach (var g in ordered)
            {
                var series = g.Series.FirstOrDefault();
                if (series != null)
                {
                    perSeries.TryGetValue(series, out var n);
                    if (n >= 2) continue;
                    perSeries[series] = n + 1;
                }
                output.Add(g);
                if (output.Count >= count) break;
            }
            return output;
        }

        // ---------- Prompts ----------

        private static string BuildSystemPrompt(LibraryAnalyzer analyzer, List<GameSnapshot> candidates, PlayNextSettings settings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are the recommendation engine inside PlayNext, an add-on for the Playnite game library manager.");
            sb.AppendLine("Your job: study the player's OWN library below, describe how they play, and pick what they should play next in several categories, using their habits, playtime, completion history and your own knowledge of each game (genre, tone, length, reputation).");
            sb.AppendLine();
            sb.AppendLine("How to read the library list: each line is");
            sb.AppendLine("id | name (year) | kind | genres | tags | series | status | hours played | last played (yyyy-MM) | my score | critics/community score | FAVORITE | installed");
            sb.AppendLine("Missing fields are omitted. \"kind\" is precomputed:");
            sb.AppendLine("- New: never really played (under 30 minutes).");
            sb.AppendLine("- Continue: started but not finished.");
            sb.AppendLine("- Revisit: finished/beaten, or abandoned, and could be worth going back to.");
            sb.AppendLine();
            sb.AppendLine("Ranking guidance:");
            sb.AppendLine("- Lean on demonstrated taste: hours played, finished games, favorites and the player's own scores matter more than critic scores.");
            sb.AppendLine("- Continue games with meaningful progress and recent activity are strong picks. Games abandoned quickly are weak picks unless there's a clear reason they'd land now.");
            sb.AppendLine("- Revisit only games the player clearly loved (lots of hours, favorite, high score) and hasn't touched in a while, or excellent games they dropped early. Don't suggest something they finished in the last few months.");
            sb.AppendLine("- If they finished an earlier entry in a series, the next unplayed entry is a great pick. Avoid filling a list with one franchise.");
            sb.AppendLine("- Mix in variety (New, Continue and Revisit) when it fits, but rank honestly.");
            sb.AppendLine("- Skip entries that aren't really games (soundtracks, tools, benchmarks, demos, DLC listed separately).");
            sb.AppendLine();
            sb.AppendLine("Reasons: ONE short sentence, about 8 to 18 words, in plain casual language, like a friend texting a suggestion. Talk to the player as \"you\".");
            sb.AppendLine("- Give one concrete reason it fits them: a game they loved that it resembles, where they left off, or the mood it suits.");
            sb.AppendLine("- No hype or marketing words (masterpiece, immersive, unforgettable, gripping), no lists of genres or tags, no semicolons or dashes, no spoilers.");
            sb.AppendLine("- Only mention hours or dates when it sounds natural (\"you're already 20 hours in\").");
            sb.AppendLine("- Good examples: \"You loved The Witcher 3, and this has the same big story and tough choices.\" / \"You were really into this last month, so finish it off.\" / \"A short, chill one for winding down after work.\"");
            sb.AppendLine("- When a game appears in more than one list, you can reuse or vary its reason.");
            sb.AppendLine();
            sb.AppendLine("Game length: for every pick also give mainHours (hours to beat the main story) and fullHours (hours to do everything, completionist), like HowLongToBeat. Use your best knowledge of the game, rounded to whole hours (one decimal under 10). Use 0 for both when the game has no real ending (MMOs, sandboxes, competitive multiplayer, endless roguelikes count their main run).");
            sb.AppendLine();
            sb.AppendLine("Player profile (playerProfile), written to the player as \"you\" in the same plain, friendly tone:");
            sb.AppendLine("- headline: 2 to 5 words that sum up their style, e.g. \"Story-first completionist\" or \"Roguelike grinder\". Not cheesy.");
            sb.AppendLine("- summary: 2 or 3 short sentences about how they play: what they love, how they treat their backlog, whether they finish or drop games, and any patterns you notice.");
            sb.AppendLine("- traits: 4 to 6 short observations (under 14 words each), each grounded in the data, e.g. \"You finish almost every RPG you start\" or \"Shooters rarely hold you past a few hours\".");
            sb.AppendLine();
            sb.AppendLine("Output: JSON only, shaped as {\"playerProfile\": {\"headline\": string, \"summary\": string, \"traits\": [string]}, \"lists\": [{\"category\": string, \"recommendations\": [{\"id\": number, \"reason\": string, \"mainHours\": number, \"fullHours\": number}]}]}. Each list is ordered best pick first. Use only ids from the library list.");
            if (!string.IsNullOrWhiteSpace(settings.NotesForAi))
            {
                sb.AppendLine();
                sb.AppendLine("Notes from the player (respect these):");
                sb.AppendLine(settings.NotesForAi.Trim());
            }
            sb.AppendLine();
            sb.AppendLine("=== Player stats ===");
            sb.AppendLine(analyzer.ProfileText.Trim());
            sb.AppendLine();
            sb.AppendLine($"=== Library ({candidates.Count} candidate games) ===");
            foreach (var g in candidates.OrderBy(g => g.ShortId))
            {
                sb.AppendLine(LibraryAnalyzer.ToPromptLine(g));
            }
            return sb.ToString();
        }

        private static string BuildUserPrompt(List<KeyValuePair<string, CategoryDefinition>> categories, int count)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Write the playerProfile, then make one list for each of these {categories.Count} categories, using these exact category names:");
            int i = 1;
            foreach (var cat in categories)
            {
                if (cat.Value == null)
                {
                    sb.AppendLine($"{i++}. {cat.Key}: any kind of game; whatever this player should play next.");
                    continue;
                }
                var line = $"{i++}. {cat.Key}";
                if (!string.IsNullOrWhiteSpace(cat.Value.Description)) line += ": " + cat.Value.Description.Trim();
                var keywords = LibraryAnalyzer.ParseKeywords(cat.Value.Keywords);
                if (keywords.Count > 0) line += " (related tags, as hints only: " + string.Join(", ", keywords.Take(10)) + ")";
                sb.AppendLine(line);
            }
            sb.AppendLine();
            sb.AppendLine($"Each list must have exactly {count} games, best pick first. Overall always has {count}. For a category, put the games that clearly fit first; if fewer than {count} clearly fit, continue with the closest matches (similar mood or gameplay). Only stop short if the library has no more games that are even loosely related. A game may appear in several lists. Use your own knowledge for games with missing or poor tags.");
            return sb.ToString();
        }
    }
}

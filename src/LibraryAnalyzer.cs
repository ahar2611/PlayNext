using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayNext
{
    /// <summary>
    /// Turns the Playnite database into snapshots, a taste profile, and a heuristic score per game.
    /// The heuristic powers the offline "Built-in" mode and decides which games to send to the AI
    /// when the library is larger than the configured limit.
    /// </summary>
    public class LibraryAnalyzer
    {
        public List<GameSnapshot> AllGames { get; private set; } = new List<GameSnapshot>();
        public Dictionary<string, double> TermAffinity { get; private set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public string ProfileText { get; private set; } = string.Empty;

        // ---------------- Snapshot ----------------

        public static LibraryAnalyzer Build(IPlayniteAPI api)
        {
            var list = new List<GameSnapshot>();
            int shortId = 1;
            foreach (var g in api.Database.Games.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(g.Name)) continue;
                var snap = new GameSnapshot
                {
                    ShortId = shortId++,
                    Id = g.Id,
                    Name = g.Name.Trim(),
                    Year = g.ReleaseDate?.Year,
                    Genres = g.Genres?.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                    Tags = g.Tags?.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                    Features = g.Features?.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                    Series = g.Series?.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                    Developers = g.Developers?.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>(),
                    Source = g.Source?.Name,
                    CompletionStatus = g.CompletionStatus?.Name,
                    Hours = g.Playtime / 3600.0,
                    PlayCount = g.PlayCount,
                    LastPlayed = g.LastActivity,
                    Added = g.Added,
                    UserScore = g.UserScore,
                    CriticScore = g.CriticScore,
                    CommunityScore = g.CommunityScore,
                    Favorite = g.Favorite,
                    Installed = g.IsInstalled,
                    Hidden = g.Hidden
                };
                list.Add(snap);
            }
            return FromSnapshots(list);
        }

        /// <summary>Analyzes already-captured games (also used by the offline test harness).</summary>
        public static LibraryAnalyzer FromSnapshots(List<GameSnapshot> games)
        {
            foreach (var snap in games)
            {
                snap.Status = ClassifyStatus(snap.CompletionStatus, snap.Hours);
                snap.Kind = ClassifyKind(snap);
            }
            var analyzer = new LibraryAnalyzer { AllGames = games };
            analyzer.ComputeAffinity();
            analyzer.ProfileText = analyzer.BuildProfileText();
            return analyzer;
        }

        public static StatusGroup ClassifyStatus(string name, double hours)
        {
            var n = (name ?? string.Empty).Trim().ToLowerInvariant();
            if (n.Length == 0) return hours < 0.25 ? StatusGroup.NotPlayed : StatusGroup.Unknown;
            if (n.Contains("abandon") || n.Contains("dropped") || n.Contains("gave up")) return StatusGroup.Abandoned;
            if (n.Contains("complete") || n.Contains("beaten") || n.Contains("finished") || n.Contains("100") || n.Contains("mastered") || n.Contains("done")) return StatusGroup.Finished;
            if (n.Contains("hold") || n.Contains("paused")) return StatusGroup.OnHold;
            if (n.Contains("plan") || n.Contains("backlog") || n.Contains("wishlist") || n.Contains("to play")) return StatusGroup.PlanToPlay;
            if (n.Contains("not played") || n.Contains("unplayed") || n.Contains("never")) return StatusGroup.NotPlayed;
            if (n.Contains("playing") || n.Contains("in progress") || n.Contains("current")) return StatusGroup.Playing;
            if (n.Contains("played")) return StatusGroup.Played;
            return StatusGroup.Unknown;
        }

        public static RecommendationKind ClassifyKind(GameSnapshot g)
        {
            if (g.Status == StatusGroup.Finished || g.Status == StatusGroup.Abandoned) return RecommendationKind.Revisit;
            if (g.Hours < 0.5 && g.Status != StatusGroup.Playing && g.Status != StatusGroup.OnHold) return RecommendationKind.New;
            return RecommendationKind.Continue;
        }

        // ---------------- Taste profile ----------------

        private static double StatusWeight(StatusGroup s)
        {
            switch (s)
            {
                case StatusGroup.Finished: return 1.3;
                case StatusGroup.Playing: return 1.2;
                case StatusGroup.OnHold: return 0.9;
                case StatusGroup.Abandoned: return 0.35;
                default: return 1.0;
            }
        }

        /// <summary>How much the player "invested" in a game: hours (log-scaled), status, favorite, own score.</summary>
        private static double Engagement(GameSnapshot g)
        {
            if (g.Hours < 1 && !g.Favorite && !(g.UserScore >= 70)) return 0;
            double w = Math.Log(1 + g.Hours) * StatusWeight(g.Status);
            if (g.Favorite) w = Math.Max(w, 1.5) * 1.5;
            if (g.UserScore.HasValue) w *= 0.5 + g.UserScore.Value / 100.0;
            return w;
        }

        private void ComputeAffinity()
        {
            var raw = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var libraryCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in AllGames)
            {
                foreach (var term in g.AllTerms.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    libraryCount[term] = libraryCount.TryGetValue(term, out var c) ? c + 1 : 1;
                }
                var w = Engagement(g);
                if (w <= 0) continue;
                foreach (var term in g.AllTerms.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    raw[term] = (raw.TryGetValue(term, out var v) ? v : 0) + w;
                }
            }

            // Dampen terms that are on nearly everything ("Single Player", "Indie"…) so they don't dominate.
            var adjusted = raw.ToDictionary(
                kv => kv.Key,
                kv => kv.Value / (1 + Math.Log(1 + libraryCount[kv.Key])),
                StringComparer.OrdinalIgnoreCase);
            var max = adjusted.Count > 0 ? adjusted.Values.Max() : 1;
            TermAffinity = adjusted.ToDictionary(kv => kv.Key, kv => max > 0 ? kv.Value / max : 0, StringComparer.OrdinalIgnoreCase);
        }

        public double GameAffinity(GameSnapshot g, out List<string> topTerms)
        {
            var scored = g.AllTerms
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(t => new { Term = t, A = TermAffinity.TryGetValue(t, out var a) ? a : 0 })
                .Where(x => x.A > 0)
                .OrderByDescending(x => x.A)
                .Take(3)
                .ToList();
            topTerms = scored.Select(x => x.Term).ToList();
            if (scored.Count == 0) return 0;
            return scored.Average(x => x.A) * (0.7 + 0.1 * scored.Count);
        }

        private string BuildProfileText()
        {
            var sb = new StringBuilder();
            var played = AllGames.Where(g => g.Hours >= 1).ToList();
            var totalHours = AllGames.Sum(g => g.Hours);
            int finished = AllGames.Count(g => g.Status == StatusGroup.Finished);
            int abandoned = AllGames.Count(g => g.Status == StatusGroup.Abandoned);
            int playing = AllGames.Count(g => g.Status == StatusGroup.Playing);
            int untouched = AllGames.Count(g => g.Hours < 0.5);

            sb.AppendLine($"Library: {AllGames.Count} games, {played.Count} played for 1h+, {untouched} essentially untouched, {totalHours:0} total hours.");
            sb.AppendLine($"Completion: {finished} finished/beaten, {abandoned} abandoned, {playing} currently playing.");
            var startedOrDone = finished + abandoned;
            if (startedOrDone > 0)
            {
                sb.AppendLine($"Finish rate among games they stopped playing: {100.0 * finished / startedOrDone:0}%.");
            }
            var finishedHours = AllGames.Where(g => g.Status == StatusGroup.Finished && g.Hours > 0).Select(g => g.Hours).OrderBy(h => h).ToList();
            if (finishedHours.Count >= 3)
            {
                sb.AppendLine($"Typical length of games they finish: median {finishedHours[finishedHours.Count / 2]:0}h.");
            }

            var genreHours = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in played)
            {
                foreach (var genre in g.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    genreHours[genre] = (genreHours.TryGetValue(genre, out var h) ? h : 0) + g.Hours;
                }
            }
            if (genreHours.Count > 0)
            {
                sb.AppendLine("Hours by genre: " + string.Join(", ", genreHours.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {kv.Value:0}h")) + ".");
            }

            var topTerms = TermAffinity.OrderByDescending(kv => kv.Value).Take(15).Select(kv => kv.Key).ToList();
            if (topTerms.Count > 0)
            {
                sb.AppendLine("Strongest taste signals (genres/tags weighted by engagement): " + string.Join(", ", topTerms) + ".");
            }

            var mostPlayed = AllGames.Where(g => g.Hours >= 1).OrderByDescending(g => g.Hours).Take(20).ToList();
            if (mostPlayed.Count > 0)
            {
                sb.AppendLine("Most played: " + string.Join("; ", mostPlayed.Select(g => $"{g.Name} ({g.Hours:0}h{StatusSuffix(g)})")) + ".");
            }

            var recent = AllGames.Where(g => g.LastPlayed.HasValue && g.LastPlayed.Value > DateTime.Now.AddDays(-90))
                .OrderByDescending(g => g.LastPlayed).Take(10).ToList();
            if (recent.Count > 0)
            {
                sb.AppendLine("Played in the last 90 days: " + string.Join("; ", recent.Select(g => $"{g.Name} ({Ago(g.LastPlayed.Value)}{StatusSuffix(g)})")) + ".");
            }

            var favorites = AllGames.Where(g => g.Favorite).Take(20).Select(g => g.Name).ToList();
            if (favorites.Count > 0)
            {
                sb.AppendLine("Favorites: " + string.Join("; ", favorites) + ".");
            }

            var loved = AllGames.Where(g => g.UserScore >= 80).OrderByDescending(g => g.UserScore).Take(15).ToList();
            if (loved.Count > 0)
            {
                sb.AppendLine("Rated highly by the player: " + string.Join("; ", loved.Select(g => $"{g.Name} ({g.UserScore})")) + ".");
            }

            var dropped = AllGames.Where(g => g.Status == StatusGroup.Abandoned).OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).Take(15).ToList();
            if (dropped.Count > 0)
            {
                sb.AppendLine("Abandoned: " + string.Join("; ", dropped.Select(g => $"{g.Name} ({g.Hours:0.#}h)")) + ".");
            }
            return sb.ToString();
        }

        private static string StatusSuffix(GameSnapshot g)
        {
            return string.IsNullOrEmpty(g.CompletionStatus) ? string.Empty : ", " + g.CompletionStatus;
        }

        // ---------------- Candidates & heuristic ----------------

        public static List<string> ParseKeywords(string keywords)
        {
            return (keywords ?? string.Empty)
                .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => k.Length > 0)
                .ToList();
        }

        public static bool MatchesKeywords(GameSnapshot g, List<string> keywords, out List<string> matched)
        {
            matched = new List<string>();
            if (keywords == null || keywords.Count == 0) return true;
            foreach (var term in g.AllTerms.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Whole-word match, so "rpg" doesn't match "MMORPG" and "party game" doesn't match "Party-Based".
                if (keywords.Any(k => Regex.IsMatch(term, @"(?<![\p{L}\p{N}])" + Regex.Escape(k) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)))
                {
                    matched.Add(term);
                }
            }
            return matched.Count > 0;
        }

        public IEnumerable<GameSnapshot> Eligible(PlayNextSettings s)
        {
            var dismissed = new HashSet<Guid>(s.DismissedGameIds ?? new List<Guid>());
            return AllGames
                .Where(g =>
                    (s.IncludeHidden || !g.Hidden) &&
                    (!s.OnlyInstalled || g.Installed) &&
                    (s.IncludeRevisits || g.Kind != RecommendationKind.Revisit) &&
                    !dismissed.Contains(g.Id))
                // The same game owned on several stores shows up more than once; keep the most-played/installed copy.
                .GroupBy(g => NormalizeName(g.Name))
                .Select(grp => grp.OrderByDescending(g => g.Hours).ThenByDescending(g => g.Installed).First());
        }

        private static string NormalizeName(string name)
        {
            var n = Regex.Replace(name.ToLowerInvariant(), @"[™®©:\-–—'’.,!]", " ");
            return Regex.Replace(n, @"\s+", " ").Trim();
        }

        /// <summary>Heuristic "should play next" score. Higher is better.</summary>
        public double Score(GameSnapshot g, List<string> keywords, out string reason)
        {
            var affinity = GameAffinity(g, out var topTerms);
            double quality = 0.6;
            var scores = new[] { g.CriticScore, g.CommunityScore }.Where(x => x.HasValue).Select(x => x.Value).ToList();
            if (scores.Count > 0) quality = scores.Average() / 100.0;

            // A finished game's own hours inflate the affinity of its genres, so discount that for revisits.
            if (g.Kind == RecommendationKind.Revisit) affinity *= 0.6;
            double score = 50 * affinity + 15 * quality;
            if (g.Favorite) score += 8;
            if (g.Installed) score += 3;
            if (g.UserScore.HasValue) score += (g.UserScore.Value - 60) / 5.0;

            var since = g.LastPlayed.HasValue ? DateTime.Now - g.LastPlayed.Value : (TimeSpan?)null;
            switch (g.Kind)
            {
                case RecommendationKind.Continue:
                    score += 12;
                    if (g.Status == StatusGroup.Playing) score += 6;
                    if (since.HasValue && since.Value.TotalDays < 60) score += 8;
                    else if (since.HasValue && since.Value.TotalDays > 730) score -= 6;
                    break;
                case RecommendationKind.New:
                    score += 6;
                    if (g.Status == StatusGroup.PlanToPlay) score += 8;
                    break;
                case RecommendationKind.Revisit:
                    if (g.Status == StatusGroup.Abandoned)
                    {
                        score -= 14;
                    }
                    else
                    {
                        score -= 8;
                        if (g.Hours >= 30 || g.Favorite || g.UserScore >= 80) score += 6;
                        if (since.HasValue && since.Value.TotalDays > 365) score += 4;
                        if (since.HasValue && since.Value.TotalDays < 180) score -= 20;
                    }
                    break;
            }

            if (keywords != null && keywords.Count > 0 && MatchesKeywords(g, keywords, out var matched))
            {
                score += 10;
                // In a category, explain the pick with the terms that put it there.
                topTerms = matched.Concat(topTerms).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            reason = BuildReason(g, topTerms, scores.Count > 0 ? (int?)Math.Round(scores.Average()) : null, since);
            return score;
        }

        /// <summary>One short, plain sentence, like a friend's suggestion.</summary>
        private string BuildReason(GameSnapshot g, List<string> topTerms, int? rating, TimeSpan? since)
        {
            // The well-liked game that has the most in common with this one, to compare against.
            if (g.Kind == RecommendationKind.New)
            {
                var seriesDone = AllGames.FirstOrDefault(o => o.Id != g.Id && SameSeries(o, g) && o.Status == StatusGroup.Finished);
                if (seriesDone != null)
                    return Pick(g, $"You finished {seriesDone.Name}, so this is a natural next one.", $"Since you beat {seriesDone.Name}, this one's an easy pick.");
            }
            var mine = new HashSet<string>(g.AllTerms, StringComparer.OrdinalIgnoreCase);
            GameSnapshot example = null;
            if (mine.Count > 0)
            {
                example = AllGames
                    .Where(o => o.Id != g.Id && o.Hours >= 3 && o.Status != StatusGroup.Abandoned && !SameSeries(o, g))
                    .Select(o =>
                    {
                        var theirs = new HashSet<string>(o.AllTerms, StringComparer.OrdinalIgnoreCase);
                        var shared = theirs.Count(t => mine.Contains(t));
                        var union = theirs.Count + mine.Count - shared;
                        return new { Game = o, Similarity = union == 0 ? 0 : (double)shared / union };
                    })
                    .Where(x => x.Similarity >= 0.3)
                    .OrderByDescending(x => x.Similarity * Math.Log(2 + Engagement(x.Game)))
                    .Select(x => x.Game)
                    .FirstOrDefault();
            }
            var daysSince = since?.TotalDays ?? double.MaxValue;
            bool loved = g.Favorite || g.UserScore >= 80 || g.Hours >= 30;

            switch (g.Kind)
            {
                case RecommendationKind.Continue:
                    if (daysSince < 45) return Pick(g, "You were just playing this, so keep it going.", "You've been into this lately. Jump back in.", "Still fresh in your mind, so keep the streak going.");
                    if (g.Hours >= 10) return Pick(g, $"You're already {g.Hours:0} hours in, so it's worth finishing.", $"{g.Hours:0} hours in is a lot to leave behind. See it through.");
                    return example != null
                        ? Pick(g, $"You barely started it, and it's a lot like {example.Name}.", $"You only scratched the surface, and fans of {example.Name} usually dig it.")
                        : "You barely started this one. Give it a proper go.";
                case RecommendationKind.New:
                    if (example != null) return Pick(g, $"Never started, and it's a lot like {example.Name}, which you enjoyed.", $"If you liked {example.Name}, this one should hit the spot.", $"Same kind of fun as {example.Name}, and you haven't tried it yet.");
                    if (rating >= 85) return Pick(g, "Never started, and people love this one.", "A well-loved game you haven't tried yet.");
                    return Pick(g, "It's been sitting in your backlog. Give it a try.", "Still waiting in your backlog. Maybe now's the time.");
                default:
                    if (g.Status == StatusGroup.Abandoned) return Pick(g, "You dropped it early, but it might click this time.", "It didn't grab you before, but it could be worth a second shot.");
                    if (loved && daysSince > 365) return Pick(g, "You loved this one and it's been a while. Time for another run.", "An old favorite you haven't touched in ages.");
                    if (loved) return "One of your favorites, and always good for another run.";
                    return "You finished it a while back. Could be fun to go again.";
            }
        }

        private static bool SameSeries(GameSnapshot a, GameSnapshot b) =>
            a.Series.Count > 0 && b.Series.Count > 0 && a.Series.Intersect(b.Series, StringComparer.OrdinalIgnoreCase).Any();

        /// <summary>Picks a phrasing variant per game so a list doesn't repeat the same sentence.</summary>
        private static string Pick(GameSnapshot g, params string[] options) => options[Math.Abs(g.Id.GetHashCode()) % options.Length];

        public static string JoinNice(IEnumerable<string> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return string.Empty;
            if (list.Count == 1) return list[0];
            return string.Join(", ", list.Take(list.Count - 1)) + " and " + list.Last();
        }

        public static string Ago(DateTime when)
        {
            var span = DateTime.Now - when;
            if (span.TotalDays < 1) return "today";
            if (span.TotalDays < 2) return "yesterday";
            if (span.TotalDays < 14) return $"{(int)span.TotalDays} days ago";
            if (span.TotalDays < 60) return $"{(int)(span.TotalDays / 7)} weeks ago";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} months ago";
            var years = span.TotalDays / 365.0;
            return years < 1.5 ? "a year ago" : $"{years:0} years ago";
        }

        // ---------------- Playstyle ----------------

        public class Stat
        {
            public string Value { get; set; }
            public string Label { get; set; }
            /// <summary>Segoe MDL2 Assets glyph.</summary>
            public string Icon { get; set; }
        }

        public class GenreShare
        {
            public string Name { get; set; }
            public string HoursText { get; set; }
            /// <summary>0..1, relative to the biggest genre (for bar widths).</summary>
            public double Share { get; set; }
            public string Percent { get; set; }
        }

        /// <summary>Top genres by hours played, for the "genre mix" bars.</summary>
        public List<GenreShare> GenreMix(int top = 6)
        {
            var genres = GenreHours();
            if (genres.Count == 0) return new List<GenreShare>();
            var total = AllGames.Where(g => g.Hours >= 1).Sum(g => g.Hours);
            var max = genres[0].Value;
            return genres.Take(top).Select(kv => new GenreShare
            {
                Name = kv.Key,
                HoursText = kv.Value.ToString("N0") + "h",
                Share = max > 0 ? Math.Max(0.04, kv.Value / max) : 0,
                Percent = total > 0 ? Math.Min(100, 100 * kv.Value / total).ToString("0") + "% of your playtime" : string.Empty
            }).ToList();
        }

        private List<KeyValuePair<string, double>> GenreHours()
        {
            var hours = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in AllGames.Where(x => x.Hours >= 1))
                foreach (var genre in g.Genres.Distinct(StringComparer.OrdinalIgnoreCase))
                    hours[genre] = (hours.TryGetValue(genre, out var h) ? h : 0) + g.Hours;
            return hours.OrderByDescending(kv => kv.Value).ToList();
        }

        /// <summary>Plain numbers about the library for the "Your playstyle" page.</summary>
        public List<Stat> Stats()
        {
            var stats = new List<Stat>();
            var played = AllGames.Count(g => g.Hours >= 1);
            var finished = AllGames.Count(g => g.Status == StatusGroup.Finished);
            var abandoned = AllGames.Count(g => g.Status == StatusGroup.Abandoned);
            var untouched = AllGames.Count(g => g.Hours < 0.5);
            stats.Add(new Stat { Value = AllGames.Count.ToString("N0"), Label = "games in your library", Icon = "\uE7FC" });
            stats.Add(new Stat { Value = AllGames.Sum(g => g.Hours).ToString("N0") + "h", Label = "played in total", Icon = "\uE823" });
            stats.Add(new Stat { Value = finished.ToString("N0"), Label = "finished", Icon = "\uE930" });
            stats.Add(new Stat { Value = untouched.ToString("N0"), Label = "never really played", Icon = "\uE8F1" });
            if (finished + abandoned > 0)
                stats.Add(new Stat { Value = (100.0 * finished / (finished + abandoned)).ToString("0") + "%", Label = "of games you stop end up finished", Icon = "\uE73E" });
            var finishedHours = AllGames.Where(g => g.Status == StatusGroup.Finished && g.Hours > 0).Select(g => g.Hours).OrderBy(h => h).ToList();
            if (finishedHours.Count >= 3)
                stats.Add(new Stat { Value = finishedHours[finishedHours.Count / 2].ToString("0") + "h", Label = "typical length of games you finish", Icon = "\uE916" });
            var top = GenreHours().FirstOrDefault();
            if (top.Key != null)
                stats.Add(new Stat { Value = top.Key, Label = $"your top genre ({top.Value:N0}h)", Icon = "\uE8EC" });
            var most = AllGames.OrderByDescending(g => g.Hours).FirstOrDefault(g => g.Hours >= 1);
            if (most != null)
                stats.Add(new Stat { Value = most.Name, Label = $"most played ({most.Hours:N0}h)", Icon = "\uE734" });
            var recent = AllGames.Count(g => g.LastPlayed.HasValue && g.LastPlayed.Value > DateTime.Now.AddDays(-30));
            stats.Add(new Stat { Value = recent.ToString("N0"), Label = "games played in the last 30 days", Icon = "\uE787" });
            if (played > 0 && AllGames.Count > 0)
                stats.Add(new Stat { Value = (100.0 * untouched / AllGames.Count).ToString("0") + "%", Label = "of your library is backlog", Icon = "\uE81C" });
            return stats;
        }

        /// <summary>Rule-based playstyle summary for the offline mode.</summary>
        public PlayerProfile LocalProfile()
        {
            var genres = GenreHours();
            var finished = AllGames.Count(g => g.Status == StatusGroup.Finished);
            var abandoned = AllGames.Count(g => g.Status == StatusGroup.Abandoned);
            var untouched = AllGames.Count(g => g.Hours < 0.5);
            double finishRate = finished + abandoned > 0 ? (double)finished / (finished + abandoned) : -1;
            double backlogShare = AllGames.Count > 0 ? (double)untouched / AllGames.Count : 0;
            var top = genres.Select(kv => kv.Key).Take(2).ToList();
            var totalHours = AllGames.Sum(g => g.Hours);
            var topShare = genres.Count > 0 && totalHours > 0 ? genres[0].Value / totalHours : 0;

            string style = finishRate >= 0.65 ? "finisher" : finishRate >= 0 && finishRate < 0.35 ? "sampler" : backlogShare > 0.6 ? "collector" : "explorer";
            string headline;
            switch (style)
            {
                case "finisher": headline = top.Count > 0 ? $"{top[0]} finisher" : "The finisher"; break;
                case "sampler": headline = top.Count > 0 ? $"{top[0]} sampler" : "The sampler"; break;
                case "collector": headline = "Big backlog collector"; break;
                default: headline = top.Count > 0 ? $"{top[0]} explorer" : "Curious explorer"; break;
            }

            var traits = new List<string>();
            if (top.Count > 0) traits.Add($"Most of your time goes into {JoinNice(top)} games.");
            if (finishRate >= 0) traits.Add(finishRate >= 0.65 ? "You usually see games through to the end." : finishRate < 0.35 ? "You try lots of games but drop many before the end." : "You finish some games and move on from others.");
            var finishedHours = AllGames.Where(g => g.Status == StatusGroup.Finished && g.Hours > 0).Select(g => g.Hours).OrderBy(h => h).ToList();
            if (finishedHours.Count >= 3)
            {
                var median = finishedHours[finishedHours.Count / 2];
                traits.Add(median >= 40 ? "You don't mind long games. Your finished ones run long." : median <= 12 ? "Shorter games are the ones you tend to finish." : "Mid-length games are your sweet spot.");
            }
            if (backlogShare > 0.4) traits.Add($"About {backlogShare * 100:0}% of your library is still unplayed.");
            if (AllGames.Count(g => g.Status == StatusGroup.Playing) >= 3) traits.Add("You like juggling a few games at once.");
            if (topShare > 0.5 && top.Count > 0) traits.Add($"{top[0]} games make up over half your playtime.");

            var summary = style == "finisher"
                ? "You pick games carefully and tend to finish what you start."
                : style == "sampler"
                    ? "You like trying lots of different games, and only some of them hold on to you."
                    : style == "collector"
                        ? "You've built up a big library, and a lot of it is still waiting for you."
                        : "You play a mix of things and follow whatever grabs you.";
            if (top.Count > 0) summary += $" {top[0]} is clearly your comfort zone.";
            return new PlayerProfile { Headline = headline, Summary = summary, Traits = traits };
        }

        // ---------------- Prompt line ----------------

        /// <summary>Compact one-line description of a game for the AI prompt.</summary>
        public static string ToPromptLine(GameSnapshot g)
        {
            var sb = new StringBuilder();
            sb.Append(g.ShortId).Append(" | ").Append(g.Name.Replace("|", "/"));
            if (g.Year.HasValue) sb.Append(" (").Append(g.Year.Value).Append(')');
            sb.Append(" | ").Append(g.Kind);
            if (g.Genres.Count > 0) sb.Append(" | genres: ").Append(string.Join(", ", g.Genres.Take(5)));
            if (g.Tags.Count > 0) sb.Append(" | tags: ").Append(string.Join(", ", g.Tags.Take(8)));
            if (g.Series.Count > 0) sb.Append(" | series: ").Append(string.Join(", ", g.Series.Take(2)));
            if (!string.IsNullOrEmpty(g.CompletionStatus)) sb.Append(" | status: ").Append(g.CompletionStatus);
            if (g.Hours >= 0.1) sb.Append(" | ").Append(g.Hours.ToString(g.Hours < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)).Append('h');
            if (g.LastPlayed.HasValue) sb.Append(" | last played ").Append(g.LastPlayed.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            if (g.UserScore.HasValue) sb.Append(" | my score ").Append(g.UserScore.Value);
            if (g.CriticScore.HasValue) sb.Append(" | critics ").Append(g.CriticScore.Value);
            else if (g.CommunityScore.HasValue) sb.Append(" | community ").Append(g.CommunityScore.Value);
            if (g.Favorite) sb.Append(" | FAVORITE");
            if (g.Installed) sb.Append(" | installed");
            return sb.ToString();
        }
    }
}

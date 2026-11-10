using System;
using System.Collections.Generic;
using Playnite.SDK;

namespace PlayNext
{
    public enum AiProvider
    {
        Gemini,
        Claude,
        BuiltIn
    }

    /// <summary>Why a game is on the list: never really started, in progress, or finished/dropped and worth another go.</summary>
    public enum AccentMode
    {
        /// <summary>One solid color picked in settings.</summary>
        Custom,
        /// <summary>Taken from the #1 game's artwork, falling back to the custom color.</summary>
        Artwork
    }

    /// <summary>Accent color presets and hex validation (no WPF types, so settings stay plain data).</summary>
    public static class AccentPresets
    {
        public static readonly IList<KeyValuePair<string, string>> All = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("PlayNext red", "#E60023"),
            new KeyValuePair<string, string>("Ember", "#F97316"),
            new KeyValuePair<string, string>("Crimson", "#E11D48"),
            new KeyValuePair<string, string>("Rose", "#EC4899"),
            new KeyValuePair<string, string>("Gold", "#EAB308"),
            new KeyValuePair<string, string>("Lime", "#84CC16"),
            new KeyValuePair<string, string>("Emerald", "#10B981"),
            new KeyValuePair<string, string>("Teal", "#14B8A6"),
            new KeyValuePair<string, string>("Ocean", "#0EA5E9"),
            new KeyValuePair<string, string>("Royal", "#3B82F6"),
            new KeyValuePair<string, string>("Steel", "#64748B")
        };

        public static bool IsValidHex(string hex) =>
            !string.IsNullOrWhiteSpace(hex) && System.Text.RegularExpressions.Regex.IsMatch(hex.Trim(), "^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");
    }

    public enum RecommendationKind
    {
        New,
        Continue,
        Revisit
    }

    /// <summary>A user-editable genre/mood category such as "Story-rich" or "Cozy &amp; Chill".</summary>
    public class CategoryDefinition : ObservableObject
    {
        private string name = string.Empty;
        public string Name { get => name; set => SetValue(ref name, value); }

        private string description = string.Empty;
        /// <summary>Plain-language description sent to the AI so it knows what fits.</summary>
        public string Description { get => description; set => SetValue(ref description, value); }

        private string keywords = string.Empty;
        /// <summary>Comma-separated genres/tags. Used as hints for the AI and as the filter in built-in mode.</summary>
        public string Keywords { get => keywords; set => SetValue(ref keywords, value); }

        public CategoryDefinition() { }

        public CategoryDefinition(string name, string description, string keywords)
        {
            this.name = name;
            this.description = description;
            this.keywords = keywords;
        }

        public static List<CategoryDefinition> Defaults()
        {
            return new List<CategoryDefinition>
            {
                new CategoryDefinition("Story-rich",
                    "Narrative-driven games with strong writing, memorable characters, or a gripping plot.",
                    "story rich, narrative, visual novel, adventure, choices matter, interactive fiction, mystery, detective, walking simulator, point & click"),
                new CategoryDefinition("RPGs",
                    "Role-playing games of any flavor: western RPGs, JRPGs, CRPGs, action RPGs, soulslikes.",
                    "rpg, role-playing, role playing, jrpg, crpg, action rpg, souls-like, soulslike, party-based, dungeon crawler"),
                new CategoryDefinition("Action & Combat",
                    "Games where the fun is in moment-to-moment combat: shooters, hack and slash, fighting, character action, roguelikes.",
                    "action, shooter, fps, third-person shooter, hack and slash, fighting, beat 'em up, character action, roguelike, roguelite, combat, bullet hell"),
                new CategoryDefinition("Cozy & Chill",
                    "Relaxing, low-stress games to unwind with: farming, life sims, gentle puzzles, building, wholesome vibes.",
                    "cozy, relaxing, casual, farming, life sim, wholesome, cute, puzzle, simulation, building, sandbox, colony sim, family friendly"),
                new CategoryDefinition("Strategy & Tactics",
                    "Games about planning and decisions: turn-based tactics, RTS, 4X, grand strategy, deckbuilders, city builders.",
                    "strategy, tactics, turn-based, turn based, rts, real-time strategy, 4x, grand strategy, card game, deckbuilding, deckbuilder, tower defense, city builder, management"),
                new CategoryDefinition("Horror",
                    "Scary and tense games: survival horror, psychological horror, thrillers.",
                    "horror, survival horror, psychological horror, thriller, dark, zombies"),
                new CategoryDefinition("Short & Sweet",
                    "Games that can be finished in a few evenings (roughly under 10 hours to beat).",
                    "short, indie, linear"),
                new CategoryDefinition("Multiplayer & Co-op",
                    "Games best played with other people: co-op, online multiplayer, party games.",
                    "multiplayer, co-op, coop, online co-op, local co-op, pvp, mmo, mmorpg, party game, split screen, online pvp")
            };
        }
    }

    /// <summary>One recommended game as stored in the cache. Rank = position in the list.</summary>
    public class StoredRecommendation
    {
        public Guid GameId { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>A generated list for one category, cached on disk.</summary>
    public class RecommendationResult
    {
        public string Category { get; set; }
        public DateTime GeneratedAt { get; set; }
        public string ProviderName { get; set; }
        public string Model { get; set; }
        public string Summary { get; set; }
        public int RequestedCount { get; set; }
        public List<StoredRecommendation> Items { get; set; } = new List<StoredRecommendation>();
    }

    /// <summary>What the AI (or the offline mode) says about how the player plays.</summary>
    public class PlayerProfile
    {
        public string Headline { get; set; }
        public string Summary { get; set; }
        public List<string> Traits { get; set; } = new List<string>();
    }

    /// <summary>Estimated hours to beat (HowLongToBeat-style). -1 = unknown, 0 = no real ending (MMOs, sandboxes…).</summary>
    public class GameLength
    {
        public double Main { get; set; } = -1;
        public double Full { get; set; } = -1;
    }

    /// <summary>One generation run: the playstyle profile plus a list for every category, made together.</summary>
    public class GenerationBatch
    {
        public DateTime GeneratedAt { get; set; }
        public string ProviderName { get; set; }
        public string Model { get; set; }
        public int RequestedCount { get; set; }
        public PlayerProfile Profile { get; set; }
        public Dictionary<string, RecommendationResult> Lists { get; set; } = new Dictionary<string, RecommendationResult>(StringComparer.OrdinalIgnoreCase);
        /// <summary>AI estimates of how long each recommended game takes to beat.</summary>
        public Dictionary<Guid, GameLength> Lengths { get; set; } = new Dictionary<Guid, GameLength>();
    }

    /// <summary>A flattened, thread-safe copy of the Playnite data we use for one game.</summary>
    public class GameSnapshot
    {
        public int ShortId { get; set; }
        public Guid Id { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Features { get; set; } = new List<string>();
        public List<string> Series { get; set; } = new List<string>();
        public List<string> Developers { get; set; } = new List<string>();
        public string Source { get; set; }
        public string CompletionStatus { get; set; }
        public StatusGroup Status { get; set; }
        public double Hours { get; set; }
        public ulong PlayCount { get; set; }
        public DateTime? LastPlayed { get; set; }
        public DateTime? Added { get; set; }
        public int? UserScore { get; set; }
        public int? CriticScore { get; set; }
        public int? CommunityScore { get; set; }
        public bool Favorite { get; set; }
        public bool Installed { get; set; }
        public bool Hidden { get; set; }
        public RecommendationKind Kind { get; set; }

        public IEnumerable<string> AllTerms
        {
            get
            {
                foreach (var g in Genres) yield return g;
                foreach (var t in Tags) yield return t;
                foreach (var f in Features) yield return f;
            }
        }
    }

    /// <summary>Playnite completion statuses are user-renamable, so we map them to a few meaningful groups by name.</summary>
    public enum StatusGroup
    {
        Unknown,
        NotPlayed,
        PlanToPlay,
        Playing,
        Played,
        OnHold,
        Finished,
        Abandoned
    }
}

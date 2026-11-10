using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayNext
{
    public class PlayNextSettings : ObservableObject
    {
        // ---------- AI provider ----------
        private AiProvider provider = AiProvider.Gemini;
        public AiProvider Provider { get => provider; set => SetValue(ref provider, value); }

        private string geminiApiKey = string.Empty;
        /// <summary>Kept in memory only; saved to disk encrypted (Windows DPAPI, current user) via <see cref="GeminiApiKeyProtected"/>.</summary>
        [DontSerialize]
        public string GeminiApiKey { get => geminiApiKey; set => SetValue(ref geminiApiKey, value?.Trim() ?? string.Empty); }

        public string GeminiApiKeyProtected
        {
            get => KeyProtector.Protect(GeminiApiKey);
            set => GeminiApiKey = KeyProtector.Unprotect(value);
        }

        private string geminiModel = Providers.GeminiProvider.DefaultModel;
        /// <summary>Tried first; other models are a backup. Empty = pick automatically.</summary>
        public string GeminiModel { get => geminiModel; set => SetValue(ref geminiModel, value?.Trim() ?? string.Empty); }

        private string claudeApiKey = string.Empty;
        [DontSerialize]
        public string ClaudeApiKey { get => claudeApiKey; set => SetValue(ref claudeApiKey, value?.Trim() ?? string.Empty); }

        public string ClaudeApiKeyProtected
        {
            get => KeyProtector.Protect(ClaudeApiKey);
            set => ClaudeApiKey = KeyProtector.Unprotect(value);
        }

        private string claudeModel = "claude-opus-5-5";
        public string ClaudeModel { get => claudeModel; set => SetValue(ref claudeModel, value?.Trim() ?? string.Empty); }

        // ---------- Library ----------
        private bool includeHidden = false;
        public bool IncludeHidden { get => includeHidden; set => SetValue(ref includeHidden, value); }

        private bool onlyInstalled = false;
        public bool OnlyInstalled { get => onlyInstalled; set => SetValue(ref onlyInstalled, value); }

        private bool includeRevisits = true;
        public bool IncludeRevisits { get => includeRevisits; set => SetValue(ref includeRevisits, value); }

        private int maxGamesSentToAi = 400;
        public int MaxGamesSentToAi { get => maxGamesSentToAi; set => SetValue(ref maxGamesSentToAi, Math.Max(50, Math.Min(3000, value))); }

        private string notesForAi = string.Empty;
        public string NotesForAi { get => notesForAi; set => SetValue(ref notesForAi, value ?? string.Empty); }

        private bool useHowLongToBeat = true;
        /// <summary>Look up real completion times on howlongtobeat.com (falls back to AI estimates).</summary>
        public bool UseHowLongToBeat { get => useHowLongToBeat; set => SetValue(ref useHowLongToBeat, value); }

        private AccentMode accentMode = AccentMode.Artwork;
        public AccentMode AccentMode { get => accentMode; set => SetValue(ref accentMode, value); }

        private string accentColor = "#E60023";
        /// <summary>Button/highlight color as #RRGGBB.</summary>
        public string AccentColor { get => accentColor; set => SetValue(ref accentColor, value?.Trim() ?? "#E60023"); }

        // 0 when missing from a saved file (older versions), so the upgrade below runs; fresh settings are set to 2.
        private int settingsVersion = 0;
        /// <summary>Used to update defaults in settings saved by older versions.</summary>
        public int SettingsVersion { get => settingsVersion; set => SetValue(ref settingsVersion, value); }

        private bool autoGenerate = true;
        /// <summary>Generate a list automatically when a category has none yet.</summary>
        public bool AutoGenerate { get => autoGenerate; set => SetValue(ref autoGenerate, value); }

        // ---------- Window / tray ----------
        private bool showTrayIcon = true;
        public bool ShowTrayIcon { get => showTrayIcon; set => SetValue(ref showTrayIcon, value); }

        private bool pinTrayIcon = true;
        /// <summary>Windows 11: show the icon next to the clock instead of under the ^ arrow.</summary>
        public bool PinTrayIcon { get => pinTrayIcon; set => SetValue(ref pinTrayIcon, value); }

        private bool trayPinDone = false;
        /// <summary>Set once the icon has been pinned, so a later manual "hide under the arrow" is respected.</summary>
        public bool TrayPinDone { get => trayPinDone; set => SetValue(ref trayPinDone, value); }

        private bool categoriesCollapsed = true;
        /// <summary>Category chips in the window show just one row.</summary>
        public bool CategoriesCollapsed { get => categoriesCollapsed; set => SetValue(ref categoriesCollapsed, value); }

        private bool trayHintShown = false;
        /// <summary>Whether the one-time "PlayNext is in your tray" notification was shown.</summary>
        public bool TrayHintShown { get => trayHintShown; set => SetValue(ref trayHintShown, value); }

        private bool closePopupOnFocusLoss = true;
        public bool ClosePopupOnFocusLoss { get => closePopupOnFocusLoss; set => SetValue(ref closePopupOnFocusLoss, value); }

        private string lastCategory = RecommendationService.OverallCategory;
        public string LastCategory { get => lastCategory; set => SetValue(ref lastCategory, value); }

        private int lastCount = 10;
        public int LastCount { get => lastCount; set => SetValue(ref lastCount, value); }

        // ---------- Categories / dismissed ----------
        // Starts empty on purpose: when settings are loaded, the JSON reader adds saved items to whatever
        // list is already here, so pre-filling defaults would duplicate them on every load/save.
        // Defaults are added by the view model when there are no categories at all.
        private ObservableCollection<CategoryDefinition> categories = new ObservableCollection<CategoryDefinition>();
        public ObservableCollection<CategoryDefinition> Categories { get => categories; set => SetValue(ref categories, value); }

        private List<Guid> dismissedGameIds = new List<Guid>();
        public List<Guid> DismissedGameIds { get => dismissedGameIds; set => SetValue(ref dismissedGameIds, value ?? new List<Guid>()); }
    }

    internal static class KeyProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PlayNext.ApiKey.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            try
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return string.Empty;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public class PlayNextSettingsViewModel : ObservableObject, ISettings
    {
        private readonly PlayNextPlugin plugin;
        private PlayNextSettings editingClone;

        private PlayNextSettings settings;
        public PlayNextSettings Settings
        {
            get => settings;
            set
            {
                if (settings != null) settings.PropertyChanged -= OnSettingsPropertyChanged;
                SetValue(ref settings, value);
                if (settings != null) settings.PropertyChanged += OnSettingsPropertyChanged;
            }
        }

        /// <summary>Picking a color (wheel, swatch or hex) means "use this color", so switch away from artwork mode.</summary>
        private void OnSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlayNextSettings.AccentColor) && settings.AccentMode != AccentMode.Custom)
            {
                settings.AccentMode = AccentMode.Custom;
                NotifyAccentMode();
            }
        }

        public IList<KeyValuePair<AiProvider, string>> ProviderOptions { get; } = new List<KeyValuePair<AiProvider, string>>
        {
            new KeyValuePair<AiProvider, string>(AiProvider.Gemini, "Google Gemini (free key from Google AI Studio)"),
            new KeyValuePair<AiProvider, string>(AiProvider.Claude, "Anthropic Claude (paid API key)"),
            new KeyValuePair<AiProvider, string>(AiProvider.BuiltIn, "Built-in (offline, no AI, no key needed)")
        };

        public IList<string> ClaudeModelOptions { get; } = new List<string> { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };

        private ObservableCollection<string> geminiModelOptions = new ObservableCollection<string>();
        public ObservableCollection<string> GeminiModelOptions { get => geminiModelOptions; set => SetValue(ref geminiModelOptions, value); }

        private string testStatus = string.Empty;
        public string TestStatus { get => testStatus; set => SetValue(ref testStatus, value); }

        private CategoryDefinition selectedCategory;
        public CategoryDefinition SelectedCategory { get => selectedCategory; set => SetValue(ref selectedCategory, value); }

        public string DismissedSummary => Settings.DismissedGameIds.Count == 0
            ? "No games dismissed."
            : $"{Settings.DismissedGameIds.Count} game(s) marked \"Not interested\".";

        public class AccentSwatch
        {
            public string Name { get; set; }
            public string Hex { get; set; }
        }

        public IList<AccentSwatch> AccentPresets { get; } = global::PlayNext.AccentPresets.All
            .Select(p => new AccentSwatch { Name = p.Key, Hex = p.Value }).ToList();

        public bool AccentCustom
        {
            get => Settings.AccentMode == AccentMode.Custom;
            set { if (value) { Settings.AccentMode = AccentMode.Custom; NotifyAccentMode(); } }
        }

        public bool AccentFromArtwork
        {
            get => Settings.AccentMode == AccentMode.Artwork;
            set { if (value) { Settings.AccentMode = AccentMode.Artwork; NotifyAccentMode(); } }
        }

        private void NotifyAccentMode()
        {
            OnPropertyChanged(nameof(AccentCustom));
            OnPropertyChanged(nameof(AccentFromArtwork));
        }

        public RelayCommand<string> SetAccentCommand { get; }
        public RelayCommand ClearHltbCacheCommand { get; }
        public RelayCommand AddCategoryCommand { get; }
        public RelayCommand RemoveCategoryCommand { get; }
        public RelayCommand ResetCategoriesCommand { get; }
        public RelayCommand ClearDismissedCommand { get; }
        public RelayCommand ClearCacheCommand { get; }
        public RelayCommand LoadGeminiModelsCommand { get; }
        public RelayCommand TestConnectionCommand { get; }
        public RelayCommand<string> OpenUrlCommand { get; }

        public PlayNextSettingsViewModel(PlayNextPlugin plugin)
        {
            this.plugin = plugin;
            var saved = plugin.LoadPluginSettings<PlayNextSettings>();
            Settings = saved ?? new PlayNextSettings { SettingsVersion = 2 };
            if (Settings.Categories == null || Settings.Categories.Count == 0)
            {
                Settings.Categories = new ObservableCollection<CategoryDefinition>();
            }
            else if (RemoveDuplicateCategories(Settings))
            {
                // Repair settings saved by older versions, which piled up copies of the categories.
                plugin.SavePluginSettings(Settings);
            }
            if (saved != null && UpgradeDefaults(saved, saved.SettingsVersion))
            {
                plugin.SavePluginSettings(Settings);
            }

            GeminiModelOptions.Add(string.Empty);
            if (!string.IsNullOrEmpty(Settings.GeminiModel)) GeminiModelOptions.Add(Settings.GeminiModel);

            SetAccentCommand = new RelayCommand<string>(hex =>
            {
                if (string.IsNullOrEmpty(hex)) return;
                Settings.AccentColor = hex;
            });
            ClearHltbCacheCommand = new RelayCommand(() =>
            {
                plugin.Service.Hltb.ClearCache();
                TestStatus = "HowLongToBeat times cleared; they'll be looked up again.";
            });
            AddCategoryCommand = new RelayCommand(() =>
            {
                var cat = new CategoryDefinition("New category", "Describe what kind of games belong here.", "");
                Settings.Categories.Add(cat);
                SelectedCategory = cat;
            });
            RemoveCategoryCommand = new RelayCommand(() =>
            {
                if (SelectedCategory != null) Settings.Categories.Remove(SelectedCategory);
                SelectedCategory = Settings.Categories.FirstOrDefault();
            });
            ResetCategoriesCommand = new RelayCommand(() =>
            {
                Settings.Categories = new ObservableCollection<CategoryDefinition>();
                SelectedCategory = Settings.Categories.FirstOrDefault();
            });
            ClearDismissedCommand = new RelayCommand(() =>
            {
                Settings.DismissedGameIds = new List<Guid>();
                OnPropertyChanged(nameof(DismissedSummary));
            });
            ClearCacheCommand = new RelayCommand(() =>
            {
                plugin.Service.ClearCache();
                TestStatus = "Saved lists cleared.";
            });
            LoadGeminiModelsCommand = new RelayCommand(async () =>
            {
                TestStatus = "Loading Gemini models…";
                try
                {
                    var models = await Providers.GeminiProvider.ListModelsAsync(Settings.GeminiApiKey);
                    var current = Settings.GeminiModel;
                    GeminiModelOptions.Clear();
                    GeminiModelOptions.Add(string.Empty);
                    foreach (var m in models) GeminiModelOptions.Add(m);
                    Settings.GeminiModel = current;
                    TestStatus = $"Found {models.Count} models. Leave the box empty to pick automatically ({Providers.GeminiProvider.PickDefaultModel(models) ?? "none found"}).";
                }
                catch (Exception e)
                {
                    TestStatus = "Couldn't load models: " + e.Message;
                }
            });
            TestConnectionCommand = new RelayCommand(async () =>
            {
                TestStatus = "Testing…";
                try
                {
                    var provider = plugin.Service.CreateProvider(Settings);
                    var started = DateTime.Now;
                    var progress = new Progress<string>(text => TestStatus = $"{text} ({(int)(DateTime.Now - started).TotalSeconds}s)");
                    TestStatus = await provider.TestAsync(progress);
                }
                catch (Exception e)
                {
                    TestStatus = "Failed: " + e.Message;
                }
            });
            OpenUrlCommand = new RelayCommand<string>(url =>
            {
                try { System.Diagnostics.Process.Start(url); } catch { }
            });
        }

        /// <summary>
        /// Applies new defaults to settings saved by older versions, but only where the old default was never changed.
        /// </summary>
        private static bool UpgradeDefaults(PlayNextSettings s, int fromVersion)
        {
            if (fromVersion >= 2) return false;
            // v2: Gemini 3.6 Flash as the default model, accent taken from game art.
            if (string.IsNullOrWhiteSpace(s.GeminiModel)) s.GeminiModel = Providers.GeminiProvider.DefaultModel;
            if (s.AccentMode == AccentMode.Custom && string.Equals(s.AccentColor, "#F97316", StringComparison.OrdinalIgnoreCase))
            {
                s.AccentMode = AccentMode.Artwork;
            }
            s.SettingsVersion = 2;
            return true;
        }

        /// <summary>Keeps one category per name (the last one saved, which holds the latest edits). True if anything was removed.</summary>
        private static bool RemoveDuplicateCategories(PlayNextSettings settings)
        {
            var list = settings.Categories.ToList();
            var lastIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++) lastIndex[(list[i].Name ?? string.Empty).Trim()] = i;
            var kept = list.Where((c, i) => lastIndex[(c.Name ?? string.Empty).Trim()] == i).ToList();
            if (kept.Count == list.Count) return false;
            // Keep the order in which each name first appeared.
            var order = list.Select(c => (c.Name ?? string.Empty).Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            settings.Categories = new ObservableCollection<CategoryDefinition>(
                order.Select(n => kept.First(c => string.Equals((c.Name ?? string.Empty).Trim(), n, StringComparison.OrdinalIgnoreCase))));
            return true;
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
            SelectedCategory = Settings.Categories.FirstOrDefault();
            OnPropertyChanged(nameof(DismissedSummary));
            NotifyAccentMode();
        }

        public void CancelEdit()
        {
            Settings = editingClone;
            NotifyAccentMode();
        }

        public void EndEdit()
        {
            // Turning "next to the clock" back on should pin again.
            if (Settings.PinTrayIcon && editingClone != null && !editingClone.PinTrayIcon) Settings.TrayPinDone = false;
            // Re-showing a hidden icon should also re-pin it.
            if (Settings.ShowTrayIcon && editingClone != null && !editingClone.ShowTrayIcon) Settings.TrayPinDone = false;
            plugin.SavePluginSettings(Settings);
            plugin.OnSettingsChanged();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            var names = Settings.Categories.Select(c => (c.Name ?? string.Empty).Trim()).ToList();
            if (names.Any(string.IsNullOrEmpty))
            {
                errors.Add("Every category needs a name.");
            }
            if (names.Where(n => n.Length > 0).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            {
                errors.Add("Category names must be unique.");
            }
            if (!global::PlayNext.AccentPresets.IsValidHex(Settings.AccentColor))
            {
                errors.Add("The accent color should look like #F97316.");
            }
            if (names.Any(n => string.Equals(n, RecommendationService.OverallCategory, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"\"{RecommendationService.OverallCategory}\" is built in; please use a different category name.");
            }
            return errors.Count == 0;
        }

        /// <summary>Saves settings changed outside the settings dialog (last category, dismissed games…).</summary>
        public void SaveQuietly()
        {
            plugin.SavePluginSettings(Settings);
        }
    }
}

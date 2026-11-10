using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayNext.Providers;

namespace PlayNext.UI
{
    public class RecommendationItemViewModel : ObservableObject
    {
        private int rank;
        public int Rank { get => rank; set { SetValue(ref rank, value); OnPropertyChanged(nameof(RankText)); } }
        public string RankText => "#" + Rank;
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public string Reason { get; set; }

        public string KindLabel { get; set; }
        public Brush KindBrush { get; set; }
        public Brush KindSoftBrush { get; set; }

        public ImageSource Cover { get; set; }
        public ImageSource Background { get; set; }
        public Visibility CoverVisibility => Cover == null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PlaceholderVisibility => Cover == null ? Visibility.Visible : Visibility.Collapsed;
        public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

        /// <summary>"2019 · RPG, Adventure"</summary>
        public string MetaLine { get; set; }
        /// <summary>"14h played · 3 weeks ago" / "Not started yet"</summary>
        public string PlayedLine { get; set; }
        public string Score { get; set; }
        public Visibility ScoreVisibility => string.IsNullOrEmpty(Score) ? Visibility.Collapsed : Visibility.Visible;
        public string Source { get; set; }
        public Visibility SourceVisibility => string.IsNullOrEmpty(Source) ? Visibility.Collapsed : Visibility.Visible;
        public bool Installed { get; set; }
        public Visibility InstalledVisibility => Installed ? Visibility.Visible : Visibility.Collapsed;

        // Inputs for length/progress
        public string LookupName { get; set; }
        public int? Year { get; set; }
        public double HoursPlayed { get; set; }
        public RecommendationKind Kind { get; set; }
        public bool HasRealLength { get; private set; }

        // How long to beat (HowLongToBeat, or AI estimates as a fallback)
        private string mainLength;
        public string MainLength { get => mainLength; set => SetValue(ref mainLength, value); }
        private string fullLength;
        public string FullLength { get => fullLength; set => SetValue(ref fullLength, value); }
        private string lengthSource;
        /// <summary>"HLTB" or "AI est."</summary>
        public string LengthSource { get => lengthSource; set => SetValue(ref lengthSource, value); }
        private string lengthTooltip;
        public string LengthTooltip { get => lengthTooltip; set => SetValue(ref lengthTooltip, value); }
        private Visibility lengthVisibility = Visibility.Collapsed;
        public Visibility LengthVisibility { get => lengthVisibility; set => SetValue(ref lengthVisibility, value); }
        private Visibility endlessVisibility = Visibility.Collapsed;
        public Visibility EndlessVisibility { get => endlessVisibility; set => SetValue(ref endlessVisibility, value); }
        private Visibility lookupVisibility = Visibility.Collapsed;
        /// <summary>Shown while HowLongToBeat is being checked for this game.</summary>
        public Visibility LookupVisibility { get => lookupVisibility; set => SetValue(ref lookupVisibility, value); }

        // Progress through the main story (Continue games with a known length)
        private double progress;
        public double Progress { get => progress; set => SetValue(ref progress, value); }
        private string progressText;
        public string ProgressText { get => progressText; set => SetValue(ref progressText, value); }
        private Visibility progressVisibility = Visibility.Collapsed;
        public Visibility ProgressVisibility { get => progressVisibility; set => SetValue(ref progressVisibility, value); }

        /// <summary>Shows completion times. <paramref name="real"/> = from HowLongToBeat (not an estimate).</summary>
        public void ApplyLength(double main, double plus, double full, bool real, string url = null)
        {
            HasRealLength = real;
            LookupVisibility = Visibility.Collapsed;
            var prefix = real ? string.Empty : "~";
            if (main <= 0 && full <= 0)
            {
                LengthVisibility = Visibility.Collapsed;
                EndlessVisibility = real ? Visibility.Collapsed : Visibility.Visible;
                ProgressVisibility = Visibility.Collapsed;
                return;
            }
            EndlessVisibility = Visibility.Collapsed;
            MainLength = main > 0 ? prefix + FormatHours(main) : "?";
            FullLength = full > 0 ? prefix + FormatHours(full) : "?";
            LengthSource = real ? "HLTB" : "AI est.";
            LengthTooltip = real
                ? $"HowLongToBeat: main story {Describe(main)}, main + extras {Describe(plus)}, completionist {Describe(full)}"
                : "Estimated by the AI (HowLongToBeat had no data for this game)";
            LengthVisibility = Visibility.Visible;

            if (Kind == RecommendationKind.Continue && HoursPlayed >= 0.5 && main > 0)
            {
                var pct = HoursPlayed / main;
                Progress = Math.Max(0.03, Math.Min(1, pct));
                ProgressText = pct >= 1
                    ? "You've played longer than the main story usually takes"
                    : $"About {pct * 100:0}% through the main story · ~{FormatHours(main - HoursPlayed)} to go";
                ProgressVisibility = Visibility.Visible;
            }
            else
            {
                ProgressVisibility = Visibility.Collapsed;
            }
        }

        private static string Describe(double h) => h > 0 ? FormatHours(h) : "n/a";

        public static string FormatHours(double h)
        {
            if (h < 1) return $"{Math.Max(1, Math.Round(h * 60)):0}m";
            return h < 10 ? $"{h:0.#}h" : $"{h:0}h";
        }
    }

    public class RecommendationsViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Color NewColor = Color.FromRgb(0x34, 0xD3, 0x99);
        private static readonly Color ContinueColor = Color.FromRgb(0x60, 0xA5, 0xFA);
        private static readonly Color RevisitColor = Color.FromRgb(0xFB, 0xBF, 0x24);

        private readonly PlayNextPlugin plugin;
        private CancellationTokenSource generationCts;
        private int generationVersion;
        private bool suppressReload;

        public RecommendationsViewModel(PlayNextPlugin plugin)
        {
            this.plugin = plugin;
            RegenerateCommand = new RelayCommand(() => Generate());
            PlayCommand = new RelayCommand<RecommendationItemViewModel>(item =>
            {
                if (item == null) return;
                RequestHide?.Invoke();
                plugin.PlayniteApi.StartGame(item.GameId);
            });
            ShowCommand = new RelayCommand<RecommendationItemViewModel>(item =>
            {
                if (item == null) return;
                RequestHide?.Invoke();
                plugin.ShowGameInLibrary(item.GameId);
            });
            DismissCommand = new RelayCommand<RecommendationItemViewModel>(Dismiss);
            SettingsCommand = new RelayCommand(() =>
            {
                RequestHide?.Invoke();
                plugin.OpenSettings();
            });
            CancelCommand = new RelayCommand(() => generationCts?.Cancel());
            ShowListCommand = new RelayCommand(() => ShowPlaystyle = false);
            ShowPlaystyleCommand = new RelayCommand(() => ShowPlaystyle = true);
            ToggleChipsCommand = new RelayCommand(() => ChipsCollapsed = !ChipsCollapsed);
        }

        public event Action RequestHide;
        /// <summary>Raised after the visible list changes, so the window can play its entrance animation.</summary>
        public event Action ListChanged;

        // ---------- Bindable state ----------

        public ObservableCollection<string> Categories { get; } = new ObservableCollection<string>();
        public IList<int> Counts { get; } = new List<int> { 10, 15, 25 };

        private RecommendationItemViewModel featured;
        public RecommendationItemViewModel Featured
        {
            get => featured;
            set { SetValue(ref featured, value); OnPropertyChanged(nameof(FeaturedVisibility)); }
        }
        public Visibility FeaturedVisibility => Featured == null ? Visibility.Collapsed : Visibility.Visible;
        public ObservableCollection<RecommendationItemViewModel> Rest { get; } = new ObservableCollection<RecommendationItemViewModel>();

        private Color accentColor = AccentColors.Fallback;
        /// <summary>Button/highlight color: the custom color, or one taken from the #1 game's art.</summary>
        public Color AccentColor { get => accentColor; set => SetValue(ref accentColor, value); }

        private int lengthsVersion;

        private ImageSource ambientImage;
        /// <summary>Blurred backdrop for the whole window: the #1 game's artwork.</summary>
        public ImageSource AmbientImage { get => ambientImage; set => SetValue(ref ambientImage, value); }

        private string selectedCategory;
        public string SelectedCategory
        {
            get => selectedCategory;
            set
            {
                if (value == null || value == selectedCategory) return;
                SetValue(ref selectedCategory, value);
                if (!suppressReload) ShowCurrent();
            }
        }

        private int selectedCount = 10;
        public int SelectedCount
        {
            get => selectedCount;
            set
            {
                if (value == selectedCount || value == 0) return;
                SetValue(ref selectedCount, value);
                plugin.Settings.Settings.LastCount = value;
                plugin.Settings.SaveQuietly();
                if (!suppressReload) ShowCurrent();
            }
        }

        private bool showPlaystyle;
        public bool ShowPlaystyle
        {
            get => showPlaystyle;
            set
            {
                SetValue(ref showPlaystyle, value);
                OnPropertyChanged(nameof(ListVisibility));
                OnPropertyChanged(nameof(PlaystyleVisibility));
                if (value) RefreshPlaystyle();
                UpdateEmptyState();
            }
        }
        public Visibility ListVisibility => ShowPlaystyle ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PlaystyleVisibility => ShowPlaystyle ? Visibility.Visible : Visibility.Collapsed;

        // Playstyle page
        private string profileHeadline;
        public string ProfileHeadline { get => profileHeadline; set => SetValue(ref profileHeadline, value); }
        private string profileSummary;
        public string ProfileSummary { get => profileSummary; set => SetValue(ref profileSummary, value); }
        public ObservableCollection<string> ProfileTraits { get; } = new ObservableCollection<string>();
        public ObservableCollection<LibraryAnalyzer.Stat> Stats { get; } = new ObservableCollection<LibraryAnalyzer.Stat>();
        public ObservableCollection<LibraryAnalyzer.GenreShare> GenreMix { get; } = new ObservableCollection<LibraryAnalyzer.GenreShare>();
        private Visibility profileVisibility = Visibility.Collapsed;
        public Visibility ProfileVisibility { get => profileVisibility; set => SetValue(ref profileVisibility, value); }
        private Visibility noProfileVisibility = Visibility.Visible;
        public Visibility NoProfileVisibility { get => noProfileVisibility; set => SetValue(ref noProfileVisibility, value); }
        private Visibility genreMixVisibility = Visibility.Collapsed;
        public Visibility GenreMixVisibility { get => genreMixVisibility; set => SetValue(ref genreMixVisibility, value); }

        // Category chips: collapsed = one row
        public bool ChipsCollapsed
        {
            get => plugin.Settings.Settings.CategoriesCollapsed;
            set
            {
                plugin.Settings.Settings.CategoriesCollapsed = value;
                plugin.Settings.SaveQuietly();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ChipsToggleGlyph));
                OnPropertyChanged(nameof(ChipsToggleTip));
            }
        }
        public string ChipsToggleGlyph => ChipsCollapsed ? "\uE70D" : "\uE70E";
        public string ChipsToggleTip => ChipsCollapsed ? "Show all categories" : "Show one row";
        public RelayCommand ToggleChipsCommand { get; private set; }

        private string shortNote;
        /// <summary>"Only 6 games in your library fit Horror." when a list can't reach the chosen size.</summary>
        public string ShortNote { get => shortNote; set { SetValue(ref shortNote, value); OnPropertyChanged(nameof(ShortNoteVisibility)); } }
        public Visibility ShortNoteVisibility => string.IsNullOrEmpty(ShortNote) ? Visibility.Collapsed : Visibility.Visible;

        private string footer;
        public string Footer { get => footer; set => SetValue(ref footer, value); }

        private bool isBusy;
        public bool IsBusy
        {
            get => isBusy;
            set
            {
                SetValue(ref isBusy, value);
                OnPropertyChanged(nameof(BusyVisibility));
                OnPropertyChanged(nameof(CanInteract));
                UpdateEmptyState();
            }
        }
        public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
        public bool CanInteract => !IsBusy;

        private string busyText;
        public string BusyText { get => busyText; set => SetValue(ref busyText, value); }
        private string busyDetail;
        public string BusyDetail { get => busyDetail; set => SetValue(ref busyDetail, value); }

        private string error;
        public string Error { get => error; set { SetValue(ref error, value); OnPropertyChanged(nameof(ErrorVisibility)); UpdateEmptyState(); } }
        public Visibility ErrorVisibility => string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;

        private Visibility emptyVisibility = Visibility.Collapsed;
        public Visibility EmptyVisibility { get => emptyVisibility; set => SetValue(ref emptyVisibility, value); }

        private string emptyText;
        public string EmptyText { get => emptyText; set => SetValue(ref emptyText, value); }

        public RelayCommand RegenerateCommand { get; }
        public RelayCommand<RecommendationItemViewModel> PlayCommand { get; }
        public RelayCommand<RecommendationItemViewModel> ShowCommand { get; }
        public RelayCommand<RecommendationItemViewModel> DismissCommand { get; }
        public RelayCommand SettingsCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand ShowListCommand { get; }
        public RelayCommand ShowPlaystyleCommand { get; }

        // ---------- Loading ----------

        /// <summary>Re-reads categories/settings and shows the saved lists. Call whenever the window is shown. Always starts on Overall.</summary>
        public void Refresh()
        {
            var s = plugin.Settings.Settings;
            suppressReload = true;
            try
            {
                Categories.Clear();
                Categories.Add(RecommendationService.OverallCategory);
                foreach (var n in s.Categories.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n))) Categories.Add(n.Trim());

                selectedCategory = null;
                SelectedCategory = RecommendationService.OverallCategory;
                SelectedCount = Counts.Contains(s.LastCount) ? s.LastCount : 10;
                OnPropertyChanged(nameof(SelectedCategory));
                OnPropertyChanged(nameof(SelectedCount));
            }
            finally
            {
                suppressReload = false;
            }
            if (IsBusy) return;

            if (plugin.Service.Batch == null)
            {
                ClearList();
                UpdateAccent();
                Footer = string.Empty;
                RefreshPlaystyle();
                if (s.AutoGenerate || s.Provider == AiProvider.BuiltIn) Generate();
                else UpdateEmptyState();
                return;
            }
            ShowCurrent();
            RefreshPlaystyle();
        }

        private void ClearList()
        {
            Featured = null;
            Rest.Clear();
        }

        /// <summary>Shows the selected category from the saved batch (no AI request).</summary>
        private void ShowCurrent()
        {
            Error = null;
            ClearList();
            var batch = plugin.Service.Batch;
            if (batch == null)
            {
                UpdateEmptyState();
                return;
            }

            var list = plugin.Service.GetList(SelectedCategory);
            if (list != null)
            {
                var dismissed = new HashSet<Guid>(plugin.Settings.Settings.DismissedGameIds);
                int rank = 1;
                foreach (var stored in list.Items)
                {
                    if (dismissed.Contains(stored.GameId)) continue;
                    var game = plugin.PlayniteApi.Database.Games.Get(stored.GameId);
                    if (game == null) continue;
                    var item = BuildItem(game, stored.Reason, rank, rank == 1);
                    if (rank == 1) Featured = item;
                    else Rest.Add(item);
                    rank++;
                    if (rank > SelectedCount) break;
                }
            }
            var shown = (Featured == null ? 0 : 1) + Rest.Count;
            ShortNote = list != null && shown > 0 && shown < SelectedCount
                ? $"Only {shown} {(shown == 1 ? "game" : "games")} in your library fit {SelectedCategory}."
                : null;
            AmbientImage = Featured?.Background ?? Featured?.Cover;
            UpdateAccent();
            var onScreen = new List<RecommendationItemViewModel>();
            if (Featured != null) onScreen.Add(Featured);
            onScreen.AddRange(Rest);
            FetchLengthsAsync(onScreen);
            Footer = $"Made {LibraryAnalyzer.Ago(batch.GeneratedAt)} with {batch.ProviderName}" +
                     (string.IsNullOrEmpty(batch.Model) || batch.ProviderName == "Built-in" ? string.Empty : $" · {batch.Model}") +
                     (plugin.Settings.Settings.UseHowLongToBeat ? " · times from HowLongToBeat" : string.Empty);
            EmptyText = list == null
                ? $"\"{SelectedCategory}\" was added after these lists were made."
                : $"No good {SelectedCategory} picks in your library.";
            UpdateEmptyState();
            ListChanged?.Invoke();
        }

        private void RefreshPlaystyle()
        {
            var profile = plugin.Service.Batch?.Profile;
            var hasProfile = profile != null && !string.IsNullOrWhiteSpace(profile.Summary);
            ProfileHeadline = hasProfile ? (string.IsNullOrWhiteSpace(profile.Headline) ? "Your playstyle" : profile.Headline) : null;
            ProfileSummary = hasProfile ? profile.Summary : null;
            ProfileTraits.Clear();
            if (hasProfile) foreach (var t in profile.Traits) ProfileTraits.Add(t);
            ProfileVisibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
            NoProfileVisibility = hasProfile ? Visibility.Collapsed : Visibility.Visible;

            Stats.Clear();
            GenreMix.Clear();
            try
            {
                var analyzer = plugin.Service.CurrentAnalyzer();
                foreach (var stat in analyzer.Stats()) Stats.Add(stat);
                foreach (var g in analyzer.GenreMix()) GenreMix.Add(g);
            }
            catch (Exception e)
            {
                logger.Warn(e, "Play Next: couldn't compute stats.");
            }
            GenreMixVisibility = GenreMix.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private RecommendationItemViewModel BuildItem(Game game, string reason, int rank, bool featured)
        {
            var hours = game.Playtime / 3600.0;
            var snap = new GameSnapshot { CompletionStatus = game.CompletionStatus?.Name, Hours = hours };
            snap.Status = LibraryAnalyzer.ClassifyStatus(snap.CompletionStatus, snap.Hours);
            snap.Kind = LibraryAnalyzer.ClassifyKind(snap);
            var color = snap.Kind == RecommendationKind.New ? NewColor : snap.Kind == RecommendationKind.Continue ? ContinueColor : RevisitColor;

            var meta = new List<string>();
            if (game.ReleaseDate.HasValue) meta.Add(game.ReleaseDate.Value.Year.ToString());
            var genres = game.Genres?.Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(2).ToList();
            if (genres != null && genres.Count > 0) meta.Add(string.Join(", ", genres));

            string played;
            if (hours >= 0.1)
            {
                played = $"{FormatHours(hours)} played";
                if (game.LastActivity.HasValue) played += " · " + LibraryAnalyzer.Ago(game.LastActivity.Value);
            }
            else
            {
                played = "Not started yet";
            }
            if (!string.IsNullOrEmpty(snap.CompletionStatus) && snap.Kind != RecommendationKind.New) played += " · " + snap.CompletionStatus;

            int? score = game.CriticScore ?? game.CommunityScore;
            var item = new RecommendationItemViewModel
            {
                Rank = rank,
                GameId = game.Id,
                Name = game.Name,
                Reason = reason,
                KindLabel = snap.Kind.ToString(),
                KindBrush = Frozen(color),
                KindSoftBrush = Frozen(Color.FromArgb(0x33, color.R, color.G, color.B)),
                MetaLine = string.Join("  ·  ", meta),
                PlayedLine = played,
                Score = score.HasValue ? score.Value.ToString() : null,
                Source = game.Source?.Name,
                Installed = game.IsInstalled,
                Cover = LoadImage(game.CoverImage, featured ? 220 : 120)
            };
            if (featured) item.Background = LoadImage(game.BackgroundImage, 720) ?? LoadImage(game.CoverImage, 480);

            item.LookupName = game.Name;
            item.Year = game.ReleaseDate?.Year;
            item.HoursPlayed = hours;
            item.Kind = snap.Kind;
            ApplyKnownLength(item);
            return item;
        }

        private static string FormatHours(double h) => RecommendationItemViewModel.FormatHours(h);

        /// <summary>Shows HowLongToBeat data if it's cached, otherwise the AI estimate (until a lookup finishes).</summary>
        private void ApplyKnownLength(RecommendationItemViewModel item)
        {
            var settings = plugin.Settings.Settings;
            var hltb = settings.UseHowLongToBeat ? plugin.Service.Hltb.GetCached(item.GameId) : null;
            if (hltb != null && hltb.HasData)
            {
                item.ApplyLength(hltb.Main, hltb.Plus, hltb.Full, true, hltb.Url);
                return;
            }
            var ai = plugin.Service.GetLength(item.GameId);
            if (ai != null) item.ApplyLength(ai.Main, -1, ai.Full, false);
            if (settings.UseHowLongToBeat && hltb == null && !plugin.Service.Hltb.IsUnavailable) item.LookupVisibility = Visibility.Visible;
        }

        /// <summary>Looks up real times for the games on screen, one at a time, updating cards as results arrive.</summary>
        private async void FetchLengthsAsync(List<RecommendationItemViewModel> items)
        {
            var version = ++lengthsVersion;
            if (!plugin.Settings.Settings.UseHowLongToBeat) return;
            foreach (var item in items)
            {
                if (version != lengthsVersion) return;
                if (item.HasRealLength || plugin.Service.Hltb.GetCached(item.GameId) != null)
                {
                    item.LookupVisibility = Visibility.Collapsed;
                    continue;
                }
                HltbEntry entry = null;
                try
                {
                    entry = await plugin.Service.Hltb.LookupAsync(item.GameId, item.LookupName, item.Year, CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Play Next: HowLongToBeat lookup failed.");
                }
                item.LookupVisibility = Visibility.Collapsed;
                if (entry != null && entry.HasData) item.ApplyLength(entry.Main, entry.Plus, entry.Full, true, entry.Url);
                if (plugin.Service.Hltb.IsUnavailable)
                {
                    foreach (var rest in items) rest.LookupVisibility = Visibility.Collapsed;
                    return;
                }
            }
        }

        private void UpdateAccent()
        {
            var s = plugin.Settings.Settings;
            AccentColors.TryParse(s.AccentColor, out var custom);
            Color? fromArt = null;
            if (s.AccentMode == AccentMode.Artwork)
            {
                fromArt = AccentColors.FromImage((Featured?.Background ?? Featured?.Cover) as BitmapSource);
            }
            AccentColor = fromArt ?? custom;
        }

        private ImageSource LoadImage(string dbPath, int decodeWidth)
        {
            try
            {
                if (string.IsNullOrEmpty(dbPath) || dbPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
                var path = plugin.PlayniteApi.Database.GetFullFilePath(dbPath);
                if (path == null || !File.Exists(path)) return null;
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                img.DecodePixelWidth = decodeWidth;
                img.UriSource = new Uri(path);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateEmptyState()
        {
            if (IsBusy || !string.IsNullOrEmpty(Error) || Featured != null || ShowPlaystyle)
            {
                EmptyVisibility = Visibility.Collapsed;
                return;
            }
            if (plugin.Service.Batch == null) EmptyText = "No lists yet. Make your first ones!";
            EmptyVisibility = Visibility.Visible;
        }

        // ---------- Actions ----------

        /// <summary>One request that makes the playstyle profile and the lists for every category.</summary>
        public async void Generate()
        {
            generationCts?.Cancel();
            var cts = new CancellationTokenSource();
            generationCts = cts;
            var version = ++generationVersion;
            var settings = plugin.Settings.Settings;
            var categoryCount = 1 + settings.Categories.Count(c => !string.IsNullOrWhiteSpace(c.Name));

            Error = null;
            var started = DateTime.Now;
            BusyText = settings.Provider == AiProvider.BuiltIn ? "Crunching your library…" : "Reading your library…";
            var stage = settings.Provider == AiProvider.BuiltIn
                ? string.Empty
                : $"Asking {(settings.Provider == AiProvider.Gemini ? "Gemini" : "Claude")} for your playstyle and {categoryCount} lists";
            Action updateText = () =>
            {
                var secs = (int)(DateTime.Now - started).TotalSeconds;
                BusyDetail = stage + (secs >= 2 ? $"\n{secs}s · usually 30–120 seconds" : string.Empty);
            };
            updateText();
            var progress = new Progress<string>(text => { stage = text; updateText(); });
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => { if (version == generationVersion) updateText(); };
            timer.Start();
            IsBusy = true;
            try
            {
                if (settings.Provider != AiProvider.BuiltIn) BusyText = "Thinking about what you'd enjoy…";
                await plugin.Service.GenerateAllAsync(settings, progress, cts.Token);
                if (version != generationVersion) return;
                ShowCurrent();
                RefreshPlaystyle();
            }
            catch (OperationCanceledException)
            {
                if (version == generationVersion)
                {
                    Error = cts.IsCancellationRequested
                        ? "Cancelled."
                        : "The AI took too long to answer. Try again, or lower \"Max games sent to the AI\" in settings.";
                }
            }
            catch (ProviderException e)
            {
                if (version == generationVersion) Error = e.Message;
            }
            catch (Exception e)
            {
                logger.Error(e, "Play Next: failed to generate recommendations.");
                if (version == generationVersion)
                {
                    Error = e is System.Net.Http.HttpRequestException || e.InnerException is System.Net.WebException
                        ? "Couldn't reach the AI service. Check your internet connection."
                        : "Something went wrong: " + e.Message;
                }
            }
            finally
            {
                timer.Stop();
                if (version == generationVersion) IsBusy = false;
            }
        }

        private void Dismiss(RecommendationItemViewModel item)
        {
            if (item == null) return;
            var s = plugin.Settings.Settings;
            if (!s.DismissedGameIds.Contains(item.GameId))
            {
                s.DismissedGameIds.Add(item.GameId);
                plugin.Settings.SaveQuietly();
            }
            ShowCurrent();
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PlayNext.UI
{
    public partial class RecommendationsWindow : Window
    {
        private const double ShadowMargin = 16;
        private const double CornerRadius = 24;
        private static readonly Duration Fast = new Duration(TimeSpan.FromMilliseconds(160));
        private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        private readonly RecommendationsViewModel viewModel;
        private readonly bool isFlyout;
        private readonly Func<bool> closeOnFocusLoss;
        private bool hiding;

        // Accent brushes live in Resources (used via DynamicResource). WPF may freeze brushes it holds,
        // so a color change always swaps in brand-new brushes instead of modifying the old ones.
        private Color currentAccent = AccentColors.Fallback;
        private System.Windows.Threading.DispatcherTimer accentTimer;
        private static readonly Playnite.SDK.ILogger logger = Playnite.SDK.LogManager.GetLogger();

        /// <summary>When the flyout was last hidden; lets a tray click that caused the hide not immediately reopen it.</summary>
        public DateTime LastHidden { get; private set; } = DateTime.MinValue;

        public RecommendationsWindow(RecommendationsViewModel viewModel, bool isFlyout, Func<bool> closeOnFocusLoss)
        {
            InitializeComponent();
            this.viewModel = viewModel;
            this.isFlyout = isFlyout;
            this.closeOnFocusLoss = closeOnFocusLoss;
            DataContext = viewModel;
            ApplyAccent(viewModel.AccentColor, false);
            viewModel.RequestHide += HideOrClose;
            viewModel.ListChanged += OnListChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            Surface.SizeChanged += (s, e) =>
                Surface.Clip = new RectangleGeometry(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight), CornerRadius, CornerRadius);

            Header.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch (InvalidOperationException) { }
                }
            };

            if (isFlyout)
            {
                Topmost = true;
                Deactivated += (s, e) =>
                {
                    if (closeOnFocusLoss() && IsVisible) HideOrClose();
                };
            }
            else
            {
                ResizeMode = ResizeMode.CanResizeWithGrip;
                // A standalone window (not owned by Playnite) so it can be minimized on its own,
                // with its own taskbar button to bring it back.
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ShowInTaskbar = true;
                MinimizeButton.Visibility = Visibility.Visible;
                try
                {
                    using (var stream = typeof(RecommendationsWindow).Assembly.GetManifestResourceStream("PlayNext.tray-white.ico"))
                    {
                        if (stream != null) Icon = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    }
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Play Next: couldn't set the window icon.");
                }
                Width = 600;
                Height = 860;
                Loaded += (s, e) => PlayOpenAnimation();
            }

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) HideOrClose();
            };
            Closed += (s, e) =>
            {
                viewModel.RequestHide -= HideOrClose;
                viewModel.ListChanged -= OnListChanged;
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            };
        }

        // ---------- Show / hide ----------

        /// <summary>Shows the flyout in the bottom-right corner, next to the taskbar clock.</summary>
        public void ShowAsFlyout()
        {
            var area = SystemParameters.WorkArea;
            Height = Math.Min(812, area.Height + 2 * ShadowMargin - 24);
            Left = area.Right - Width + ShadowMargin - 12;
            Top = area.Bottom - Height + ShadowMargin - 12;
            viewModel.Refresh();
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Focus();
            PlayOpenAnimation();
        }

        private void PlayOpenAnimation()
        {
            hiding = false;
            var slide = new TranslateTransform(0, 18);
            Surface.RenderTransform = slide;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(200))) { EasingFunction = EaseOut });
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, new Duration(TimeSpan.FromMilliseconds(280))) { EasingFunction = EaseOut });
        }

        private void HideOrClose()
        {
            if (!isFlyout)
            {
                Close();
                return;
            }
            if (!IsVisible || hiding) return;
            hiding = true;
            LastHidden = DateTime.Now;
            var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(120)));
            fade.Completed += (s, e) =>
            {
                if (!hiding) return;
                Hide();
                BeginAnimation(OpacityProperty, null);
                Opacity = 1;
                hiding = false;
            };
            BeginAnimation(OpacityProperty, fade);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            HideOrClose();
        }

        // ---------- View model reactions ----------

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RecommendationsViewModel.ShowPlaystyle))
            {
                // Slide the tab highlight under the active tab.
                var target = viewModel.ShowPlaystyle ? ListTabButton.ActualWidth : 0;
                TabPill.BeginAnimation(WidthProperty, new DoubleAnimation(viewModel.ShowPlaystyle ? 128 : 118, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = EaseOut });
                TabPillShift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(260))) { EasingFunction = EaseOut });
                var page = viewModel.ShowPlaystyle ? (FrameworkElement)PlaystyleScroller : ListScroller;
                FadeUp(page, 0, 14);
            }
            else if (e.PropertyName == nameof(RecommendationsViewModel.ChipsCollapsed))
            {
                // The panel re-lays itself out; just ease the chips in so the change doesn't feel abrupt.
                FadeUp(CategoryChips, 0, 4);
            }
            else if (e.PropertyName == nameof(RecommendationsViewModel.AccentColor))
            {
                ApplyAccent(viewModel.AccentColor, IsVisible);
            }
            else if (e.PropertyName == nameof(RecommendationsViewModel.AmbientImage))
            {
                Ambient.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.5, new Duration(TimeSpan.FromMilliseconds(700))) { EasingFunction = EaseOut });
            }
        }

        private void ApplyAccent(Color target, bool animate)
        {
            try
            {
                accentTimer?.Stop();
                if (!animate || target == currentAccent)
                {
                    SetAccentResources(target);
                    return;
                }
                // Short cross-fade between the old and new accent (new brushes each step).
                var from = currentAccent;
                int step = 0;
                const int steps = 12;
                accentTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                accentTimer.Tick += (s, e) =>
                {
                    step++;
                    var t = 1 - Math.Pow(1 - (double)step / steps, 3);
                    SetAccentResources(Lerp(from, target, t));
                    if (step >= steps) accentTimer.Stop();
                };
                accentTimer.Start();
            }
            catch (Exception e)
            {
                logger.Warn(e, "Play Next: couldn't apply the accent color.");
            }
        }

        private void SetAccentResources(Color c)
        {
            currentAccent = c;
            Resources["B.Accent"] = FrozenBrush(c);
            Resources["B.AccentSoft"] = FrozenBrush(AccentColors.WithAlpha(c, 0x33));
            Resources["B.AccentLight"] = FrozenBrush(AccentColors.Light(c));
            Resources["B.OnAccent"] = FrozenBrush(AccentColors.OnColor(c));
            var glow = new RadialGradientBrush(AccentColors.WithAlpha(c, 0x48), Colors.Transparent);
            glow.Freeze();
            Resources["B.Glow"] = glow;
        }

        private static SolidColorBrush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
        }

        private void OnListChanged()
        {
            ListScroller.ScrollToTop();
            FadeUp(Hero, 0, 10);
            HeroScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, new Duration(TimeSpan.FromMilliseconds(320))) { EasingFunction = EaseOut });
            HeroScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, new Duration(TimeSpan.FromMilliseconds(320))) { EasingFunction = EaseOut });
        }

        private static void FadeUp(UIElement element, int delayMs, double distance)
        {
            var shift = new TranslateTransform(0, distance);
            element.RenderTransform = element is FrameworkElement fe && fe.Name == "Hero" ? element.RenderTransform : shift;
            var begin = TimeSpan.FromMilliseconds(delayMs);
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(260))) { BeginTime = begin, EasingFunction = EaseOut });
            if (element.RenderTransform == shift)
            {
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, new Duration(TimeSpan.FromMilliseconds(320))) { BeginTime = begin, EasingFunction = EaseOut });
            }
        }

        // ---------- Cards ----------

        private void Card_Loaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is Border card)) return;
            var rank = (card.DataContext as RecommendationItemViewModel)?.Rank ?? 2;
            var group = new TransformGroup();
            var scale = new ScaleTransform(1, 1);
            var shift = new TranslateTransform(0, 16);
            group.Children.Add(scale);
            group.Children.Add(shift);
            card.RenderTransform = group;
            card.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));

            // Staggered entrance: each card fades and slides up a moment after the one above it.
            var begin = TimeSpan.FromMilliseconds(Math.Min(60 + (rank - 2) * 45, 600));
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(280))) { BeginTime = begin, EasingFunction = EaseOut });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, new Duration(TimeSpan.FromMilliseconds(360))) { BeginTime = begin, EasingFunction = EaseOut });
        }

        private void Card_MouseEnter(object sender, MouseEventArgs e) => AnimateCard(sender as Border, true);

        private void Card_MouseLeave(object sender, MouseEventArgs e) => AnimateCard(sender as Border, false);

        private static void AnimateCard(Border card, bool hover)
        {
            if (card == null) return;
            if (card.Background is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(hover ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), Fast));
            }
            if (card.RenderTransform is TransformGroup group && group.Children.Count > 1 && group.Children[1] is TranslateTransform shift)
            {
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(hover ? -2 : 0, Fast) { EasingFunction = EaseOut });
            }
            var actions = FindByTag(card, "actions");
            actions?.BeginAnimation(OpacityProperty, new DoubleAnimation(hover ? 1 : 0.6, Fast));
        }

        private void Hero_MouseEnter(object sender, MouseEventArgs e)
        {
            HeroScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.012, Fast) { EasingFunction = EaseOut });
            HeroScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.012, Fast) { EasingFunction = EaseOut });
        }

        private void Hero_MouseLeave(object sender, MouseEventArgs e)
        {
            HeroScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, Fast) { EasingFunction = EaseOut });
            HeroScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, Fast) { EasingFunction = EaseOut });
        }

        private static UIElement FindByTag(DependencyObject root, string tag)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && Equals(fe.Tag, tag)) return fe;
                var found = FindByTag(child, tag);
                if (found != null) return found;
            }
            return null;
        }

        // ---------- Spinner ----------

        private void BusyOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (BusyOverlay.IsVisible)
            {
                SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1.1))) { RepeatBehavior = RepeatBehavior.Forever });
                SpinnerGlyph.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0.45, 1, new Duration(TimeSpan.FromSeconds(0.9))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                FadeUp(BusyOverlay, 0, 8);
            }
            else
            {
                SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                SpinnerGlyph.BeginAnimation(OpacityProperty, null);
            }
        }
    }
}

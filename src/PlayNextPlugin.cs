using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using PlayNext.UI;

namespace PlayNext
{
    public class PlayNextPlugin : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public override Guid Id { get; } = Guid.Parse("67c5a910-8da6-44c2-bd79-88b4c171c113");

        public PlayNextSettingsViewModel Settings { get; }
        public RecommendationService Service { get; }

        private TrayIcon trayIcon;
        private RecommendationsWindow flyout;
        private RecommendationsViewModel flyoutViewModel;

        public PlayNextPlugin(IPlayniteAPI api) : base(api)
        {
            Service = new RecommendationService(api, GetPluginUserDataPath());
            Settings = new PlayNextSettingsViewModel(this);
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        private static string InstallDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        private bool IsDesktopMode => PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop;

        // ---------- Lifecycle ----------

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            logger.Info($"Play Next: started (mode {PlayniteApi.ApplicationInfo.Mode}, tray icon {(Settings.Settings.ShowTrayIcon ? "on" : "off")}).");
            if (IsDesktopMode)
            {
                RunOnUi(UpdateTrayIcon);
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            RunOnUi(() =>
            {
                trayIcon?.Dispose();
                trayIcon = null;
            });
        }

        public void OnSettingsChanged()
        {
            if (IsDesktopMode) RunOnUi(UpdateTrayIcon);
        }

        private void UpdateTrayIcon()
        {
            if (Settings.Settings.ShowTrayIcon && trayIcon == null)
            {
                try
                {
                    var s = Settings.Settings;
                    trayIcon = new TrayIcon(this, s.PinTrayIcon && !s.TrayPinDone);
                    trayIcon.PinFinished += done =>
                    {
                        // Only pin once, so if you later move it back under the arrow it stays there.
                        if (done) { Settings.Settings.TrayPinDone = true; Settings.SaveQuietly(); }
                    };
                    if (!Settings.Settings.TrayHintShown)
                    {
                        trayIcon.ShowWhereIAmHint();
                        Settings.Settings.TrayHintShown = true;
                        Settings.SaveQuietly();
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, "Play Next: couldn't create the tray icon.");
                }
            }
            else if (!Settings.Settings.ShowTrayIcon && trayIcon != null)
            {
                trayIcon.Dispose();
                trayIcon = null;
            }
        }

        /// <summary>From the tray menu: hide the icon (it can be turned back on in settings).</summary>
        public void HideTrayIcon()
        {
            RunOnUi(() =>
            {
                Settings.Settings.ShowTrayIcon = false;
                Settings.SaveQuietly();
                UpdateTrayIcon();
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    "PlayNext_TrayHidden",
                    "Play Next's tray icon is hidden. Turn it back on in Settings → Extensions → Play Next, or use the Play Next button in the sidebar.",
                    NotificationType.Info,
                    () => OpenSettings()));
            });
        }

        /// <summary>
        /// Runs a UI action so that a bug in Play Next shows a notification instead of crashing Playnite
        /// (exceptions from tray clicks would otherwise take the whole app down).
        /// </summary>
        public void Safe(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                logger.Error(e, $"Play Next: {what} failed.");
                try
                {
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "PlayNext_Error",
                        $"Play Next hit a problem ({what}): {e.Message} Details are in Playnite's log.",
                        NotificationType.Error));
                }
                catch { }
            }
        }

        private static void RunOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        // ---------- Playnite integration points ----------

        public override ISettings GetSettings(bool firstRunSettings) => Settings;

        public override UserControl GetSettingsView(bool firstRunSettings) => new PlayNextSettingsView();

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = "Play Next",
                Type = SiderbarItemType.Button,
                Icon = Path.Combine(InstallDir, "sidebar.png"),
                Activated = () => Safe("opening the window", OpenWindow)
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@Play Next",
                Description = "Open recommendations",
                Action = a => Safe("opening the window", OpenWindow)
            };
            yield return new MainMenuItem
            {
                MenuSection = "@Play Next",
                Description = "Settings",
                Action = a => Safe("opening settings", OpenSettings)
            };
        }

        // ---------- Windows ----------

        /// <summary>Normal window centered on Playnite (sidebar / menu).</summary>
        private RecommendationsWindow mainWindow;

        public void OpenWindow()
        {
            // Only one window: clicking the sidebar button again brings the open one to the front.
            if (mainWindow != null)
            {
                if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                return;
            }
            var vm = new RecommendationsViewModel(this);
            var window = new RecommendationsWindow(vm, false, () => false);
            mainWindow = window;
            window.Closed += (s, e) => mainWindow = null;
            // Not owned by Playnite's window, so it minimizes and restores independently.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            vm.Refresh();
            window.Show();
            window.Activate();
        }

        /// <summary>Small popup above the tray, like Dropbox's. A tray click toggles it.</summary>
        public void ToggleFlyout()
        {
            if (flyout == null)
            {
                flyoutViewModel = new RecommendationsViewModel(this);
                flyout = new RecommendationsWindow(flyoutViewModel, true, () => Settings.Settings.ClosePopupOnFocusLoss);
                flyout.Closed += (s, e) => { flyout = null; flyoutViewModel = null; };
            }

            if (flyout.IsVisible)
            {
                flyout.Hide();
                return;
            }
            // Clicking the tray icon steals focus, which already hid the popup a moment ago; don't reopen it.
            if ((DateTime.Now - flyout.LastHidden).TotalMilliseconds < 350) return;
            flyout.ShowAsFlyout();
        }

        public void RegenerateFromTray()
        {
            if (flyout == null || !flyout.IsVisible) ToggleFlyout();
            flyoutViewModel?.Generate();
        }

        public void OpenSettings()
        {
            RunOnUi(() =>
            {
                ShowPlayniteWindow();
                OpenSettingsView();
            });
        }

        public void ShowGameInLibrary(Guid gameId)
        {
            ShowPlayniteWindow();
            PlayniteApi.MainView.SelectGame(gameId);
        }

        public void ShowPlayniteWindow()
        {
            var main = Application.Current?.MainWindow;
            if (main == null) return;
            if (!main.IsVisible) main.Show();
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            main.Activate();
        }
    }
}

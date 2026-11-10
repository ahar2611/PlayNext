using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;
using Playnite.SDK;

namespace PlayNext
{
    /// <summary>
    /// Notification-area icon (next to the clock). Left click toggles the popup; right click shows a menu.
    /// The glyph is white on a dark taskbar and black on a light one, and follows theme changes.
    /// </summary>
    internal sealed class TrayIcon : IDisposable
    {
        private const string Tooltip = "Play Next: what to play from your library";
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly NotifyIcon notifyIcon;
        private readonly System.Windows.Threading.DispatcherTimer pinTimer;
        private Icon icon;
        private bool? lightTaskbar;

        public TrayIcon(PlayNextPlugin plugin, bool pinNextToClock)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show recommendations", null, (s, e) => plugin.Safe("opening the popup", plugin.ToggleFlyout));
            menu.Items.Add("Regenerate all lists", null, (s, e) => plugin.Safe("refreshing", plugin.RegenerateFromTray));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Playnite", null, (s, e) => plugin.Safe("opening Playnite", plugin.ShowPlayniteWindow));
            menu.Items.Add("Play Next settings", null, (s, e) => plugin.Safe("opening settings", plugin.OpenSettings));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Hide this icon", null, (s, e) => plugin.Safe("hiding the icon", plugin.HideTrayIcon));

            notifyIcon = new NotifyIcon
            {
                Text = Tooltip,
                ContextMenuStrip = menu
            };
            UpdateIcon();
            notifyIcon.Visible = true;
            notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) plugin.Safe("opening the popup", plugin.ToggleFlyout);
            };
            notifyIcon.BalloonTipClicked += (s, e) => plugin.Safe("opening the popup", plugin.ToggleFlyout);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            logger.Info("Play Next: tray icon created.");

            if (pinNextToClock)
            {
                // Windows registers the icon a moment after it appears, so look for its entry a few times.
                int attempts = 0;
                pinTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                pinTimer.Tick += (s, e) =>
                {
                    attempts++;
                    var result = TryPinNextToClock();
                    if (result != PinResult.NotFound || attempts >= 8)
                    {
                        pinTimer.Stop();
                        if (result == PinResult.Pinned) RefreshShellIcon();
                        PinFinished?.Invoke(result != PinResult.NotFound);
                    }
                };
                pinTimer.Start();
            }
        }

        /// <summary>Raised once the pin attempt is done (true = pinned or already pinned, so don't try again on later starts).</summary>
        public event Action<bool> PinFinished;

        // ---------- Theme-aware icon ----------

        private static bool IsTaskbarLight()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        private void UpdateIcon()
        {
            var light = IsTaskbarLight();
            if (lightTaskbar == light && icon != null) return;
            lightTaskbar = light;
            var old = icon;
            icon = LoadIcon(light ? "PlayNext.tray-black.ico" : "PlayNext.tray-white.ico");
            notifyIcon.Icon = icon;
            if (old != null && old != SystemIcons.Application) old.Dispose();
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.VisualStyle) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { UpdateIcon(); } catch (Exception ex) { logger.Warn(ex, "Play Next: couldn't update tray icon for theme."); }
            }));
        }

        private static Icon LoadIcon(string resource)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                {
                    if (stream != null) return new Icon(stream, SystemInformation.SmallIconSize);
                    logger.Warn("Play Next: embedded tray icon not found: " + resource);
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Play Next: couldn't load the tray icon image, using Playnite's.");
            }
            try
            {
                var exeIcon = Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName);
                if (exeIcon != null) return exeIcon;
            }
            catch { }
            return SystemIcons.Application;
        }

        // ---------- Pin next to the clock (Windows 11) ----------

        private enum PinResult { NotFound, Pinned, AlreadyPinned }

        /// <summary>
        /// Windows 11 keeps a per-icon "show in taskbar" switch under HKCU\Control Panel\NotifyIconSettings
        /// (the same switch as Settings → Personalization → Taskbar → Other system tray icons).
        /// Turn it on for our icon only, identified by Playnite's exe and our tooltip.
        /// </summary>
        private static PinResult TryPinNextToClock()
        {
            try
            {
                var exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) return PinResult.AlreadyPinned; // Windows 10: no such switch; nothing to do.
                    foreach (var name in root.GetSubKeyNames())
                    {
                        using (var entry = root.OpenSubKey(name, true))
                        {
                            var exe = entry?.GetValue("ExecutablePath") as string;
                            var tip = entry?.GetValue("InitialTooltip") as string;
                            if (exe == null || tip == null) continue;
                            if (!exe.EndsWith("\\" + exeName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!tip.StartsWith("Play Next", StringComparison.OrdinalIgnoreCase)) continue;
                            if (entry.GetValue("IsPromoted") is int promoted && promoted == 1)
                            {
                                return PinResult.AlreadyPinned;
                            }
                            entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            logger.Info("Play Next: pinned the tray icon next to the clock.");
                            return PinResult.Pinned;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn(e, "Play Next: couldn't pin the tray icon next to the clock.");
                return PinResult.AlreadyPinned; // don't keep retrying on errors
            }
            return PinResult.NotFound;
        }

        /// <summary>Re-adds the icon so Explorer applies the new setting right away.</summary>
        private void RefreshShellIcon()
        {
            notifyIcon.Visible = false;
            notifyIcon.Visible = true;
        }

        public void ShowWhereIAmHint()
        {
            notifyIcon.ShowBalloonTip(8000, "Play Next is in your tray",
                "Click this icon any time for game recommendations. Right-click it for more options, including hiding it.",
                ToolTipIcon.Info);
        }

        public void Dispose()
        {
            pinTimer?.Stop();
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            notifyIcon.Visible = false;
            notifyIcon.ContextMenuStrip?.Dispose();
            notifyIcon.Dispose();
            if (icon != null && icon != SystemIcons.Application) icon.Dispose();
        }
    }
}

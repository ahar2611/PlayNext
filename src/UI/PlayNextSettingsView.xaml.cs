using System.Windows;
using System.Windows.Controls;

namespace PlayNext.UI
{
    public partial class PlayNextSettingsView : UserControl
    {
        private bool loading;

        public PlayNextSettingsView()
        {
            InitializeComponent();
            // PasswordBox can't be data-bound, so sync it by hand whenever the settings object changes.
            DataContextChanged += (s, e) => LoadKeys();
            Loaded += (s, e) => LoadKeys();
        }

        private PlayNextSettingsViewModel Vm => DataContext as PlayNextSettingsViewModel;

        private void LoadKeys()
        {
            if (Vm == null) return;
            loading = true;
            GeminiKeyBox.Password = Vm.Settings.GeminiApiKey ?? string.Empty;
            ClaudeKeyBox.Password = Vm.Settings.ClaudeApiKey ?? string.Empty;
            loading = false;
        }

        private void GeminiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!loading && Vm != null) Vm.Settings.GeminiApiKey = GeminiKeyBox.Password;
        }

        private void ClaudeKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!loading && Vm != null) Vm.Settings.ClaudeApiKey = ClaudeKeyBox.Password;
        }
    }
}

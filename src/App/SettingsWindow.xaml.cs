using System.Windows;
using DevChat.App.Services;

namespace DevChat.App;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        DataContext = new SettingsEdit { Port = settings.Port.ToString(), Token = settings.Token, WorkingFolder = settings.WorkingFolder };
        InitializeComponent();
        PreventSleepCheck.IsChecked = settings.PreventSleep;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsEdit edit) return;
        if (!int.TryParse(edit.Port, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Некорректный порт.", "DevChat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.Port = port;
        _settings.Token = edit.Token.Trim();
        _settings.WorkingFolder = edit.WorkingFolder.Trim();
        _settings.PreventSleep = PreventSleepCheck.IsChecked == true;
        _settings.Save();
        Services.PowerService.PreventSleep(_settings.PreventSleep); // применяется сразу
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class SettingsEdit
    {
        public string Port { get; set; } = "8765";
        public string Token { get; set; } = "";
        public string WorkingFolder { get; set; } = "";
    }
}

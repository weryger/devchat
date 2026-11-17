using System.Windows;
using DevChat.App.Services;
using DevChat.App.ViewModels;

namespace DevChat.App;

public partial class App : Application
{
    internal readonly CancellationTokenSource ShutdownCts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = Settings.Load();
        if (settings.PreventSleep) Services.PowerService.PreventSleep(true);
        var history = new HistoryService();
        var bridge = new BridgeService();
        var window = new MainWindow { DataContext = new MainViewModel(settings, history, bridge) };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownCts.Cancel();
        base.OnExit(e);
    }
}

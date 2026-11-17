using System.Windows;

namespace DevChat.App;

public partial class ConfirmWindow : Window
{
    /// <summary>true — разрешить, false — отклонить.</summary>
    public bool Allowed { get; private set; }

    /// <summary>Пользователь снял вопросы до конца задачи.</summary>
    public bool AllowRest => AllowRestCheck.IsChecked == true;

    public ConfirmWindow(string header, string subHeader, string detail)
    {
        DataContext = new ConfirmModel { Header = header, SubHeader = subHeader, Detail = detail };
        InitializeComponent();
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        Allowed = true;
        DialogResult = true;
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        Allowed = false;
        DialogResult = false;
    }

    private sealed class ConfirmModel
    {
        public string Header { get; init; } = "";
        public string SubHeader { get; init; } = "";
        public string Detail { get; init; } = "";
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevChat.App.ViewModels;

namespace DevChat.App;

public partial class MainWindow : Window
{
    private ScrollViewer? _messagesScroll;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.ScrollToBottomRequested += ScrollToBottom;
            _messagesScroll = FindVisualChild<ScrollViewer>(MessagesList);
        };
        InputBox.Focus();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter — отправить, Shift+Enter — новая строка
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && DataContext is MainViewModel vm)
        {
            if (vm.SendCommand.CanExecute(null))
            {
                vm.SendCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var win = new HelpWindow { Owner = this };
        win.ShowDialog();
    }

    private void SideToggle_Click(object sender, RoutedEventArgs e)
    {
        var collapsed = SidePanel.Width <= 60;
        SidePanel.Width = collapsed ? 330 : 40;
        SideContent.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SideToggle.Content = collapsed ? "⟩" : "⟨";
        SideToggle.HorizontalAlignment = collapsed ? HorizontalAlignment.Right : HorizontalAlignment.Center;
    }

    private void ScrollToBottom()
    {
        // автоскролл только если пользователь и так у низа — не мешаем читать выше
        if (MessagesList.Items.Count == 0) return;
        if (_messagesScroll is { } sv && sv.ScrollableHeight - sv.VerticalOffset > 160) return;
        MessagesList.ScrollIntoView(MessagesList.Items[^1]);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var result = FindVisualChild<T>(child);
            if (result is not null) return result;
        }
        return null;
    }
}

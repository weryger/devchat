using CommunityToolkit.Mvvm.ComponentModel;

namespace DevChat.App.ViewModels;

/// <summary>Одно сообщение в ленте: role = user | assistant | system.</summary>
public partial class ChatMessageViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string? _duration;

    public required string Role { get; init; }

    /// <summary>Автор (для команды: Планировщик/Разработчик/Ревьюер).</summary>
    public string? Author { get; init; }

    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>Метаданные для шапки блока: «17.11 03:45 · Автор · 1:12».</summary>
    public string Meta =>
        $"{Timestamp:dd.MM HH:mm}"
        + (Author is { Length: > 0 } ? $" · {Author}" : "")
        + (Duration is { Length: > 0 } ? $" · {Duration}" : "");
}

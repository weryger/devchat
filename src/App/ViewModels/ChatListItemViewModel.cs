using CommunityToolkit.Mvvm.ComponentModel;

namespace DevChat.App.ViewModels;

/// <summary>Элемент списка чатов. ProviderChatUrl — сессия DeepSeek (для агентов M4).</summary>
public partial class ChatListItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "Новый чат";

    [ObservableProperty]
    private string? _providerChatUrl;

    public long Id { get; init; }
    public string? AgentRole { get; init; }
}

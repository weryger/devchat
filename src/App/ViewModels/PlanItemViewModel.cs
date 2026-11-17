using CommunityToolkit.Mvvm.ComponentModel;

namespace DevChat.App.ViewModels;

/// <summary>Пункт плана: текст, отметка и тронутые файлы.</summary>
public partial class PlanItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _mark = "";

    [ObservableProperty]
    private string _filesNote = "";

    public required string Text { get; init; }

    public bool Touched
    {
        get => Mark == "✓";
        set => Mark = value ? "✓" : "";
    }
}

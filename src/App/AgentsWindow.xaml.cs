using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevChat.App.Services;

namespace DevChat.App;

public partial class AgentsWindow : Window
{
    private readonly SkillService _skills;
    private bool _loading;

    private sealed class AgentRow
    {
        public required Skill Skill { get; init; }
        public string Name => Skill.Name;
        public bool Enabled => Skill.Enabled;
        public string RoleNote => (Skill.Role is { Length: > 0 } ? $"роль: {Skill.Role} · " : "") + $"tools: {Skill.ToolMode.ToString().ToLower()}";
    }

    public AgentsWindow(SkillService skills)
    {
        _skills = skills;
        InitializeComponent();
        Reload();
    }

    private void Reload(string? selectFile = null)
    {
        _loading = true;
        AgentsList.Items.Clear();
        Skill? toSelect = null;
        foreach (var s in _skills.List())
        {
            AgentsList.Items.Add(new AgentRow { Skill = s });
            if (selectFile is not null && s.FileName == selectFile) toSelect = s;
        }
        _loading = false;
        if (toSelect is not null) AgentsList.SelectedIndex = FindIndex(toSelect);
        else if (AgentsList.Items.Count > 0 && AgentsList.SelectedIndex < 0) AgentsList.SelectedIndex = 0;
    }

    private int FindIndex(Skill skill)
    {
        for (var i = 0; i < AgentsList.Items.Count; i++)
            if (((AgentRow)AgentsList.Items[i]!).Skill.FileName == skill.FileName) return i;
        return 0;
    }

    private void AgentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AgentsList.SelectedItem is not AgentRow row) return;
        _loading = true;
        NameBox.Text = row.Skill.Name;
        RoleBox.Text = row.Skill.Role ?? "";
        ToolsBox.Text = row.Skill.ToolMode switch { Tools.ToolMode.ReadOnly => "readonly", Tools.ToolMode.RunNoWrite => "run", _ => "full" };
        DescBox.Text = row.Skill.Description;
        EnabledCheck.IsChecked = row.Skill.Enabled;
        BodyBox.Text = row.Skill.Body;
        _loading = false;
    }

    private Skill? Current()
    {
        if (_loading || AgentsList.SelectedItem is not AgentRow row) return null;
        var tools = ToolsBox.Text.Trim().ToLowerInvariant() switch { "readonly" => Tools.ToolMode.ReadOnly, "run" => Tools.ToolMode.RunNoWrite, _ => Tools.ToolMode.Full };
        return new Skill(
            string.IsNullOrWhiteSpace(NameBox.Text) ? "Без имени" : NameBox.Text.Trim(),
            DescBox.Text.Trim(),
            "specialist",
            BodyBox.Text,
            RoleBox.Text.Trim(),
            tools,
            EnabledCheck.IsChecked == true,
            row.Skill.FileName);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var skill = Current();
        if (skill is null) return;
        if (string.IsNullOrWhiteSpace(RoleBox.Text))
        {
            MessageBox.Show(this, "Укажите role (ключ роли, латиницей) — по нему тимлид назначает задачи.", "DevChat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _skills.Save(skill);
        Reload(skill.FileName);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var skill = new Skill("Новый агент", "краткое описание роли", "specialist",
            "# Роль: Новый агент\n\nОпиши правила работы и формат вывода.",
            "new-agent", Tools.ToolMode.Full, true, "");
        _skills.Save(skill);
        Reload();
        // выбрать созданного (последний с таким именем)
        for (var i = AgentsList.Items.Count - 1; i >= 0; i--)
            if (((AgentRow)AgentsList.Items[i]!).Skill.Name == skill.Name) { AgentsList.SelectedIndex = i; break; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (AgentsList.SelectedItem is not AgentRow row) return;
        if (MessageBox.Show(this, $"Удалить агента «{row.Skill.Name}» (файл {row.Skill.FileName})?", "DevChat",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _skills.Delete(row.Skill.FileName);
        Reload();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Markdown (*.md)|*.md|Все файлы (*.*)|*.*", Title = "Импорт файла скилла" };
        if (dlg.ShowDialog(this) != true) return;
        var imported = _skills.Import(dlg.FileName);
        Reload(imported);
    }
}

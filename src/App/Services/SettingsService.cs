using System.IO;
using System.Text.Json;

namespace DevChat.App.Services;

/// <summary>Настройки приложения (JSON в %AppData%\DevChat).</summary>
public sealed class Settings
{
    public int Port { get; set; } = 8765;
    public string Token { get; set; } = "DevChat-dev-token";
    public string WorkingFolder { get; set; } = "";

    /// <summary>0 — подтверждать всё; 1 — только команды run; 2 — без подтверждений.</summary>
    public int ConfirmMode { get; set; }

    /// <summary>Не давать ПК спать, пока приложение запущено.</summary>
    public bool PreventSleep { get; set; }

    /// <summary>Провайдер по умолчанию: 0 — DeepSeek, 1 — ChatGPT.</summary>
    public int ProviderIndex { get; set; }

    private static string PathFor() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevChat", "settings.json");

    public static Settings Load()
    {
        try
        {
            var path = PathFor();
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch { /* повреждённый файл — используем значения по умолчанию */ }
        return new Settings();
    }

    public void Save()
    {
        var path = PathFor();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

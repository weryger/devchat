using System.IO;

namespace DevChat.App.Services;

/// <summary>
/// Простой потокобезопасный журнал: %AppData%\DevChat\log.txt, ротация при 2 МБ.
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();

    private static string PathFor() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevChat", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = PathFor();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n");
            }
        }
        catch { /* журнал не должен ронять приложение */ }
    }
}

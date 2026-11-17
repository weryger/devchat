using System.Net.Http;
using System.Reflection;

namespace DevChat.App.Services;

public sealed record UpdateInfo(string? LatestVersion, string? DownloadUrl, string? ReleaseNotes, bool HasUpdate);

/// <summary>
/// Проверка обновлений через GitHub Releases API.
/// Репозиторий: https://github.com/bmcoder/DevChat
/// </summary>
public static class UpdateService
{
    private const string RepoApi = "https://api.github.com/repos/bmcoder/DevChat/releases/latest";

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Проверить наличие новой версии (не блокирует вызывающий поток).</summary>
    public static async Task<UpdateInfo> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("User-Agent", "DevChat-UpdateChecker");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var json = await http.GetStringAsync(RepoApi);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
            var url = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            var latest = tag?.TrimStart('v', 'V') ?? "";
            var hasUpdate = !string.IsNullOrEmpty(latest) && Version.TryParse(latest, out var v) &&
                            v > Version.Parse(CurrentVersion);

            return new UpdateInfo(latest, url, notes, hasUpdate);
        }
        catch
        {
            return new UpdateInfo(null, null, null, false);
        }
    }
}

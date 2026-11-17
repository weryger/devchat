using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DevChat.App.Agent;
using DevChat.App.Services;

namespace DevChat.App.Tools;

public sealed record ToolResult(bool Ok, string Output);

/// <summary>
/// Исполнение действий агента. Всё — строго внутри рабочей папки (песочница по каноническому пути).
/// write_file/run — только после подтверждения; перед перезаписью оригинал уходит в журнал отката.
/// </summary>
/// <summary>Права роли на инструменты.</summary>
public enum ToolMode
{
    Full,      // чтение + запись + команды
    ReadOnly,  // только чтение (планировщик, ревьюер, UX)
    RunNoWrite // чтение + команды, без записи (тестировщик)
}

public sealed class ToolExecutor
{
    static ToolExecutor() =>
        // cmd.exe отдаёт вывод в OEM-кодировке (cp866); в .NET 8 кодовые страницы
        // не зарегистрированы по умолчанию — без этого Encoding.GetEncoding(866) падает
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly string _root;
    private readonly HistoryService _history;
    private readonly long _chatId;

    /// <summary>Подтверждение опасного действия: (действие, дифф|команда) → разрешено ли.</summary>
    public Func<ActionBlock, string?, Task<bool>>? ConfirmAsync;

    /// <summary>Права роли: Full | ReadOnly | RunNoWrite.</summary>
    public ToolMode Mode { get; init; } = ToolMode.Full;

    public ToolExecutor(string root, HistoryService history, long chatId)
    {
        _root = Path.GetFullPath(root);
        _history = history;
        _chatId = chatId;
    }

    public async Task<ToolResult> ExecuteAsync(ActionBlock a)
    {
        try
        {
            return a.Tool switch
            {
                "list_dir" => ListDir(a.Attr("path") ?? "."),
                "read_file" => ReadFile(a.Attr("path") ?? ""),
                "search" => Search(a.Attr("query") ?? "", a.Attr("path") ?? "."),
                "write_file" => await WriteFileAsync(a.Attr("path") ?? "", a.Body),
                "run" => await RunAsync(a.Body, a.Attr("cwd") ?? "."),
                _ => new ToolResult(false, $"неизвестный инструмент: {a.Tool}"),
            };
        }
        catch (UnauthorizedAccessException ex) { return new ToolResult(false, ex.Message); }
        catch (IOException ex) { return new ToolResult(false, $"IO: {ex.Message}"); }
        catch (Exception ex) { return new ToolResult(false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private (string full, string rel) Resolve(string rel)
    {
        var full = Path.GetFullPath(Path.Combine(_root, rel));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"путь вне рабочей папки: {rel}");
        return (full, Path.GetRelativePath(_root, full));
    }

    internal static bool ShouldSkipName(string? name) =>
        name is "bin" or "obj" or "node_modules" or ".git" or "dist" or ".vs" or null or "";

    private ToolResult ListDir(string rel)
    {
        var (full, relPath) = Resolve(rel);
        if (!Directory.Exists(full)) return new ToolResult(false, $"папки нет: {relPath}");
        var sb = new StringBuilder();
        var entries = Directory.EnumerateFileSystemEntries(full)
            .Where(e => !ShouldSkipName(Path.GetFileName(e)))
            .OrderBy(e => e)
            .Take(300);
        foreach (var e in entries)
            sb.AppendLine((Directory.Exists(e) ? "[D] " : "    ") + Path.GetFileName(e));
        sb.Append($"(папка: {relPath})");
        return new ToolResult(true, sb.ToString());
    }

    private ToolResult ReadFile(string rel)
    {
        var (full, relPath) = Resolve(rel);
        if (!File.Exists(full)) return new ToolResult(false, $"файла нет: {relPath}");
        var info = new FileInfo(full);
        if (info.Length > 200_000) return new ToolResult(false, $"файл слишком большой ({info.Length / 1000} КБ): {relPath}");
        var text = File.ReadAllText(full);
        if (text.Length > 50_000) text = text[..50_000] + "\n…(обрезано)";
        return new ToolResult(true, $"{relPath}:\n{text}");
    }

    private ToolResult Search(string query, string rel)
    {
        var (full, relPath) = Resolve(rel);
        if (!Directory.Exists(full)) return new ToolResult(false, $"папки нет: {relPath}");
        Regex regex;
        try { regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException) { regex = new Regex(Regex.Escape(query), RegexOptions.IgnoreCase); }

        var sb = new StringBuilder();
        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ShouldSkipName))
            .Where(f => new FileInfo(f).Length < 300_000)
            .Take(400);
        int matches = 0;
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch { continue; } // бинарные/занятые — пропускаем
            foreach (Match m in regex.Matches(text))
            {
                if (matches++ >= 50) { sb.Append("…(обрезано на 50 совпадениях)"); return new ToolResult(true, sb.ToString()); }
                var lineNo = text[..m.Index].Count(c => c == '\n') + 1;
                var line = text.Split('\n')[lineNo - 1].Trim();
                sb.AppendLine($"{Path.GetRelativePath(_root, f)}:{lineNo}: {line}");
            }
        }
        return new ToolResult(true, sb.Length > 0 ? sb.ToString() : "совпадений нет");
    }

    private async Task<ToolResult> WriteFileAsync(string rel, string content)
    {
        if (Mode != ToolMode.Full) return new ToolResult(false, "эта роль не имеет права записи файлов");
        if (string.IsNullOrWhiteSpace(rel) || rel.Trim() is "…" or "." or ".." || rel.Contains("ПУТЬ_"))
            return new ToolResult(false, "path должен быть точным путём файла из пункта плана (не заглушкой)");
        if (content.Contains("ПОЛНОЕ_НОВОЕ_СОДЕРЖИМОЕ") || content.Trim() is "полное содержимое" or "…" or "...")
            return new ToolResult(false, "тело write_file — реальное полное содержимое файла, а не текст-заглушка из примера");
        var (full, relPath) = Resolve(rel);
        var old = File.Exists(full) ? await File.ReadAllTextAsync(full) : null;
        var diff = old is null ? null : SimpleDiff.Unified(old, content);

        if (ConfirmAsync is not null && !await ConfirmAsync(
                new ActionBlock("write_file", new Dictionary<string, string> { ["path"] = relPath }, ""), diff))
            return new ToolResult(false, "запись отклонена пользователем");

        if (old is not null) _history.SaveFileVersion(_chatId, relPath, old);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, new UTF8Encoding(false));
        return new ToolResult(true, $"{relPath} записан ({content.Split('\n').Length} строк; {(old is null ? "новый файл" : "перезаписан")})");
    }

    private async Task<ToolResult> RunAsync(string command, string cwd)
    {
        if (Mode == ToolMode.ReadOnly) return new ToolResult(false, "эта роль работает только на чтение — команды запрещены");
        var (fullCwd, _) = Resolve(cwd);
        if (!Directory.Exists(fullCwd)) return new ToolResult(false, $"папки нет: {cwd}");

        if (ConfirmAsync is not null && !await ConfirmAsync(
                new ActionBlock("run", new Dictionary<string, string> { ["cwd"] = cwd }, command), command))
            return new ToolResult(false, "команда отклонена пользователем");

        var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            WorkingDirectory = fullCwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.GetEncoding(866), // cp866 — консоль cmd
            StandardErrorEncoding = Encoding.GetEncoding(866),
        };
        using var process = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cts.Token);
            var output = (await stdout + await stderr).Trim();
            if (output.Length > 8000) output = output[..8000] + "\n…(обрезано)";
            return new ToolResult(process.ExitCode == 0, $"exit={process.ExitCode}\n{output}");
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new ToolResult(false, "таймаут 60с — процесс убит");
        }
    }
}

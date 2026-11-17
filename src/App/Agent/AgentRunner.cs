using System.IO;
using System.Text;
using System.Text.Json;
using DevChat.App.Agent;
using DevChat.App.Services;
using DevChat.App.Tools;

namespace DevChat.App.Agent;

/// <summary>
/// Цикл агентного режима: задача → ответ модели → разбор действий → исполнение → результаты обратно модели → …до &lt;final&gt;.
/// </summary>
/// <summary>Ошибка, пришедшая от моста/провайдера (код + сообщение).</summary>
public sealed class AgentOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AgentRunner
{
    private const int MaxIterations = 25;

    /// <summary>Порог ротации: суммарные символы ходов в одной сессии DeepSeek.</summary>
    private const long RotateAfterChars = 60_000;

    private long _charsThisSession;
    private int _protocolStrikes;
    private bool _anyActionDone;

    /// <summary>Начался очередной ход модели (следом поедут дельты стрима).</summary>
    public event Action? TurnStarted;

    /// <summary>Ход модели завершён (полный ответ получен).</summary>
    public event Action? TurnCompleted;

    /// <summary>Строка о действии: «🔧 write_file src/x.cs».</summary>
    public event Action<string>? ActionStarted;

    /// <summary>Итог действия: «✓ файл записан» / «✗ отклонено».</summary>
    public event Action<string>? ActionDone;

    /// <summary>Служебные сообщения (лимит итераций и т.п.).</summary>
    public event Action<string>? SystemMessage;

    /// <summary>Файлы, изменённые этим запуском агента (успешные write_file).</summary>
    public IReadOnlyList<string> ChangedFiles => _changedFiles;
    private readonly List<string> _changedFiles = new();

    /// <summary>Минимальное «профильное» действие роли: без него финал не принимается.</summary>
    private string? RequiredTool => Profile?.Mode switch
    {
        Tools.ToolMode.Full => "write_file",
        Tools.ToolMode.RunNoWrite => "run",
        _ => null,
    };

    /// <summary>Профиль роли (для оркестратора): своя вкладка DeepSeek, свой промпт, права.</summary>
    public AgentProfile? Profile { get; set; }

    /// <summary>Секция скиллов для одиночного агента (без профиля).</summary>
    public string? SkillsSection { get; set; }

    /// <summary>Провайдер: deepseek | chatgpt.</summary>
    public string Provider { get; set; } = "deepseek";

    /// <summary>Своё первое сообщение вместо «ЗАДАЧА: …» (оркестратор передаёт план/замечания).</summary>
    public string? FirstPromptOverride { get; set; }

    public async Task<string> RunAsync(
        BridgeService bridge,
        long chatId,
        string task,
        string workingFolder,
        HistoryService history,
        Func<ActionBlock, string?, Task<bool>> confirm,
        CancellationToken ct)
    {
        var summary = BuildDirSummary(workingFolder);
        var intro = Profile?.Intro ?? SystemPrompts.AgentIntro(workingFolder, summary) + (SkillsSection ?? "");
        var prompt = FirstPromptOverride ?? $"{intro}\n\nЗАДАЧА:\n{task}\n\nНачинай.";

        var tools = new ToolExecutor(workingFolder, history, chatId) { ConfirmAsync = confirm, Mode = Profile?.Mode ?? Tools.ToolMode.Full };

        for (var iter = 1; iter <= MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();

            // ротация контекста: сессия DeepSeek переполнилась — переносим резюме в новый чат
            if (_charsThisSession > RotateAfterChars && iter < MaxIterations)
            {
                _charsThisSession = 0;
                SystemMessage?.Invoke($"🔄 Контекст вырос — переношу работу в новый чат DeepSeek…");
                var summaryResult = await SendTurnAsync(bridge,
                    "Составь краткое резюме для переноса в новый чат: исходная задача, что уже сделано, что осталось, важные пути файлов и текущее состояние. Только текст, БЕЗ блоков action.",
                    ct);
                var rotSummary = summaryResult.TryGetProperty("markdown", out var sm) ? sm.GetString() ?? "" : "";
                await bridge.RequestAsync("openChat", new Dictionary<string, object?> { ["newChat"] = true, ["agent"] = Profile?.Key, ["provider"] = Provider }, 30_000, ct);
                prompt = $"ПРОДОЛЖЕНИЕ РАБОТЫ (новый чат, предыдущий контекст перенесён резюме).\n\nРЕЗЮМЕ:\n{rotSummary}\n\nПродолжай выполнять исходную задачу теми же действиями.";
            }

            TurnStarted?.Invoke();

            var result = await SendTurnAsync(bridge, prompt, ct);
            TurnCompleted?.Invoke();
            var answer = result.TryGetProperty("markdown", out var md) ? md.GetString() ?? "" : "";
            _charsThisSession += prompt.Length + answer.Length;

            var final = ActionParser.ParseFinal(answer);
            var actions = ActionParser.ParseActions(answer);
            // «профильная работа» сделана: запись для исполнителей, команда для QA;
            // list_dir/read_file не считаются — модель любит имитировать работу одним чтением
            if (actions.Any(a => RequiredTool is null || a.Tool == RequiredTool)) _anyActionDone = true;
            if (final is not null)
            {
                // «инструменты недоступны / прикрепи ZIP» — отказная колея контекста вкладки:
                // сразу пересоздаём сессию агента (чистый контекст) и повторяем ход
                if (System.Text.RegularExpressions.Regex.IsMatch(final, @"недоступн|unavailable|не подключены|ZIP-архив|не буду имитировать|не буду утверждать", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && Profile?.Key is { Length: > 0 } agentKey)
                {
                    SystemMessage?.Invoke("♻️ Агент отказывается от инструментов — пересоздаю его сессию с чистым контекстом…");
                    await bridge.RequestAsync("resetAgent", new Dictionary<string, object?> { ["agent"] = agentKey }, 15_000, ct);
                    prompt = FirstPromptOverride ?? $"{Profile!.Intro}\n\nЗАДАЧА:\n{task}\n\nНачинай.";
                    continue;
                }
                if (Profile is { Mode: Tools.ToolMode.ReadOnly } || _anyActionDone || iter == MaxIterations)
                    return final;
                var req = RequiredTool == "write_file"
                    ? "Для твоей роли обязателен минимум один УСПЕШНЫЙ <action tool=\"write_file\"> с реальным содержимым"
                    : "Для твоей роли обязателен минимум один <action tool=\"run\">";
                SystemMessage?.Invoke("⚠ Финал без профильной работы не принимается — выполняй пункты до конца.");
                prompt = "НАРУШЕНИЕ: ты прислал <final>, но НЕ выполнил свои пункты (одного изучения list_dir/read_file недостаточно).\n" +
                         "Инструменты РЕАЛЬНО работают: отправь <action> — система исполнит его на диске и вернёт настоящий <result>. Никаких ZIP и «недоступно» — это твоя рабочая среда.\n" +
                         req + ".\n" +
                         "Если файла из пункта нет — СОЗДАЙ его с нуля (<action tool=\"write_file\">). CSS/JS/HTML пиши ПОЛНОСТЬЮ, без сокращений и заглушек.\n" +
                         "Выполни ВСЕ свои пункты плана по очереди, затем завершай <final>.";
                continue;
            }
            if (Profile is { Mode: Tools.ToolMode.ReadOnly }) return answer.Trim();
            if (actions.Count == 0)
            {
                // нарушение протокола: текст без действий и без <final> — часто «наслово сделал»,
                // при этом ничего не выполнив. Требуем соблюдать протокол, после 3 раз сдаёмся.
                _protocolStrikes++;
                if (_protocolStrikes >= 3)
                {
                    SystemMessage?.Invoke("⚠ Модель трижды ответила вне протокола — показываю её последний текст как финал.");
                    return answer.Trim();
                }
                SystemMessage?.Invoke("⚠ Модель ответила без действий и без <final> — требую выполнить действия.");
                prompt = "НАРУШЕНИЕ ПРОТОКОЛА: в ответе нет НИ ОДНОГО блока <action …> и нет <final>.\n" +
                         "Следующий ответ начни СРАЗУ с символа «<». Формат: <action tool=\"read_file\" path=\"точный путь из пункта\"/>\n" +
                         "КОД передавай ТОЛЬКО внутри <action tool=\"write_file\"> — markdown-блоки ``` для кода ЗАПРЕЩЕНЫ.\n" +
                         "Никакого текста до/после блоков. Проверь: path — из твоих пунктов плана, тело write_file — реальный код.";
                continue;
            }
            _protocolStrikes = 0;

            var sb = new StringBuilder();
            foreach (var a in actions)
            {
                ct.ThrowIfCancellationRequested();
                var path = a.Attr("path") ?? a.Attr("cwd") ?? "";
                var title = a.Tool == "run"
                    ? $"🔧 run [{path}]: {Truncate(a.Body.Replace("\n", " "), 100)}"
                    : $"🔧 {a.Tool} {path}".Trim();
                ActionStarted?.Invoke(title);
                var actionSw = System.Diagnostics.Stopwatch.StartNew();
                var r = await tools.ExecuteAsync(a);
                actionSw.Stop();
                if (a.Tool == "write_file" && r.Ok && a.Attr("path") is { } changed)
                    _changedFiles.Add(changed);
                ActionDone?.Invoke($"{(r.Ok ? "✓" : "✗")} ({actionSw.Elapsed.TotalSeconds:0.0}с) " + Truncate(r.Output.Split('\n')[0], 120));

                sb.AppendLine($"<result tool=\"{a.Tool}\" path=\"{path}\" ok=\"{(r.Ok ? "true" : "false")}\">");
                sb.AppendLine(r.Output.Length > 6000 ? r.Output[..6000] + "\n…(обрезано)" : r.Output);
                sb.AppendLine("</result>");
            }
            sb.Append("Продолжай (или заверши блоком <final>…</final>).");
            prompt = sb.ToString();
        }

        SystemMessage?.Invoke("Достигнут лимит итераций (25) — задача остановлена.");
        return "Остановлено: лимит итераций.";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Ход с ретраями: ошибка моста при живом соединении (таймаут SW) — один повтор;
    /// ретрайебельные ошибки провайдера (лимит/перегрузка) — пауза 30с и повтор;
    /// обрыв соединения — ждём переподключения (внутри Resilient).
    /// </summary>
    private async Task<JsonElement> SendTurnAsync(BridgeService bridge, string prompt, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            JsonElement result;
            try
            {
                result = await SendPromptResilientAsync(bridge, prompt, ct);
            }
            catch (TimeoutException) when (attempt < 2 && bridge.IsConnected)
            {
                SystemMessage?.Invoke("Ход не завершён (расширение молчит) — повторяю…");
                continue;
            }

            if (result.TryGetProperty("type", out var t) && t.GetString() == "error")
            {
                var code = result.TryGetProperty("code", out var c) ? c.GetString() ?? "error" : "error";
                var msg = result.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var pageText = result.TryGetProperty("data", out var d) && d.TryGetProperty("bodyText", out var bt)
                    ? bt.GetString() : null;
                if (attempt < 2 && code is "timeout" or "rateLimited" or "internal" or "selectorNotFound" or "sendFailed" or "composerFailed")
                {
                    SystemMessage?.Invoke($"Провайдер вернул «{code}» — пауза {(code == "selectorNotFound" ? 15 : 30)}с и повтор…"
                        + (pageText is null ? "" : $" (страница: «{Truncate(pageText, 120)}»)"));
                    await Task.Delay(code == "selectorNotFound" ? 15_000 : 30_000, ct);
                    continue;
                }
                throw new AgentOperationException(code, msg
                    + (pageText is null ? "" : $" | страница: «{Truncate(pageText, 200)}»"));
            }
            return result;
        }
    }

    /// <summary>
    /// Отправка хода с одним ретраем: если расширение отвалилось (Chrome перезапускался,
    /// SW умер) — ждём переподключения до 60с и шлём ещё раз. Если соединение живо,
    /// но ответа нет — пробрасываем таймаут (обработает SendTurnAsync).
    /// </summary>
    private async Task<JsonElement> SendPromptResilientAsync(BridgeService bridge, string prompt, CancellationToken ct)
    {
        const int timeoutMs = 360_000;
        var args = new Dictionary<string, object?> { ["text"] = prompt, ["agent"] = Profile?.Key, ["provider"] = Provider };
        try
        {
            return await bridge.RequestAsync("sendPrompt", args, timeoutMs, ct);
        }
        catch (TimeoutException) when (!bridge.IsConnected)
        {
            SystemMessage?.Invoke("Расширение отключилось — жду переподключения (до 60с) и повторяю ход…");
            for (var i = 0; i < 120 && !bridge.IsConnected; i++)
                await Task.Delay(500, ct);
            if (!bridge.IsConnected) throw;
            return await bridge.RequestAsync("sendPrompt", args, timeoutMs, ct);
        }
    }

    /// <summary>Дерево рабочей папки (2 уровня, без bin/obj/node_modules) для промпта.</summary>
    internal static string BuildDirSummary(string root)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var dir in Directory.EnumerateDirectories(root).Where(d => !ToolExecutor.ShouldSkipName(Path.GetFileName(d))).OrderBy(d => d).Take(20))
            {
                sb.AppendLine("[D] " + Path.GetFileName(dir) + "/");
                foreach (var f in Directory.EnumerateFiles(dir).Take(15))
                    sb.AppendLine("    " + Path.GetFileName(f));
            }
            foreach (var f in Directory.EnumerateFiles(root).Take(20))
                sb.AppendLine(Path.GetFileName(f));
            return sb.Length > 0 ? sb.ToString() : "(пусто)";
        }
        catch (Exception ex)
        {
            return $"(не удалось прочитать: {ex.Message})";
        }
    }
}

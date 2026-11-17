using DevChat.App.Services;

namespace DevChat.App.Agent;

/// <summary>
/// Оркестратор «Команда»: Планировщик → Разработчик → Ревьюер → (правки Разработчиком).
/// У каждого агента своя вкладка DeepSeek со своим контекстом.
/// Состояние (план/этап/изменённые файлы) сохраняется в БД — при сбое «продолжить»
/// стартует с места остановки, а не с начала.
/// </summary>
public sealed class OrchestratorRunner
{
    /// <summary>Этап: «👤 Планировщик приступает», «📋 План готов» и т.п.</summary>
    public event Action<string>? StageStarted;

    /// <summary>Состояние сохранено (план/этап/файлы) — UI обновляет чеклист.</summary>
    public event Action<HistoryService.TeamState>? StateSaved;

    /// <summary>Создать настроенный агент-раннер (события подключает вызывающий).</summary>
    public Func<string, AgentRunner> RunnerFactory { get; set; } = _ => new AgentRunner();

    /// <summary>Скиллы по ролям: planner | dev | reviewer → секция промпта.</summary>
    public required Func<string, (string section, int count)> SkillsForRole { get; init; }

    /// <summary>Специалисты команды (скиллы с role:) — роль → скилл.</summary>
    public Dictionary<string, Services.Skill> Specialists { get; init; } = new();

    /// <summary>Пункт плана с назначенной ролью (или без неё). Терпит и построчный план, и простыню в одну строку.</summary>
    private static List<(string? role, string text)> ParseRoleItems(string plan)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(plan, @"^\s*ПЛАН\s*:?\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // кандидаты «N. [роль]» в любом месте; числа внутри текста отсеет монотонная цепочка
        var candidates = System.Text.RegularExpressions.Regex.Matches(text,
                @"(?:^|\n|\s)(?<n>\d{1,2})[\.\)]\s+(?:\[(?<role>[a-z][a-z0-9-]{1,20})\]\s*)?")
            .Select(m => (num: int.Parse(m.Groups["n"].Value),
                          role: m.Groups["role"].Value.ToLowerInvariant(),
                          start: m.Index + m.Length,
                          itemStart: m.Index))
            .ToList();

        var chain = new List<(int num, string role, int start, int itemStart)>();
        var expected = 1;
        foreach (var c in candidates)
        {
            if (c.num == expected) { chain.Add(c); expected++; }
            else if (c.num == 1 && chain.Count == 0) { chain.Add(c); expected = 2; }
        }

        var result = new List<(string?, string)>();
        for (var i = 0; i < chain.Count; i++)
        {
            var end = i + 1 < chain.Count ? chain[i + 1].itemStart : text.Length;
            var chunk = text[chain[i].start..end].Trim();
            if (chunk.Length > 2)
                result.Add((chain[i].role.Length > 0 ? chain[i].role : null, chunk));
        }
        return result;
    }

    private void Save(HistoryService history, long chatId, string task, string plan, string stage, IEnumerable<string> files)
    {
        var list = files.Distinct().ToList();
        history.SaveTeamState(chatId, task, plan, stage, list);
        StateSaved?.Invoke(new HistoryService.TeamState(task, plan, stage, list));
    }

    /// <summary>«продолжить/дальше/continue» — не несёт новой информации.</summary>
    private static bool IsContinuation(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (t.Length == 0) return true;
        return t.StartsWith("продолж") || t is "дальше" or "continue" or "go on" or "ещё" or "еще" or "давай";
    }

    // ── тайминг этапов и прогона ──
    private readonly System.Diagnostics.Stopwatch _stageSw = new();
    private readonly System.Diagnostics.Stopwatch _totalSw = new();
    private string _stageLabel = "";

    /// <summary>Начать этап: предыдущий закрывается строкой «⏱ … : mm:ss».</summary>
    private void Stage(string label)
    {
        CloseStage();
        _stageLabel = label;
        _stageSw.Restart();
        StageStarted?.Invoke(label + "…");
    }

    private void CloseStage()
    {
        if (!_stageSw.IsRunning) return;
        _stageSw.Stop();
        StageStarted?.Invoke($"⏱ {_stageLabel}: {Fmt(_stageSw.Elapsed)}");
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    public async Task<string> RunTeamAsync(
        BridgeService bridge,
        long chatId,
        string task,
        string workingFolder,
        HistoryService history,
        Func<ActionBlock, string?, Task<bool>> confirm,
        CancellationToken ct,
        HistoryService.TeamState? resume = null)
    {
        var resumable = resume is { Stage: not "done" } ? resume : null;
        // исходная задача берётся из сохранённого состояния; новое сообщение юзера —
        // либо пустое «продолжить», либо дополнительное указание
        var effectiveTask = resumable is { Task.Length: > 0 } ? resumable.Task : task;
        var userNote = resumable is not null && !IsContinuation(task) && task != effectiveTask
            ? $"\n\nДОПОЛНИТЕЛЬНОЕ УКАЗАНИЕ ПОЛЬЗОВАТЕЛЯ (учти при продолжении):\n{task}\n"
            : "";
        _totalSw.Restart();
        try
        {

        // ── план: сохранённый или новый ──
        string plan;
        if (resumable is not null && resumable.Plan.Length > 0)
        {
            plan = resumable.Plan;
            StageStarted?.Invoke("📋 Найден сохранённый план — продолжаю без перепланирования");
        }
        else
        {
            var planner = RunnerFactory("Планировщик");
            var rolesLine = Specialists.Count > 0
                ? $"\nДОСТУПНЫЕ РОЛИ КОМАНДЫ (назначай исполнителя каждому пункту: «N. [роль] действие»):\n{string.Join(", ", Specialists.Keys)}\n"
                : "";
            planner.Profile = AgentProfiles.Planner(workingFolder, AgentRunner.BuildDirSummary(workingFolder), SkillsForRole("planner").section, rolesLine);
            Stage("👤 Планировщик разбирает задачу");
            plan = await planner.RunAsync(bridge, chatId, task, workingFolder, history, confirm, ct);
            StageStarted?.Invoke($"📋 План готов ({plan.Length} симв.)");
            Save(history, chatId, effectiveTask, plan, "dev", resumable?.ChangedFiles ?? Array.Empty<string>());
        }

        var changed = new List<string>(resumable?.ChangedFiles ?? Array.Empty<string>());

        // ── разработка ──
        string devResult;

        // конвейер специалистов: тимлид назначил роли в плане — каждая роль работает своим агентом
        var specItems = ParseRoleItems(plan);
        var specOrder = specItems.Where(i => i.role is not null).Select(i => i.role!).Distinct().ToList();
        var knownOrder = specOrder.Where(Specialists.ContainsKey).ToList();
        if (knownOrder.Count > 0)
        {
            var defaultRole = knownOrder[0]; // пункты без роли достаются первому специалисту
            var resumeRole = resumable?.Stage.StartsWith("spec:") == true ? resumable.Stage["spec:".Length..].ToLowerInvariant() : null;
            var results = new List<string>();
            foreach (var role in knownOrder)
            {
                if (resumeRole is not null && knownOrder.IndexOf(resumeRole) > knownOrder.IndexOf(role))
                    continue; // роль выполнена до прерывания
                var skill = Specialists[role];
                var myItems = specItems
                    .Where(i => i.role == role || (i.role is null && role == defaultRole))
                    .Select(i => i.text).ToList();
                var runner = RunnerFactory(skill.Name);
                runner.Profile = AgentProfiles.Specialist(skill);
                var continuing = resumeRole == role;
                runner.FirstPromptOverride =
                    $"{runner.Profile.Intro}\n\n{(continuing ? "ПРОДОЛЖЕНИЕ ПРЕРВАННОЙ РАБОТЫ.\n" : "")}ЗАДАЧА:\n{effectiveTask}{userNote}\n\nПОЛНЫЙ ПЛАН ТИМЛИДА:\n{plan}\n\nТВОИ ПУНКТЫ:\n{string.Join("\n", myItems.Select((t, i) => $"{i + 1}. {t}"))}\n\nФайлы, изменённые на предыдущих этапах:\n{FileList(changed)}\nВыполни СВОИ пункты{(continuing ? " (часть могла быть уже сделана — не переделывай готовое)" : "")}. Не трогай чужое.";
                Stage($"👤 {skill.Name}: {myItems.Count} пункт(ов)");
                var r = await runner.RunAsync(bridge, chatId, effectiveTask, workingFolder, history, confirm, ct);
                changed.AddRange(runner.ChangedFiles);
                results.Add($"[{skill.Name}]\n{r}");
                // фиксируем СЛЕДУЮЩУЮ роль: «продолжить» не должно повторять выполненный этап
                var idx = knownOrder.IndexOf(role);
                var nextStage = idx + 1 < knownOrder.Count ? $"spec:{knownOrder[idx + 1]}" : "review";
                Save(history, chatId, effectiveTask, plan, nextStage, changed);
                resumeRole = null; // дальше — по порядку
            }
            devResult = string.Join("\n\n", results);
        }
        else if (resumable is null || resumable.Stage == "dev" || resumable.Stage.StartsWith("spec:"))
        {
            var dev = RunnerFactory("Разработчик");
            dev.Profile = AgentProfiles.Developer(workingFolder, AgentRunner.BuildDirSummary(workingFolder), SkillsForRole("dev").section);
            dev.FirstPromptOverride = resumable is null
                ? $"{dev.Profile.Intro}\n\nЗАДАЧА:\n{effectiveTask}{userNote}\n\nПЛАН ПЛАНИРОВЩИКА (следуй ему, поправляй при необходимости):\n{plan}\n\nВАЖНО: НЕ повторяй план словами и не пиши вводного текста. Первым же ответом НАЧНИ ВЫПОЛНЯТЬ. В каждом действии подставляй СВОИ реальные значения вместо заглушек: path — точный путь файла из твоего пункта плана, тело write_file — полное новое содержимое этого файла. Ответ без <action> — ошибка.\nНачинай."
                : $"{dev.Profile.Intro}\n\nПРОДОЛЖЕНИЕ ПРЕРВАННОЙ РАБОТЫ.\nЗАДАЧА:\n{effectiveTask}{userNote}\n\nПЛАН:\n{plan}\n\nУЖЕ СДЕЛАНО РАНЕЕ (файлы):\n{FileList(changed)}\nПрочитай существующие файлы, сверься с планом и доделай оставшееся. Не переделывай готовое.\nВАЖНО: отвечай ТОЛЬКО блоками <action>, без плана текстом.\nНачинай.";
            Stage("🔨 Разработчик реализует план");
            devResult = await dev.RunAsync(bridge, chatId, task, workingFolder, history, confirm, ct);
            changed.AddRange(dev.ChangedFiles);
            Save(history, chatId, effectiveTask, plan, "review", changed);
        }
        else
        {
            StageStarted?.Invoke("🔨 Разработка уже завершена (сохранённое состояние) — перехожу к ревью");
            devResult = "(выполнено в предыдущем прогоне)";
        }

        // ── ревью только если что-то менялось ──
        if (changed.Count == 0)
        {
            StageStarted?.Invoke("ℹ Файлы не менялись — ревью пропущено");
            Save(history, chatId, effectiveTask, plan, "done", changed);
            return $"ПЛАН:\n{plan}\n\nРЕЗУЛЬТАТ РАЗРАБОТЧИКА:\n{devResult}";
        }

        var reviewer = RunnerFactory("Ревьюер");
        reviewer.Profile = AgentProfiles.Reviewer(workingFolder, AgentRunner.BuildDirSummary(workingFolder), SkillsForRole("reviewer").section);
        reviewer.FirstPromptOverride = $"{reviewer.Profile.Intro}\n\nИЗМЕНЁННЫЕ ФАЙЛЫ (проверь каждый):\n{FileList(changed)}\n\nИсходная задача для контекста:\n{effectiveTask}\n\nНачинай проверку.";
        Stage("🔍 Ревьюер проверяет изменения");
        var review = await reviewer.RunAsync(bridge, chatId, task, workingFolder, history, confirm, ct);

        if (review.Contains("ВЕРДИКТ: ОК", StringComparison.OrdinalIgnoreCase))
        {
            StageStarted?.Invoke("✅ Ревью: ОК");
            Save(history, chatId, effectiveTask, plan, "done", changed);
            return $"ПЛАН:\n{plan}\n\nРЕЗУЛЬТАТ РАЗРАБОТЧИКА:\n{devResult}\n\nРЕВЬЮ: ОК ✅";
        }

        // замечания → второй круг разработчика (тот же агент-контекст: вкладка dev сохранена)
        Stage("🛠 Ревью нашёл замечания — разработчик исправляет");
        var fix = RunnerFactory("Разработчик");
        fix.Profile = AgentProfiles.Developer(workingFolder, AgentRunner.BuildDirSummary(workingFolder), SkillsForRole("dev").section);
        // фикс-агент работает в новой вкладке без контекста — интро с описанием инструментов обязательно
        fix.FirstPromptOverride = $"{fix.Profile.Intro}\n\nЗАМЕЧАНИЯ РЕВЬЮЕРА (исправь их, файлы те же):\n{review}\n\nСначала прочитай указанные файлы (<action tool=\"read_file\">), исправь (<action tool=\"write_file\">), затем заверши <final>…</final>.";
        var fixResult = await fix.RunAsync(bridge, chatId, task, workingFolder, history, confirm, ct);
        changed.AddRange(fix.ChangedFiles);
        Save(history, chatId, effectiveTask, plan, "done", changed);

        return $"ПЛАН:\n{plan}\n\nРЕЗУЛЬТАТ РАЗРАБОТЧИКА:\n{devResult}\n\nЗАМЕЧАНИЯ РЕВЬЮЕРА:\n{review}\n\nПОСЛЕ ПРАВОК:\n{fixResult}";
        }
        finally
        {
            CloseStage();
            _totalSw.Stop();
            StageStarted?.Invoke($"⏱ Общее время прогона: {Fmt(_totalSw.Elapsed)}");
        }
    }

    private static string FileList(IEnumerable<string> files) =>
        files.Count() == 0 ? "(пока ничего)" : string.Join("\n", files.Distinct().Select(f => "- " + f));
}

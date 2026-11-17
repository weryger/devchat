using System.IO;
using System.Text;

namespace DevChat.App.Services;

public sealed record Skill(string Name, string Description, string Applies, string Body,
    string? Role = null, Tools.ToolMode ToolMode = Tools.ToolMode.Full, bool Enabled = true, string FileName = "")
{
    public bool IsSpecialist => Role is { Length: > 0 };
}

/// <summary>
/// Скиллы агентов: markdown-файлы в %AppData%\DevChat\skills\.
/// Формат: front-matter (name, description, applies) + тело с правилами и форматом вывода.
/// applies: planner | dev | reviewer | agent (через запятую). Тела подмешиваются в промпт роли.
/// </summary>
public sealed class SkillService
{
    private readonly string _dir;

    public SkillService()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevChat", "skills");
        EnsureDefaults();
    }

    public string SkillsDir => _dir;

    private void EnsureDefaults()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            WriteIfMissing("plan-format.md", """
                ---
                name: plan-format
                description: Формат плана Планировщика — обязательно построчные пункты
                applies: planner
                ---
                # Формат плана (строго)

                План выводи ТОЛЬКО внутри <final>…</final> в виде нумерованного списка:
                - КАЖДЫЙ пункт на СВОЕЙ строке, формат: `N. конкретное действие над конкретным файлом`
                - один пункт = одно атомарное действие (файл + что сделать)
                - если пункт про файл — укажи точный путь файла в пункте
                - никаких простыней в одну строку и вложенных абзацев

                Пример:
                <final>
                ПЛАН:
                1. Прочитать data/pricing.json и зафиксировать текущую схему тарифов
                2. Обновить data/pricing.json: добавить ступени по площади и коэффициенты
                3. Обновить js/calculator-engine.js: ступенчатая формула с минимумом заказа
                </final>
                """);
            WriteIfMissing("coding-standards.md", """
                ---
                name: coding-standards
                description: Стандарты кода для Разработчика
                applies: dev, agent
                ---
                # Стандарты кода

                - кодировка UTF-8 без BOM, переводы строк как в остальных файлах проекта
                - пиши код в стиле существующих файлов проекта (сначала прочитай их!)
                - комментарии — на языке проекта, по делу
                - не оставляй закомментированного мусора и TODO без нужды
                - новые зависимости — только если без них никак, и упомяни это в финале
                - перед завершением проверь синтаксис (для кода — запуск/сборка, если возможно)
                """);
            WriteIfMissing("final-format.md", """
                ---
                name: final-format
                description: Формат итогового резюме Разработчика
                applies: dev
                ---
                # Формат финала

                <final> обязан содержать:
                1. Список файлов: `путь — что сделано` (каждый с новой строки)
                2. Как проверить (команда или шаги)
                3. Ограничения/допущения, если есть
                Без воды и извинений.
                """);
            WriteIfMissing("review-format.md", """
                ---
                name: review-format
                description: Формат вердикта Ревьюера
                applies: reviewer
                ---
                # Формат ревью

                Финал строго одного из двух видов:
                <final>ВЕРДИКТ: ОК</final>
                или
                <final>
                ВЕРДИКТ: ЗАМЕЧАНИЯ
                1. [путь:строка] проблема → что исправить
                </final>
                - замечание = то, что реально ломает или ухудшает; стилистику не выноси, если не критично
                - каждое замечание указывай с файлом и местом
                """);
            WriteIfMissing("agent-ux.md", """
                ---
                name: UX-дизайнер
                description: UX-дизайнер
                role: ux
                applies: specialist
                ---
                # Роль: UX-дизайнер

                Ты продумываешь пользовательские сценарии и логику интерфейса (не визуал).
                - у тебя ЕСТЬ инструмент <action tool="write_file"> — ОБЯЗАН использовать его для фиксации результата
                - РЕЗУЛЬТАТ ОБЯЗАТЕЛЬНО записывай файлом (ui-notes.md или файл, указанный в пункте плана) — по заметкам работают следующие этапы
                - код проекта не изменяй: только читаешь его и пишешь документацию/заметки
                """);
            // v2: раньше ux был readonly и не мог фиксировать заметки — обновляем старый дефолт
            var uxFile = Path.Combine(_dir, "agent-ux.md");
            if (File.Exists(uxFile) && File.ReadAllText(uxFile).Contains("tools: readonly"))
                File.WriteAllText(uxFile, """
                    ---
                    name: UX-дизайнер
                    description: UX-дизайнер
                    role: ux
                    applies: specialist
                    ---
                    # Роль: UX-дизайнер

                    Ты продумываешь пользовательские сценарии и логику интерфейса (не визуал).
                    - опиши сценарии использования, состояния (загрузка/ошибка/пусто), какие данные нужны пользователю
                    - РЕЗУЛЬТАТ ОБЯЗАТЕЛЬНО фиксируй write_file в заметки (ui-notes.md или файл из плана) — по ним работают следующие этапы
                    - код проекта не переписывай: только читаешь его и пишешь документацию/заметки
                    """, new UTF8Encoding(false));
            WriteIfMissing("agent-webdesigner.md", """
                ---
                name: Web-дизайнер
                description: Web-дизайнер (визуал)
                role: design
                applies: specialist
                ---
                # Роль: Web-дизайнер

                Ты отвечаешь за визуальное решение: палитра, типографика, отступы, компоненты.
                - работай в стиле проекта: сначала прочитай существующие CSS/HTML
                - изменения в стили делай аккуратными write_file (CSS/разметка), без слома вёрстки
                - адаптивность обязательна (мобильные экраны), консистентность компонентов
                """);
            WriteIfMissing("agent-frontend.md", """
                ---
                name: Frontend-программист
                description: Frontend-программист
                role: frontend
                applies: specialist
                ---
                # Роль: Frontend-программист

                Ты реализуешь клиентскую часть: HTML/JS/CSS, взаимодействие с API.
                - сначала прочитай существующие файлы и UX-решения, не переделывай чужое
                - чистый код без фреймворков, если их нет в проекте; обработка ошибок ввода
                - изменения вноси минимально необходимыми write_file
                """);
            WriteIfMissing("agent-backend.md", """
                ---
                name: Бэкенд-программист
                description: Бэкенд-программист
                role: backend
                applies: specialist
                ---
                # Роль: Бэкенд-программист

                Ты реализуешь серверную часть и данные: скрипты, API, хранение.
                - данные и логика расчётов — твои; фронтенд только отображает
                - проверяй свой код запуском (run) с простыми входами
                - не трогай визуал, если не просили
                """);
            WriteIfMissing("agent-qa.md", """
                ---
                name: Тестировщик
                description: Тестировщик (QA)
                role: qa
                applies: specialist
                ---
                # Роль: Тестировщик

                Ты проверяешь работу команды: запускаешь, гоняешь сценарии, ловишь баги.
                - писать можешь ТОЛЬКО тестовые файлы (tests/…): <action tool="write_file" path="tests/app.test.js">…</action>
                - остальной код не изменяй; приёмку кода делай через run (node tests/…, python и т.п.)
                - path в action — реальный путь из пунктов плана, не многоточие
                - финал: список проверок «✔/✘ + что увидел»; для ✘ — как воспроизвести
                """);
            // обновляем формат плана тимлида, если дефолт ещё не правился руками
            var planFile = Path.Combine(_dir, "plan-format.md");
            if (File.Exists(planFile) && !File.ReadAllText(planFile).Contains("тимлид"))
                File.WriteAllText(planFile, """
                    ---
                    name: plan-format
                    description: Формат плана тимлида — поэтапно, с назначением исполнителей
                    applies: planner
                    ---
                    # Формат плана (строго)

                    Ты — тимлид: нарежь работу по этапам и назначь исполнителей.
                    - КАЖДЫЙ пункт на СВОЕЙ строке: `N. [роль] конкретное действие над конкретным файлом`
                    - роль — в квадратных скобках ровно в начале пункта, из доступных ролей команды
                    - этапы шли по порядку выполнения (сначала UX/данные, потом код, в конце QA)
                    - один пункт = одно атомарное действие; путь файла указывай точно

                    Пример:
                    <final>
                    ПЛАН:
                    1. [ux] Описать сценарии расчёта и состояния калькулятора в ui-notes.md
                    2. [frontend] Обновить js/calculator-ui.js: поля ввода и live-пересчёт
                    3. [backend] Обновить js/calculator-engine.js: ступенчатая формула
                    4. [design] Обновить style.css: карточка, адаптив
                    5. [qa] Прогнать сценарии и сверить с планом
                    </final>
                    """, new UTF8Encoding(false));
        }
        catch { /* нет прав на запись — работаем с тем, что есть */ }
    }

    private void WriteIfMissing(string file, string content)
    {
        var path = Path.Combine(_dir, file);
        if (!File.Exists(path)) File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public List<Skill> List()
    {
        var result = new List<Skill>();
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_dir, "*.md"))
            {
                var text = File.ReadAllText(file);
                var (meta, body) = SplitFrontMatter(text);
                var role = meta.GetValueOrDefault("role");
                result.Add(new Skill(
                    meta.GetValueOrDefault("name") ?? Path.GetFileNameWithoutExtension(file),
                    meta.GetValueOrDefault("description") ?? "",
                    meta.GetValueOrDefault("applies") ?? (role is { Length: > 0 } ? "specialist" : "agent"),
                    body.Trim(),
                    role,
                    ParseToolMode(meta.GetValueOrDefault("tools")),
                    !string.Equals(meta.GetValueOrDefault("enabled"), "false", StringComparison.OrdinalIgnoreCase),
                    Path.GetFileName(file)));
            }
        }
        catch { /* папка недоступна */ }
        return result;
    }

    /// <summary>Записать скилл-агента в файл (front-matter + тело).</summary>
    public void Save(Skill skill)
    {
        var name = string.IsNullOrWhiteSpace(skill.FileName) ? Slug(skill.Name) + ".md" : skill.FileName;
        var tools = skill.ToolMode switch { Tools.ToolMode.ReadOnly => "readonly", Tools.ToolMode.RunNoWrite => "run", _ => "full" };
        var content = $"""
            ---
            name: {skill.Name}
            description: {skill.Description}
            {(skill.Role is { Length: > 0 } ? $"role: {skill.Role}\napplies: specialist" : $"applies: {skill.Applies}")}
            tools: {tools}
            enabled: {(skill.Enabled ? "true" : "false")}
            ---
            {skill.Body}
            """;
        File.WriteAllText(Path.Combine(_dir, name), content, new UTF8Encoding(false));
    }

    public void Delete(string fileName) => File.Delete(Path.Combine(_dir, fileName));

    /// <summary>Импорт готового файла скилла в папку агентов.</summary>
    public string? Import(string sourcePath)
    {
        var target = Path.Combine(_dir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, target, overwrite: true);
        return Path.GetFileName(target);
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? ch : '-');
        var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug.Length > 0 ? slug : "agent";
    }

    private static Tools.ToolMode ParseToolMode(string? s) => s switch
    {
        "readonly" => Tools.ToolMode.ReadOnly,
        "run" => Tools.ToolMode.RunNoWrite,
        _ => Tools.ToolMode.Full,
    };

    /// <summary>Специалисты команды (скиллы с role: и enabled=true) — роль → скилл.</summary>
    public Dictionary<string, Skill> ListSpecialists() =>
        List().Where(s => s.IsSpecialist && s.Enabled)
            .GroupBy(s => s.Role!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Секция промпта со скиллами для роли (пустая, если скиллов нет).</summary>
    public (string section, int count) BuildSection(params string[] roles)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var s in List())
        {
            var applies = s.Applies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!applies.Intersect(roles).Any()) continue;
            if (sb.Length + s.Body.Length > 8_000) break; // бюджет промпта
            sb.AppendLine($"### Скилл: {s.Name}");
            sb.AppendLine(s.Body);
            sb.AppendLine();
            count++;
        }
        return (count == 0 ? "" : $"\n\nПРАВИЛА РАБОТЫ (скиллы — обязательны к исполнению):\n{sb}", count);
    }

    private static (Dictionary<string, string> meta, string body) SplitFrontMatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith("---")) return (meta, text);
        var end = text.IndexOf("\n---", 4);
        if (end < 0) return (meta, text);
        var fm = text[4..end];
        var body = text[(end + 4)..].TrimStart('-');
        foreach (var line in fm.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx > 0) meta[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return (meta, body.TrimStart('\r', '\n'));
    }
}

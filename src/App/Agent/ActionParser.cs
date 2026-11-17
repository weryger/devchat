using System.Text.RegularExpressions;

namespace DevChat.App.Agent;

/// <summary>Одно действие из ответа модели: tool + атрибуты + тело.</summary>
public sealed record ActionBlock(string Tool, Dictionary<string, string> Attrs, string Body)
{
    public string? Attr(string name) => Attrs.GetValueOrDefault(name);
}

/// <summary>Разбор ответа модели на блоки &lt;action&gt; и &lt;final&gt;.</summary>
public static class ActionParser
{
    private static readonly Regex ActionRe = new(
        @"<action\s+tool=""(?<tool>\w+)""(?<attrs>(?:\s+\w+=""[^""]*"")*)\s*(?:/>|>(?<body>[\s\S]*?)</action\s*>)",
        RegexOptions.Compiled);

    private static readonly Regex AttrRe = new(@"(?<name>\w+)=""(?<value>[^""]*)""", RegexOptions.Compiled);

    private static readonly Regex FinalRe = new(@"<final>(?<text>[\s\S]*?)</final\s*>", RegexOptions.Compiled);

    public static List<ActionBlock> ParseActions(string text)
    {
        var list = new List<ActionBlock>();
        foreach (Match m in ActionRe.Matches(text))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match am in AttrRe.Matches(m.Groups["attrs"].Value))
                attrs[am.Groups["name"].Value] = am.Groups["value"].Value;
            list.Add(new ActionBlock(m.Groups["tool"].Value, attrs, m.Groups["body"].Value.Trim('\r', '\n')));
        }
        return list;
    }

    public static string? ParseFinal(string text)
    {
        var m = FinalRe.Match(text);
        return m.Success ? m.Groups["text"].Value.Trim() : null;
    }
}

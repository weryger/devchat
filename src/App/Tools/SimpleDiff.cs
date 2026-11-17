namespace DevChat.App.Tools;

/// <summary>Простейший построчный unified diff (LCS). Для больших файлов — заглушка.</summary>
public static class SimpleDiff
{
    private const int MaxLines = 1500; // выше — LCS-матрица слишком тяжёлая

    public static string Unified(string oldText, string newText)
    {
        var a = oldText.Replace("\r\n", "\n").Split('\n');
        var b = newText.Replace("\r\n", "\n").Split('\n');
        if (a.Length > MaxLines || b.Length > MaxLines)
            return $"[файл большой: {a.Length} → {b.Length} строк; дифф не показан]";

        var lcs = BuildLcs(a, b);
        var sb = new System.Text.StringBuilder();
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && lcs[i + 1, j + 1] == lcs[i, j] + 1 && a[i] == b[j])
            {
                Append(sb, " ", a[i]);
                i++; j++;
            }
            else if (j < b.Length && (i >= a.Length || lcs[i, j + 1] >= lcs[i + 1, j]))
            {
                Append(sb, "+", b[j]);
                j++;
            }
            else if (i < a.Length)
            {
                Append(sb, "-", a[i]);
                i++;
            }
            else break;
        }
        return sb.ToString();
    }

    private static int[,] BuildLcs(string[] a, string[] b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        return dp;
    }

    private static void Append(System.Text.StringBuilder sb, string prefix, string line)
    {
        if (sb.Length < 12_000)
            sb.Append(prefix).Append(' ').AppendLine(line);
        else if (sb.Length < 12_100)
            sb.AppendLine("… (дифф обрезан)");
    }
}

using System.Globalization;

namespace RimRunner;

/// <summary>按 Keyed 模板从已渲染文本回读实参。</summary>
/// <remarks>按行整行匹配：模板的定值段须从行首排到行尾；numericArgIndex 那一位解析不成数的行不算命中。</remarks>
public static class RimRunnerTranslatedLine
{
    private const string SentinelOpen = "@@VTRARG";
    private const string SentinelClose = "@@";

    /// <summary>一次回读命中：行号 + 实参 + 数值。</summary>
    public readonly struct Match(int lineIndex, string[] args, float number)
    {
        /// <summary>命中行在文本里的行号。</summary>
        public int LineIndex { get; } = lineIndex;

        /// <summary>回读出的各实参。</summary>
        public string[] Args { get; } = args;

        /// <summary>numericArgIndex 那一位解析出的数。</summary>
        public float Number { get; } = number;
    }

    /// <summary>回读全部：该 key 的行每出现一次产一条命中，按出现顺序。</summary>
    /// <remarks>数值位认带正负号的小数，可带 % 后缀（除以 100）或 ×、x、* 前缀。模板占位符可重排；numericArgIndex 指模板里的实参序号，不是行内位置。</remarks>
    /// <exception cref="InvalidOperationException">text 非空而模板渲染出的实参占位数不等于 argCount 时抛。key 不存在时占位数为 0。</exception>
    public static List<Match> ReadAll(string text, string key, int argCount, int numericArgIndex)
    {
        List<Match> results = [];
        if (text.NullOrEmpty()) return results;

        List<string> literals = TemplateSkeleton(key, argCount, out int[] argOrder);
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!TryMatchLine(lines[i], literals, argOrder, argCount, out string[]? args)) continue;
            if (!TryParseNumber(args[numericArgIndex], out float number)) continue;
            results.Add(new Match(i, args, number));
        }

        return results;
    }

    /// <summary>恰好出现一次时返回命中，出现零次或多次时返回 false。</summary>
    public static bool TryReadSingle(
        string text, string key, int argCount, int numericArgIndex, out Match match)
    {
        List<Match> all = ReadAll(text, key, argCount, numericArgIndex);
        if (all.Count == 1)
        {
            match = all[0];
            return true;
        }

        match = default;
        return false;
    }

    /// <summary>该 key 的行在文本里是否出现过。</summary>
    public static bool Contains(string text, string key, int argCount, int numericArgIndex) =>
        ReadAll(text, key, argCount, numericArgIndex).Count > 0;

    private static bool TryMatchLine(
        string line, List<string> literals, int[] argOrder, int argCount,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string[]? args)
    {
        args = null;
        string trimmed = line.TrimEnd('\r');
        if (!trimmed.StartsWith(literals[0], StringComparison.Ordinal)) return false;

        string[] found = new string[argCount];
        int scan = literals[0].Length;
        for (int i = 0; i < argOrder.Length; i++)
        {
            string next = literals[i + 1];
            bool last = i == argOrder.Length - 1;
            int end;
            if (last)
            {
                if (next.Length == 0)
                {
                    end = trimmed.Length;
                }
                else
                {
                    end = trimmed.LastIndexOf(next, StringComparison.Ordinal);
                    if (end < scan || end + next.Length != trimmed.Length) return false;
                }
            }
            else
            {
                end = trimmed.IndexOf(next, scan, StringComparison.Ordinal);
                if (end < 0) return false;
            }

            found[argOrder[i]] = trimmed[scan..end];
            scan = end + next.Length;
        }

        args = found;
        return true;
    }

    private static bool TryParseNumber(string raw, out float value)
    {
        string s = raw?.Trim() ?? string.Empty;
        bool percent = s.EndsWith("%", StringComparison.Ordinal);
        if (percent) s = s[..^1];
        if (s.StartsWith("×", StringComparison.Ordinal)
            || s.StartsWith("x", StringComparison.Ordinal)
            || s.StartsWith("*", StringComparison.Ordinal))
        {
            s = s[1..];
        }

        s = s.Trim();
        if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (percent) value /= 100f;
        return true;
    }

    private static List<string> TemplateSkeleton(string key, int argCount, out int[] argOrder)
    {
        NamedArgument[] args = new NamedArgument[argCount];
        for (int i = 0; i < argCount; i++)
        {
            args[i] = SentinelOpen + i.ToString(CultureInfo.InvariantCulture) + SentinelClose;
        }

        string rendered = key.Translate(args);
        List<string> literals = [];
        List<int> order = [];
        int cursor = 0;
        while (true)
        {
            int open = rendered.IndexOf(SentinelOpen, cursor, StringComparison.Ordinal);
            if (open < 0) break;
            int close = rendered.IndexOf(
                SentinelClose, open + SentinelOpen.Length, StringComparison.Ordinal);
            if (close < 0) break;
            literals.Add(rendered[cursor..open]);
            order.Add(int.Parse(
                rendered.Substring(open + SentinelOpen.Length, close - open - SentinelOpen.Length),
                CultureInfo.InvariantCulture));
            cursor = close + SentinelClose.Length;
        }

        literals.Add(rendered[cursor..]);
        argOrder = order.ToArray();
        if (argOrder.Length != argCount)
        {
            throw new InvalidOperationException(
                RimRunnerLog.Prefix + " Keyed 模板 " + key + " 渲染后只解析出 " + argOrder.Length
                + " 个实参占位（期望 " + argCount + "）");
        }

        return literals;
    }
}

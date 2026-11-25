using System.Text.RegularExpressions;

namespace RimRunnerFixtureIndex;

internal static class DocForm
{
    private static readonly Regex TagLine = new("^<(\\w+)(?:\\s+(name|cref)=\"([^\"]+)\")?>(.*)</\\1>$", RegexOptions.Compiled);

    internal static Doc Parse(List<(int Line, string Text)> docs, string where, List<string> problems, List<string> forms, string[] allowedTags)
    {
        var doc = new Doc();
        if (docs.Count == 0)
        {
            forms.Add($"{where}：缺一条 \"/// <summary>…。</summary>\"");
            return doc;
        }

        for (int index = 0; index < docs.Count; index++)
        {
            Match hit = TagLine.Match(docs[index].Text);
            if (!hit.Success)
            {
                problems.Add($"{where}：/// 第 {index + 1} 行不是单行完整标签——每行写一个 \"<标签>…。</标签>\"");
                continue;
            }
            string tag = hit.Groups[1].Value;
            string attribute = hit.Groups[2].Value;
            string key = hit.Groups[3].Value;
            string body = hit.Groups[4].Value.Trim().Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
            if (index == 0 && tag != "summary")
            {
                forms.Add($"{where}：首行须是 <summary>");
                continue;
            }

            string? wantAttribute = tag switch { "param" => "name", "exception" => "cref", _ => "" };
            int max = tag switch { "summary" => FixtureScanner.SummaryMax, "remarks" => FixtureScanner.RemarksMax, _ => FixtureScanner.ShortTagMax };
            if (!allowedTags.Contains(tag))
            {
                problems.Add($"{where}：不认 <{tag}>——只许 {string.Join(" / ", allowedTags)}");
                continue;
            }
            if (attribute != wantAttribute)
            {
                problems.Add(wantAttribute == ""
                    ? $"{where}：<{tag}> 不带属性"
                    : $"{where}：<{tag}> 须带 {wantAttribute}=\"…\"");
                continue;
            }
            if (!body.EndsWith('。')) forms.Add($"{where}：<{tag}> 须以「。」结尾");
            if (body.Length > max) forms.Add($"{where}：<{tag}> {body.Length} 字，上限 {max}");

            bool duplicate = tag switch
            {
                "summary" => doc.Summary != null,
                "remarks" => doc.Remarks != null,
                "returns" => doc.Returns != null,
                "param" => doc.Params.Any(p => p.Name == key),
                _ => doc.Exceptions.Any(e => e.Cref == key),
            };
            if (duplicate)
            {
                problems.Add($"{where}：<{tag}{(key == "" ? "" : $" {attribute}=\"{key}\"")}> 重复");
                continue;
            }
            switch (tag)
            {
                case "summary": doc.Summary = body; break;
                case "remarks": doc.Remarks = body; break;
                case "returns": doc.Returns = body; break;
                case "param": doc.Params.Add((key, body)); break;
                default: doc.Exceptions.Add((key, body)); break;
            }
        }
        return doc;
    }
}

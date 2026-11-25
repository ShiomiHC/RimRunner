using System.Text.RegularExpressions;
using System.Xml;

namespace RimRunnerCli;

public static class BaselineHeaderGate
{
    public sealed record Diff(IReadOnlyList<string> Missing, IReadOnlyList<string> Extra)
    {
        public bool Empty => Missing.Count == 0 && Extra.Count == 0;
    }

    public static string? Verdict(OrchestratorDescriptor descriptor, string savePath, ModListDefinition definition)
    {
        if (!File.Exists(savePath))
        {
            return null;
        }

        IReadOnlyList<string> header = ReadHeaderModIds(savePath);
        Diff diff = Compare(header, definition.ModIds, descriptor.TestMod.IdPrefix);
        if (diff.Empty)
        {
            CliLog.Info($"[名单] 基线档 {savePath} 的存档头与 mod 名单 {definition.Name} 比对通过");
            return null;
        }

        var parts = new List<string>();
        if (diff.Missing.Count > 0)
        {
            parts.Add("名单有、存档头没有：" + string.Join("、", diff.Missing));
        }

        if (diff.Extra.Count > 0)
        {
            parts.Add("存档头有、名单没有：" + string.Join("、", diff.Extra));
        }

        return $"基线档 {savePath} 的存档头与 mod 名单 {definition.Name} 不匹配（{string.Join("；", parts)}）";
    }

    public static Diff Compare(IEnumerable<string> headerIds, IReadOnlyList<string> modListIds, string testModPrefix)
    {
        var testMod = new Regex("^" + Regex.Escape(testModPrefix) + @"\.\d+$", RegexOptions.IgnoreCase);
        List<string> header = [.. headerIds.Where(id => !testMod.IsMatch(id))];
        return new Diff(
            [.. modListIds.Where(id => !header.Contains(id, StringComparer.OrdinalIgnoreCase))],
            [.. header.Where(id => !modListIds.Contains(id, StringComparer.OrdinalIgnoreCase))]);
    }

    public static IReadOnlyList<string> ReadHeaderModIds(string savePath)
    {
        var ids = new List<string>();
        using XmlReader reader = XmlReader.Create(savePath, new XmlReaderSettings { IgnoreComments = true });
        if (!reader.ReadToFollowing("meta"))
        {
            throw new StoreException($"存档缺少 /savegame/meta：{savePath}");
        }

        using XmlReader meta = reader.ReadSubtree();
        if (meta.ReadToFollowing("modIds"))
        {
            using XmlReader list = meta.ReadSubtree();
            list.Read();
            while (!list.EOF)
            {
                if (list.NodeType == XmlNodeType.Element && list.Name == "li")
                {
                    string id = list.ReadElementContentAsString().Trim();
                    if (id.Length > 0)
                    {
                        ids.Add(id);
                    }

                    continue;
                }

                list.Read();
            }
        }

        return ids;
    }
}

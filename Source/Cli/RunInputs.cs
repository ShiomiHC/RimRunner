using System.Text;

namespace RimRunnerCli;

public static class RunInputs
{
    public const int MaxTotalBytes = 2 * 1024 * 1024;

    public sealed record Entry(string Path, string Text, string Origin);

    public static IReadOnlyList<Entry>? Resolve(IReadOnlyList<string> raws, Func<string, string> fullPath,
        out string? problem)
    {
        problem = null;
        var entries = new List<Entry>();
        foreach (string raw in raws)
        {
            int split = raw.IndexOf('=');
            if (split <= 0)
            {
                problem = $"--input 写法 <数据根下的相对路径>=<文件或正文>，收到：{raw}";
                return null;
            }

            string? path = RimRunner.RimRunnerWireKeys.RunRequest.NormalizeInputPath(raw[..split], out string? pathProblem);
            if (path == null)
            {
                problem = $"--input 的路径{pathProblem}：{raw[..split]}";
                return null;
            }

            if (entries.Any(entry => entry.Path == path))
            {
                problem = $"--input 的路径重复：{path}";
                return null;
            }

            string value = raw[(split + 1)..];
            string trimmed = value.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                entries.Add(new Entry(path, value, "命令行正文"));
                continue;
            }

            if (value.Length == 0)
            {
                problem = $"--input {path} 缺少文件或正文";
                return null;
            }

            string file = fullPath(value);
            if (!File.Exists(file))
            {
                problem = $"--input {path} 的文件不存在：{file}";
                return null;
            }

            try
            {
                entries.Add(new Entry(path, File.ReadAllText(file), file));
            }
            catch (IOException exception)
            {
                problem = $"--input {path}：无法读取文件 {file}；{exception.Message}";
                return null;
            }
        }

        long total = entries.Sum(entry => (long)Encoding.UTF8.GetByteCount(entry.Text));
        if (total > MaxTotalBytes)
        {
            problem = $"--input 正文合计 {total} 字节，超过上限 {MaxTotalBytes}";
            return null;
        }

        return entries;
    }
}

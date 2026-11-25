namespace RimRunnerCli;

public sealed record BaselineSave(string Name, string SourcePath, bool Custom)
{
    public string SourceDir => Path.GetDirectoryName(SourcePath)!;

    public static BaselineSave Default(OrchestratorDescriptor descriptor, string productRoot, string? saveOwner)
    {
        string name = descriptor.BaselineSaveNameFor(saveOwner);
        return new BaselineSave(name, Path.Combine(descriptor.ResolveSavesDir(productRoot), name + ".rws"), Custom: false);
    }

    public static BaselineSave FromPath(string fullPath) =>
        new(Path.GetFileNameWithoutExtension(fullPath), fullPath, Custom: true);

    public static string PathOf(string raw, OrchestratorDescriptor descriptor, string productRoot)
    {
        bool bareName = raw.IndexOfAny(['/', '\\']) < 0
                        && !raw.EndsWith(".rws", StringComparison.OrdinalIgnoreCase);
        return bareName
            ? Path.Combine(descriptor.ResolveSavesDir(productRoot), raw + ".rws")
            : CommandContext.FullPath(raw);
    }

    public static string? TryResolve(string raw, OrchestratorDescriptor descriptor, string productRoot,
        out BaselineSave? save)
    {
        save = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "--save 的值为空";
        }

        string path = PathOf(raw, descriptor, productRoot);
        if (Directory.Exists(path))
        {
            return $"--save 指向的是目录：{path}";
        }

        if (!File.Exists(path))
        {
            return $"--save 指定的档不存在：{path}";
        }

        if (path.Contains('='))
        {
            return $"--save 的路径含 `=`，下传给游戏时会被整条丢弃：{path}";
        }

        save = FromPath(path);
        return null;
    }
}

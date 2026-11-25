namespace RimRunnerCli;

public static class SaveExport
{
    public const string StagingPrefix = "RimRunnerExport-";
    private const string Tag = "save";

    public sealed record Result(string Path, long Bytes, bool Overwrote);

    public sealed class HeaderVerificationException(string message, bool rewriteNotApplied) : StoreException(message)
    {
        public bool RewriteNotApplied { get; } = rewriteNotApplied;
    }

    public static string StagingName() => StagingPrefix + Guid.NewGuid().ToString("N")[..8];

    public static string OutSavesDir(OrchestratorDescriptor descriptor, string productRoot) =>
        Path.Combine(descriptor.ResolveArtifactsDir(productRoot), RimRunner.RimRunnerDataLayout.ArtifactsSavesDirName);

    public static string? RejectName(string name)
    {
        if (name.IndexOfAny(['/', '\\']) >= 0 || name.Contains("..") || Path.IsPathRooted(name))
            return "存档名须是纯文件名，不含路径分隔符、`..` 或盘符；收到：" + name;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "存档名含文件名非法字符；收到：" + name;
        return null;
    }

    public static Result Finish(string stagedPath, string outSavesDir, string name,
        ClaimRecord claim, string testModId, Action<string>? log)
    {
        Directory.CreateDirectory(outSavesDir);
        string workName = Path.GetFileNameWithoutExtension(stagedPath);
        string work = Path.Combine(outSavesDir, workName + ".rws");
        string target = Path.Combine(outSavesDir, name + ".rws");
        bool overwrote = File.Exists(target);
        try
        {
            File.Copy(stagedPath, work, overwrite: true);
            BuildSaveFlow.Scrub(outSavesDir, workName, Tag);
            try
            {
                BuildSaveFlow.VerifyOutwardModIds(outSavesDir, workName, testModId, claim.PackageIds);
            }
            catch (BuildSaveFlow.OutwardModIdsException exception)
            {
                throw new HeaderVerificationException(exception.Message, exception.RewriteNotApplied);
            }

            File.Move(work, target, overwrite: true);
        }
        catch (Exception exception) when (exception is StoreException or IOException
                                              or UnauthorizedAccessException or System.Xml.XmlException)
        {
            try { File.Delete(work); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            string message = exception.Message + Environment.NewLine
                + $"  未导出；实例里的原档留在 {stagedPath}";
            throw exception is HeaderVerificationException header
                ? new HeaderVerificationException(message, header.RewriteNotApplied)
                : new StoreException(message);
        }

        File.Delete(stagedPath);
        return new Result(target, new FileInfo(target).Length, overwrote);
    }
}

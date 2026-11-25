namespace RimRunnerCli;

public sealed class GameCache(string? root = null)
{
    public const string TextureCacheDir = "TextureCache";
    public static readonly IReadOnlyList<string> Dirs = [TextureCacheDir, "ShaderCache"];

    public sealed record Holder(int Number, int? GamePid);

    public static string DefaultRoot => Path.Combine(MachineRoot.Resolve(), "cache");

    public string Root { get; } = root ?? DefaultRoot;

    private IDisposable Lock() =>
        Leases.Lock("game-cache:" + PathIdentity.Normalize(Root).ToUpperInvariant(), TimeSpan.FromSeconds(30), "游戏缓存锁");

    public static Func<ClaimRecord, Holder?> LiveProbe(string claimRoot)
    {
        var processes = new Lazy<List<GameProcessProbe.GameProcess>>(GameProcessProbe.Enumerate);
        var machine = new Lazy<MachineSnapshot?>(MachineSnapshot.Read);
        return claim =>
        {
            if (InstanceProbe.Facts(claim.Number, processes.Value, machine.Value) is { } facts
                && ProcessIdentity.GameAlive(facts.Pid, facts.StartedAt))
                return new Holder(claim.Number, facts.Pid);
            return ColdStartLock.Held(claimRoot, claim.Number) ? new Holder(claim.Number, null) : null;
        };
    }

    public void Bind(ClaimRecord claim, bool privateTextureCache = false)
    {
        using IDisposable guard = Lock();
        foreach (string dir in Dirs)
        {
            if (privateTextureCache && dir == TextureCacheDir) continue;
            string target = Path.Combine(Root, dir);
            Directory.CreateDirectory(target);
            Junction.Create(Path.Combine(claim.SaveData, dir), target);
        }
    }

    public List<Holder> Holders(IReadOnlyList<ClaimRecord> claims, Func<ClaimRecord, Holder?> holderOf) =>
    [
        .. claims.Where(claim => Links(claim.SaveData))
            .Select(holderOf)
            .OfType<Holder>()
            .OrderBy(holder => holder.Number)
    ];

    public bool Links(string saveData) =>
        Dirs.Any(dir =>
        {
            string link = Path.Combine(saveData, dir);
            return Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is string target
                   && PathIdentity.Same(Path.GetDirectoryName(PathIdentity.Normalize(target))!, Root);
        });

    public (long? Freed, List<Holder> Held) Clear(IReadOnlyList<ClaimRecord> claims, Func<ClaimRecord, Holder?> holderOf)
    {
        using IDisposable guard = Lock();
        List<Holder> held = Holders(claims, holderOf);
        if (held.Count > 0) return (null, held);
        long size = SizeOf(Root);
        foreach (string dir in Dirs)
        {
            string path = Path.Combine(Root, dir);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }

        return (size, held);
    }

    public long Size() => SizeOf(Root);

    private static long SizeOf(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
            : 0;
}

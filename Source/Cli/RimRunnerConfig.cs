using System.Globalization;
using RimTooling.IO;

namespace RimRunnerCli;

public static class RimRunnerConfig
{
    public const string MachineFileName = "config.toml";

    public const string GameExeKey = "gameExe";
    public const string FfmpegPathKey = "ffmpegPath";
    public const string BusyCapKey = "busyCap";
    public const string BusyReserveKey = "busyReserve";
    public const string VramHeadroomKey = "vramHeadroomMb";
    public const string LoadBudgetKey = "loadBudgetMb";
    public const string MemHeadroomKey = "memHeadroomMb";
    public const string InstanceMemKey = "instanceMemMb";
    public const string InstancesTable = "instances";
    public const string FilmTable = "film";
    public const string ShardIdleKey = "shardIdleMinutes";
    public const string BatchIdleKey = "batchIdleMinutes";
    public const string FilmIdleKey = "filmIdleMinutes";
    public const string ShowcaseIdleKey = "showcaseIdleMinutes";
    public const string LiveGraceKey = "liveGraceMinutes";
    public const string LiveWaitKey = "liveWaitMinutes";
    public const string LiveAfterWaitKey = "liveAfterWait";
    public const string LiveLimitKey = "liveLimitMinutes";
    public const string KeepRecentKey = "keepRecent";
    public const string KeepTaggedKey = "keepTagged";
    public const string KeepFramesRunsKey = "keepFramesRuns";

    private static readonly string[] TopKeys =
        [GameExeKey, FfmpegPathKey, BusyCapKey, BusyReserveKey, VramHeadroomKey, LoadBudgetKey, MemHeadroomKey, InstanceMemKey];

    private static readonly string[] InstanceKeys =
        [ShardIdleKey, BatchIdleKey, FilmIdleKey, ShowcaseIdleKey, LiveGraceKey, LiveWaitKey, LiveAfterWaitKey, LiveLimitKey];

    private static readonly string[] FilmKeys = [KeepRecentKey, KeepTaggedKey, KeepFramesRunsKey];

    public static IReadOnlyList<string> MachineKeys { get; } = [.. TopKeys, InstancesTable, FilmTable];

    public static string MachinePath => Path.Combine(MachineRoot.Resolve(), MachineFileName);

    public static MachineConfig Machine() => Once(MachinePath, MachineConfig.Load);

    private static T Once<T>(string path, Func<string, T> load) where T : class =>
        CommandContext.Current is { } context ? context.Once(path, () => load(path)) : load(path);

    public static long Bytes(long megabytes) => megabytes * 1_000_000;

    public readonly record struct Setting<T>(T Value, bool FromFile);

    public static string SourceTag(bool fromFile) => fromFile ? "（已配置）" : "（缺省）";

    public sealed record MachineConfig(
        bool FileExists,
        Setting<string> GameExe,
        Setting<string> FfmpegPath,
        Setting<int> BusyCap,
        Setting<double> BusyReserve,
        Setting<long> VramHeadroomMb,
        Setting<long> LoadBudgetMb,
        Setting<long> MemHeadroomMb,
        Setting<long> InstanceMemMb,
        Setting<int> ShardIdleMinutes,
        Setting<int> BatchIdleMinutes,
        Setting<int> FilmIdleMinutes,
        Setting<int> ShowcaseIdleMinutes,
        Setting<int> LiveGraceMinutes,
        Setting<int> LiveWaitMinutes,
        Setting<string> LiveAfterWait,
        Setting<int> LiveLimitMinutes,
        Setting<int> KeepRecent,
        Setting<int> KeepTagged,
        Setting<int> KeepFramesRuns)
    {
        public static MachineConfig Load(string path)
        {
            (bool exists, TomlTable table) = ReadFile(path, MachineFileName);
            if (table.ContainsKey("busyHeadroom"))
                throw new StoreException($"{MachineFileName} 的 busyHeadroom 已停用，改用 busyReserve；文件 {path}");
            RejectUnknown(table, MachineFileName, path, [.. TopKeys, InstancesTable, FilmTable]);
            string instancesWhere = $"{MachineFileName} 的 [{InstancesTable}]";
            string filmWhere = $"{MachineFileName} 的 [{FilmTable}]";
            TomlTable instances = Table(table, MachineFileName, path, InstancesTable);
            TomlTable film = Table(table, MachineFileName, path, FilmTable);
            RejectUnknown(instances, instancesWhere, path, InstanceKeys);
            RejectUnknown(film, filmWhere, path, FilmKeys);
            return new MachineConfig(exists,
                AbsolutePath(table, MachineFileName, path, GameExeKey),
                AbsolutePath(table, MachineFileName, path, FfmpegPathKey),
                Integer(table, MachineFileName, path, BusyCapKey, RimRunnerCli.BusyCap.Default()),
                Number(table, MachineFileName, path, BusyReserveKey, RimRunnerCli.BusyReserve.DefaultCores),
                Megabytes(table, MachineFileName, path, VramHeadroomKey, AdmissionGate.DefaultHeadroomMb),
                Megabytes(table, MachineFileName, path, LoadBudgetKey, AdmissionGate.DefaultLoadBudgetMb),
                Megabytes(table, MachineFileName, path, MemHeadroomKey, MemoryAccount.DefaultHeadroomMb),
                Megabytes(table, MachineFileName, path, InstanceMemKey, MemoryAccount.DefaultInstanceMb),
                Integer(instances, instancesWhere, path, ShardIdleKey, IdleExit.ShardDefault),
                Integer(instances, instancesWhere, path, BatchIdleKey, IdleExit.BatchDefault),
                Integer(instances, instancesWhere, path, FilmIdleKey, IdleExit.FilmDefault),
                Integer(instances, instancesWhere, path, ShowcaseIdleKey, IdleExit.ShowcaseDefault),
                Integer(instances, instancesWhere, path, LiveGraceKey, LiveHolds.GraceDefault),
                Integer(instances, instancesWhere, path, LiveWaitKey, LiveDriveClient.WaitMinutesDefault),
                Choice(instances, instancesWhere, path, LiveAfterWaitKey,
                    [LiveDriveClient.AfterWaitBackground, LiveDriveClient.AfterWaitCancel], LiveDriveClient.AfterWaitBackground),
                Integer(instances, instancesWhere, path, LiveLimitKey, LiveDriveClient.LimitMinutesDefault),
                Integer(film, filmWhere, path, KeepRecentKey, FilmRetention.Quota.DefaultKeepRecent),
                Integer(film, filmWhere, path, KeepTaggedKey, FilmRetention.Quota.DefaultKeepTagged),
                Integer(film, filmWhere, path, KeepFramesRunsKey, FilmRetention.Quota.DefaultKeepFramesRuns));
        }

        public FilmRetention.Quota Film => new(KeepRecent.Value, KeepTagged.Value, KeepFramesRuns.Value);

        public IReadOnlyList<string> Effective() =>
        [
            $"{GameExeKey} {PathShown(GameExe)}",
            $"{FfmpegPathKey} {(IsUnset(FfmpegPath) ? "（未配置，在 PATH 上找）" : FfmpegPath.Value + Source(true))}",
            $"{BusyCapKey} {BusyCap.Value}{Source(BusyCap.FromFile)}",
            $"{BusyReserveKey} {Shown(BusyReserve.Value)}{Source(BusyReserve.FromFile)}",
            $"{VramHeadroomKey} {VramHeadroomMb.Value}{Source(VramHeadroomMb.FromFile)}",
            $"{LoadBudgetKey} {LoadBudgetMb.Value}{Source(LoadBudgetMb.FromFile)}",
            $"{MemHeadroomKey} {MemHeadroomMb.Value}{Source(MemHeadroomMb.FromFile)}",
            $"{InstanceMemKey} {InstanceMemMb.Value}{Source(InstanceMemMb.FromFile)}",
            $"{InstancesTable}.{ShardIdleKey} {ShardIdleMinutes.Value}{Source(ShardIdleMinutes.FromFile)}",
            $"{InstancesTable}.{BatchIdleKey} {BatchIdleMinutes.Value}{Source(BatchIdleMinutes.FromFile)}",
            $"{InstancesTable}.{FilmIdleKey} {FilmIdleMinutes.Value}{Source(FilmIdleMinutes.FromFile)}",
            $"{InstancesTable}.{ShowcaseIdleKey} {ShowcaseIdleMinutes.Value}{Source(ShowcaseIdleMinutes.FromFile)}",
            $"{InstancesTable}.{LiveGraceKey} {LiveGraceMinutes.Value}{Source(LiveGraceMinutes.FromFile)}",
            $"{InstancesTable}.{LiveWaitKey} {LiveWaitMinutes.Value}{Source(LiveWaitMinutes.FromFile)}",
            $"{InstancesTable}.{LiveAfterWaitKey} {LiveAfterWait.Value}{Source(LiveAfterWait.FromFile)}",
            $"{InstancesTable}.{LiveLimitKey} {LiveLimitMinutes.Value}{Source(LiveLimitMinutes.FromFile)}",
            $"{FilmTable}.{KeepRecentKey} {KeepRecent.Value}{Source(KeepRecent.FromFile)}",
            $"{FilmTable}.{KeepTaggedKey} {KeepTagged.Value}{Source(KeepTagged.FromFile)}",
            $"{FilmTable}.{KeepFramesRunsKey} {KeepFramesRuns.Value}{Source(KeepFramesRuns.FromFile)}",
        ];

        private static bool IsUnset(Setting<string> setting) => string.IsNullOrEmpty(setting.Value);

        private static string PathShown(Setting<string> setting) =>
            IsUnset(setting) ? "（未配置）" : setting.Value + Source(true);

        private static string Source(bool fromFile) => fromFile ? $"（{MachineFileName}）" : "（缺省）";
    }

    private static (bool Exists, TomlTable Table) ReadFile(string path, string file) =>
        TomlFile.Read(path, file, message => new StoreException(message));

    private static void RejectUnknown(TomlTable table, string where, string path, IReadOnlyList<string> known)
    {
        foreach (string key in table.Keys.Where(key => !known.Contains(key, StringComparer.Ordinal)))
            throw new StoreException($"{where} 无法识别的键 {key}；可识别的键：{string.Join("、", known)}；文件 {path}");
    }

    private static TomlTable Table(TomlTable table, string where, string path, string key)
    {
        if (!table.TryGetValue(key, out object? raw)) return new TomlTable();
        if (raw is not TomlTable nested)
            throw new StoreException($"{where} 的 {key} 不是表：{Shown(raw)}；文件 {path}");
        return nested;
    }

    private static Setting<string> AbsolutePath(TomlTable table, string where, string path, string key)
    {
        if (!table.TryGetValue(key, out object? raw)) return new Setting<string>("", false);
        if (raw is not string text || !Path.IsPathFullyQualified(text))
            throw new StoreException($"{where} 的 {key} 须为绝对路径：{Shown(raw)}；文件 {path}");
        return new Setting<string>(text, true);
    }

    private static Setting<int> Integer(TomlTable table, string where, string path, string key, int fallback)
    {
        if (!table.TryGetValue(key, out object? raw)) return new Setting<int>(fallback, false);
        if (raw is not long number || number < 0 || number > int.MaxValue)
            throw new StoreException($"{where} 的 {key} 不是非负整数：{Shown(raw)}；文件 {path}");
        return new Setting<int>((int)number, true);
    }

    private static Setting<string> Choice(TomlTable table, string where, string path, string key,
        IReadOnlyList<string> allowed, string fallback)
    {
        if (!table.TryGetValue(key, out object? raw)) return new Setting<string>(fallback, false);
        if (raw is not string text || !allowed.Contains(text, StringComparer.Ordinal))
            throw new StoreException($"{where} 的 {key} 只收 {string.Join("、", allowed.Select(value => "\"" + value + "\""))}：{Shown(raw)}；文件 {path}");
        return new Setting<string>(text, true);
    }

    private static Setting<long> Megabytes(TomlTable table, string where, string path, string key, long fallback)
    {
        if (!table.TryGetValue(key, out object? raw)) return new Setting<long>(fallback, false);
        if (raw is not long number || number < 0)
            throw new StoreException($"{where} 的 {key} 不是非负整数（MB）：{Shown(raw)}；文件 {path}");
        return new Setting<long>(number, true);
    }

    private static Setting<double> Number(TomlTable table, string where, string path, string key, double fallback)
    {
        if (!table.TryGetValue(key, out object? raw)) return new Setting<double>(fallback, false);
        double number = raw switch
        {
            long whole => whole,
            double fractional => fractional,
            _ => double.NaN,
        };
        if (!double.IsFinite(number) || number < 0)
            throw new StoreException($"{where} 的 {key} 不是非负数：{Shown(raw)}；文件 {path}");
        return new Setting<double>(number, true);
    }

    private static string Shown(object? value) => value switch
    {
        null => "（无）",
        string text => $"「{text}」",
        bool flag => flag ? "true" : "false",
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        TomlTable => "表",
        TomlTableArray => "表数组",
        TomlArray => "数组",
        _ => value.GetType().Name,
    };

    private static string Shown(double number) => number.ToString("0.##", CultureInfo.InvariantCulture);
}

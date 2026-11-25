using System.Diagnostics;

namespace RimRunnerCli;

public enum LaunchKind
{
    Batch,
    NoCompat,
    Warm,
    BuildSave,
    Showcase,
    Film,
}

internal static class LaunchKinds
{
#pragma warning disable CS8524
    internal static string PurposeOf(LaunchKind kind) => kind switch
    {
        LaunchKind.Batch or LaunchKind.Warm or LaunchKind.BuildSave => ClaimRecord.PurposeBatch,
        LaunchKind.NoCompat => ClaimRecord.PurposeNoCompat,
        LaunchKind.Showcase => ClaimRecord.PurposeShowcase,
        LaunchKind.Film => ClaimRecord.PurposeFilm,
    };
#pragma warning restore CS8524
}

public abstract record Launch
{
    private Launch() { }

    public sealed record Batch(StructuredRunRequest Request,
        Action<StructuredRunRequest, bool>? Announce = null) : Launch;
    public sealed record NoCompat(StructuredRunRequest Request, string CompatOff,
        Action<StructuredRunRequest, bool>? Announce = null) : Launch;
    public sealed record Warm : Launch;
    public sealed record BuildSave : Launch;
    public sealed record Showcase : Launch;
    public sealed record Film(FilmFlow.Options Options) : Launch;
    public sealed record ShardPiece(ShardChildSpec Spec,
        Action<StructuredRunRequest, bool>? Announce = null) : Launch;

    public LaunchKind? Kind => this switch
    {
        Batch => LaunchKind.Batch,
        NoCompat => LaunchKind.NoCompat,
        Warm => LaunchKind.Warm,
        BuildSave => LaunchKind.BuildSave,
        Showcase => LaunchKind.Showcase,
        Film => LaunchKind.Film,
        ShardPiece => null,
        _ => throw new UnreachableException(),
    };

    public string? Purpose => Kind is { } kind ? LaunchKinds.PurposeOf(kind) : null;

    public bool Dispatches => this is Batch or NoCompat or ShardPiece { Spec.Dispatch: true };

    public StructuredRunRequest? BatchRequest => this switch
    {
        Batch b => b.Request,
        NoCompat n => n.Request,
        ShardPiece p => p.Spec.Batch,
        _ => null,
    };

    public Action<StructuredRunRequest, bool>? BatchAnnounce => this switch
    {
        Batch b => b.Announce,
        NoCompat n => n.Announce,
        ShardPiece p => p.Announce,
        _ => null,
    };

    public string CompatOffArg => (this as NoCompat)?.CompatOff ?? "";

    public string ActivityNoun => this switch
    {
        BuildSave => "建档",
        Film => "拍摄",
        Showcase => "展台",
        _ when !Dispatches => "启动实例",
        _ => "批次",
    };
}

public sealed record LaunchCommon(string? ModList, bool ForceBusy, int? TimeoutMinutes,
    Action<string>? Log = null, CancellationToken Cancellation = default, BaselineSave? Save = null);

using Args = RimRunner.RimRunnerEngineArgs;

namespace RimRunnerCli;

public static class TextureTierPolicy
{
    public const string DirectiveKey = "textures";

    public static readonly string[] Ladder =
    [
        Args.TextureTierPlaceholder, Args.TextureTierNative
    ];

    public const string DefaultTier = Args.TextureTierNative;

    public static int Rank(string? tier) => Array.IndexOf(Ladder, tier);

    public static string ScreenshotNote(string? tier) => tier switch
    {
        Args.TextureTierPlaceholder => "[贴图] placeholder：贴图为占位像素",
        Args.TextureTierNative => "[贴图] native：原图",
        _ => "[贴图] 无法读取本实例的贴图档位",
    };

    public sealed record Resolution(string Tier, string Announcement, bool Default = false);

    public sealed record CaseDemand(string Floor, IReadOnlyList<string> FloorCases)
    {
        public static readonly CaseDemand None = new(Ladder[0], []);
    }

    public static Resolution Resolve(string? modListName, string? directiveValue, bool showcase, CaseDemand demand,
        bool film = false)
    {
        string label = modListName == null ? "无名单" : $"mod 名单 {modListName}";
        string? declared = null;
        if (!string.IsNullOrWhiteSpace(directiveValue))
        {
            declared = directiveValue.Trim().ToLowerInvariant();
            if (Rank(declared) < 0)
                throw new StoreException(
                    $"{label} 的 [rimrunner].{DirectiveKey} 取值未知：「{directiveValue}」；可用：{string.Join("、", Ladder)}");
        }

        if (showcase)
        {
            string occupant = film ? "拍摄实例" : "展台实例";
            return new Resolution(Args.TextureTierNative, declared == null
                ? $"[贴图] {occupant}：native"
                : $"[贴图] {label} 声明 {declared}；{occupant}恒 native", Default: declared == null);
        }

        if (declared == null)
            return new Resolution(DefaultTier, $"[贴图] {DefaultTier}（缺省）", Default: true);

        string tier = declared;
        string origin = $"（{label} 声明）";
        if (Rank(demand.Floor) > Rank(tier))
        {
            string from = tier;
            tier = demand.Floor;
            origin = $"：用例 {string.Join("、", demand.FloorCases)} 申报下限 {tier}，{from}{origin}自动升到 {tier}";
        }

        return new Resolution(tier, $"[贴图] {tier}{origin}");
    }

    public static CaseDemand Demand(StructuredRunRequest request, CaseManifest manifest)
    {
        List<CaseManifest.CaseEntry> selected =
        [
            .. manifest.SelectedBy(request)
                .Where(entry => Rank(entry.MinTextures) >= 0)
        ];
        int floor = selected.Count == 0 ? 0 : selected.Max(entry => Rank(entry.MinTextures));
        List<string> cases = floor == 0
            ? []
            : [.. selected.Where(entry => Rank(entry.MinTextures) == floor).Select(entry => entry.Id)];
        return new CaseDemand(Ladder[floor], cases);
    }

    internal static bool InBatch(StructuredRunRequest request, CaseManifest.CaseEntry entry)
    {
        if (request.IsNamed)
            return false;
        if (entry.EnvCheck)
            return request.EnvChecks != StructuredRunRequest.EnvCheckMode.Off;
        return request.EnvChecks != StructuredRunRequest.EnvCheckMode.Only
               && string.Equals(entry.Kind, nameof(RimRunner.RimRunnerKind.Regression), StringComparison.Ordinal);
    }

    public static string? ReuseRefusal(string? instanceTier, string requiredTier)
    {
        if (instanceTier == null)
            return "实例未记录贴图档位";
        return Rank(instanceTier) < Rank(requiredTier)
            ? $"实例贴图档位 {instanceTier}，本轮要 {requiredTier}"
            : null;
    }
}

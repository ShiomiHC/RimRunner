using System.Text;
using RimWorld.Planet;

namespace RimRunner;

/// <summary>对照组探针目标收集：按程序集分组的 tick 入口集合。</summary>
/// <remarks>组名取程序集名中的字母，滤后撞名的依次追加 A、B 等后缀。</remarks>
public static class RimRunnerProfilerTargets
{
    private static readonly string[] ThingCompTickMethods =
        ["CompTick", "CompTickInterval", "CompTickRare"];

    private static readonly string[] HediffCompTickMethods =
        ["CompPostTick", "CompPostTickInterval"];

    private static readonly string[] ThingTickMethods =
        ["Tick", "TickInterval", "TickRare", "TickLong"];

    private static readonly string[] HediffTickMethods = ["Tick", "TickInterval"];

    private static readonly string[] ConditionTickMethods = ["GameConditionTick"];

    private static HashSet<string> PawnExcludedAssemblies
    {
        get
        {
            field ??= DerivePawnExcludedAssemblies();
            return field;
        }
    }

    private static HashSet<string> GlobalExcludedAssemblies
    {
        get
        {
            field ??= DeriveGlobalExcludedAssemblies();
            return field;
        }
    }

    private static HashSet<string> DerivePawnExcludedAssemblies()
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly assembly in RimRunnerDefScope.OwnedAssemblies)
            result.Add(NameOf(assembly.GetName()));
        result.Add("Analyzer");
        return result;
    }

    private static HashSet<string> DeriveGlobalExcludedAssemblies()
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (Assembly assembly in RimRunnerDefScope.GaugeAssemblies)
            result.Add(NameOf(assembly.GetName()));
        result.Add("Analyzer");
        return result;
    }

    private static string NameOf(AssemblyName reference) => reference.Name!;

    /// <summary>给定图上全部 pawn 的 comp tick 入口，按程序集分组。</summary>
    /// <remarks>收给定图上 pawn 的 comp、hediff 与 hediff comp 自己声明的 tick 方法；本产品与测试 mod 的程序集及 Analyzer 不收。</remarks>
    public static Dictionary<string, HashSet<MethodInfo>> CollectPawnCompGroups(Map map)
    {
        Dictionary<Assembly, HashSet<MethodInfo>> byAssembly =
            [];
        HashSet<Type> seenTypes = [];

        foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
        {
            List<ThingComp> comps = pawn.AllComps;
            for (int i = 0; i < comps.Count; i++)
                CollectType(comps[i].GetType(), ThingCompTickMethods, seenTypes, byAssembly,
                    PawnExcludedAssemblies);

            List<Hediff>? hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs == null) continue;
            for (int i = 0; i < hediffs.Count; i++)
            {
                CollectType(hediffs[i].GetType(), HediffTickMethods, seenTypes, byAssembly,
                    PawnExcludedAssemblies);
                List<HediffComp>? hediffComps = (hediffs[i] as HediffWithComps)?.comps;
                if (hediffComps == null) continue;
                for (int j = 0; j < hediffComps.Count; j++)
                    CollectType(hediffComps[j].GetType(), HediffCompTickMethods, seenTypes, byAssembly,
                        PawnExcludedAssemblies);
            }
        }

        return ToUniqueNamedGroups(byAssembly);
    }

    /// <summary>整个 tick 的根入口（占比的分母）。</summary>
    /// <returns>含 TickManager.DoSingleTick 一项的集合，无法解析该方法时为空。</returns>
    public static HashSet<MethodInfo> CollectTickRoot()
    {
        HashSet<MethodInfo> methods = [];
        MethodInfo root = AccessTools.Method(typeof(TickManager), "DoSingleTick");
        if (root != null) methods.Add(root);
        return methods;
    }

    /// <summary>全局组件与非 pawn 物件的逐 tick 入口，按程序集分组。</summary>
    /// <remarks>收给定图、存档与世界的组件，该图与世界的 GameCondition，图上会 tick 的非 pawn 物件及其 comp；引擎、引擎自测、测试与 Analyzer 程序集不收；组名后缀 Global。</remarks>
    public static Dictionary<string, HashSet<MethodInfo>> CollectGlobalGroups(Map map)
    {
        Dictionary<Assembly, HashSet<MethodInfo>> byAssembly =
            [];
        HashSet<Type> seenTypes = [];

        foreach (MapComponent component in map.components)
            CollectType(component.GetType(), ["MapComponentTick"], seenTypes, byAssembly,
                GlobalExcludedAssemblies);

        if (Current.Game?.components != null)
            foreach (GameComponent component in Current.Game.components)
                CollectType(component.GetType(), ["GameComponentTick"], seenTypes, byAssembly,
                    GlobalExcludedAssemblies);

        if (Find.World?.components != null)
            foreach (WorldComponent component in Find.World.components)
                CollectType(component.GetType(), ["WorldComponentTick"], seenTypes, byAssembly,
                    GlobalExcludedAssemblies);

        CollectConditions(map.gameConditionManager, seenTypes, byAssembly);
        if (Find.World?.gameConditionManager != null)
            CollectConditions(Find.World.gameConditionManager, seenTypes, byAssembly);

        foreach (Thing thing in map.listerThings.AllThings)
        {
            if (thing is Pawn) continue;
            if (thing.def.tickerType == TickerType.Never) continue;
            CollectType(thing.GetType(), ThingTickMethods, seenTypes, byAssembly,
                GlobalExcludedAssemblies);
            List<ThingComp>? comps = (thing as ThingWithComps)?.AllComps;
            if (comps == null) continue;
            for (int i = 0; i < comps.Count; i++)
                CollectType(comps[i].GetType(), ThingCompTickMethods, seenTypes, byAssembly,
                    GlobalExcludedAssemblies);
        }

        Dictionary<string, HashSet<MethodInfo>> named = ToUniqueNamedGroups(byAssembly);
        Dictionary<string, HashSet<MethodInfo>> result =
            [];
        foreach (KeyValuePair<string, HashSet<MethodInfo>> pair in named)
            result[pair.Key + "Global"] = pair.Value;
        return result;
    }

    private static void CollectConditions(GameConditionManager manager, HashSet<Type> seenTypes,
        Dictionary<Assembly, HashSet<MethodInfo>> byAssembly)
    {
        List<GameCondition> conditions = manager.ActiveConditions;
        for (int i = 0; i < conditions.Count; i++)
            CollectType(conditions[i].GetType(), ConditionTickMethods, seenTypes, byAssembly,
                GlobalExcludedAssemblies);
    }

    private static void CollectType(Type type, string[] methodNames, HashSet<Type> seenTypes,
        Dictionary<Assembly, HashSet<MethodInfo>> byAssembly, HashSet<string> excluded)
    {
        if (!seenTypes.Add(type)) return;
        Assembly assembly = type.Assembly;
        if (excluded.Contains(NameOf(assembly.GetName()))) return;

        for (int i = 0; i < methodNames.Length; i++)
        {
            MethodInfo m = AccessTools.DeclaredMethod(type, methodNames[i]);
            if (m == null) continue;
            if (!byAssembly.TryGetValue(assembly, out HashSet<MethodInfo>? set))
            {
                set = [];
                byAssembly[assembly] = set;
            }

            set.Add(m);
        }
    }

    private static Dictionary<string, HashSet<MethodInfo>> ToUniqueNamedGroups(
        Dictionary<Assembly, HashSet<MethodInfo>> byAssembly)
    {
        Dictionary<string, HashSet<MethodInfo>> result =
            [];
        HashSet<string> used = new(StringComparer.Ordinal);

        foreach (KeyValuePair<Assembly, HashSet<MethodInfo>> pair in byAssembly)
        {
            if (pair.Value.Count == 0) continue;
            string baseName = LettersOnly(NameOf(pair.Key.GetName()));
            if (baseName.Length == 0) baseName = "Unnamed";
            string name = baseName;
            char suffix = 'A';
            while (!used.Add(name))
            {
                name = baseName + suffix;
                suffix++;
            }

            result[name] = pair.Value;
        }

        return result;
    }

    private static string LettersOnly(string raw)
    {
        StringBuilder builder = new(raw.Length);
        for (int i = 0; i < raw.Length; i++)
            if (char.IsLetter(raw[i]))
                builder.Append(raw[i]);
        return builder.ToString();
    }
}

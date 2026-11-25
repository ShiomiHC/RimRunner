using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RimRunner;

internal static class RimRunnerLiveScripts
{
    private static readonly Regex ParamName = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.Ordinal);

    internal const string ConditionAsAction = "为条件，live do 不受理";
    internal const string ActionAsCondition = "为动作，live until 不受理";

    internal const string PrimitiveNameTaken = "与内置原语重名";

    internal static string KindMismatch(string fullName, bool condition) =>
        "脚本里的 " + fullName + " " + (condition ? ConditionAsAction : ActionAsCondition);

    internal static T? Instantiate<T>(byte[] image, string name, out string? fault) where T : class
    {
        object? instance = InstantiateAny(image, name, out fault);
        if (instance is null or T)
        {
            return instance as T;
        }

        fault = KindMismatch(instance.GetType().FullName ?? "", instance is RimRunnerLiveCondition);
        return null;
    }

    internal static object? InstantiateAny(byte[] image, string name, out string? fault)
    {
        try
        {
            return InstantiateUnguarded(image, name, out fault);
        }
        catch (Exception exception)
        {
            fault = Describe(exception);
            return null;
        }
    }

    private static object? InstantiateUnguarded(byte[] image, string name, out string? fault)
    {
        Type[] entries = [.. Load(image).GetTypes().Where(type => type.IsClass && !type.IsAbstract
            && (typeof(RimRunnerLiveAction).IsAssignableFrom(type) || typeof(RimRunnerLiveCondition).IsAssignableFrom(type)))];
        if (entries.Length != 1)
        {
            fault = entries.Length == 0
                ? "脚本里没有派生 RimRunnerLiveAction 或 RimRunnerLiveCondition 的非抽象类"
                : "脚本里派生 RimRunnerLiveAction 或 RimRunnerLiveCondition 的非抽象类有 " + entries.Length + " 个，须恰好一个："
                  + string.Join("、", entries.Select(type => type.FullName).OrderBy(text => text, StringComparer.Ordinal));
            return null;
        }

        Type entry = entries[0];
        if (entry.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) == null)
        {
            fault = entry.FullName + " 没有无参构造函数";
            return null;
        }

        object instance;
        try
        {
            instance = Activator.CreateInstance(entry, nonPublic: true)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            fault = entry.FullName + " 构造失败：" + Describe(exception.InnerException);
            return null;
        }

        string id;
        List<LiveScriptFault> faults;
        if (instance is RimRunnerLiveAction action)
        {
            action.ScriptName = name;
            id = action.Id;
            faults = id == name ? ActionFaults([action]) : [];
        }
        else
        {
            var condition = (RimRunnerLiveCondition)instance;
            condition.ScriptName = name;
            id = condition.Id;
            faults = id == name ? ConditionFaults([condition]) : [];
        }

        if (id != name)
        {
            fault = entry.FullName + " 把 Id 重写成「" + id + "」，与脚本名 " + name + " 不匹配：脚本的 Id 只取文件名";
            return null;
        }

        fault = faults.Count > 0 ? string.Join("；", faults.Select(item => item.Text)) : null;
        return fault == null ? instance : null;
    }

    private static Assembly Load(byte[] image)
    {
        string hash = Convert.ToHexString(SHA256.HashData(image));
        if (!Loaded.TryGetValue(hash, out Assembly? assembly))
        {
            assembly = Assembly.Load(image);
            Loaded[hash] = assembly;
        }

        return assembly;
    }

    internal static string Describe(Exception exception)
    {
        if (exception is ReflectionTypeLoadException typeLoad)
        {
            string[] causes = [.. typeLoad.LoaderExceptions.Where(cause => cause != null).Select(cause => cause!.Message).Distinct().Take(3)];
            return "类型加载失败：" + (causes.Length > 0 ? string.Join("；", causes) : typeLoad.Message);
        }

        return exception.GetType().Name + "：" + exception.Message;
    }

    internal enum LiveScriptFaultKind
    {
        IdInvalid,
        IdDuplicate,
        ParamNameInvalid,
        ParamNameDuplicate,
        PrimitiveNameTaken,
        ConditionReservedParam,
    }

    internal readonly record struct LiveScriptFault(string? Id, LiveScriptFaultKind Kind, string Text);

    internal static List<LiveScriptFault> ActionFaults(IReadOnlyList<RimRunnerLiveAction> actions)
    {
        var faults = new List<LiveScriptFault>();
        foreach (RimRunnerLiveAction action in actions)
        {
            string name = action.GetType().Name;
            if (string.IsNullOrWhiteSpace(action.Id) || action.Id.Any(char.IsWhiteSpace) || action.Id.Contains('='))
            {
                faults.Add(new(null, LiveScriptFaultKind.IdInvalid, name + " 的 Id 为空或含空白、=：「" + action.Id + "」"));
                continue;
            }

            string id = action.Id;
            if (actions.Count(other => other.Id == id) > 1)
            {
                faults.Add(new(id, LiveScriptFaultKind.IdDuplicate, "动作 Id " + id + " 重复：" + string.Join("、", actions.Where(other => other.Id == id)
                    .Select(other => other.GetType().Name))));
            }

            if (RimRunnerDriveTable.Find(id) != null)
            {
                faults.Add(new(id, LiveScriptFaultKind.PrimitiveNameTaken, "动作 Id " + id + " " + PrimitiveNameTaken));
            }

            faults.AddRange(ParamFaults(id, "动作 " + id, action.Params));
        }

        return faults.DistinctBy(fault => fault.Text).ToList();
    }

    private static IEnumerable<LiveScriptFault> ParamFaults(string id, string owner,
        IReadOnlyList<RimRunnerLiveParam> parameters)
    {
        foreach (RimRunnerLiveParam param in parameters)
        {
            if (param.Name == null || !ParamName.IsMatch(param.Name))
            {
                yield return new(id, LiveScriptFaultKind.ParamNameInvalid,
                    owner + " 的参数名「" + param.Name + "」须以字母开头，只用字母、数字、- 与 _");
            }
        }

        foreach (string duplicate in parameters.GroupBy(param => param.Name).Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            yield return new(id, LiveScriptFaultKind.ParamNameDuplicate, owner + " 的参数名 " + duplicate + " 重复");
        }
    }

    internal static List<LiveScriptFault> ConditionFaults(IReadOnlyList<RimRunnerLiveCondition> conditions)
    {
        var faults = new List<LiveScriptFault>();
        foreach (RimRunnerLiveCondition condition in conditions)
        {
            if (string.IsNullOrWhiteSpace(condition.Id) || condition.Id.Any(char.IsWhiteSpace) || condition.Id.Contains('='))
            {
                faults.Add(new(null, LiveScriptFaultKind.IdInvalid,
                    condition.GetType().Name + " 的 Id 为空或含空白、=：「" + condition.Id + "」"));
                continue;
            }

            if (conditions.Count(other => other.Id == condition.Id) > 1)
            {
                faults.Add(new(condition.Id, LiveScriptFaultKind.IdDuplicate, "条件 Id " + condition.Id + " 重复：" + string.Join("、", conditions
                    .Where(other => other.Id == condition.Id).Select(other => other.GetType().Name))));
            }

            if (condition.Id == RimRunnerDriveTable.UntilLetter)
            {
                faults.Add(new(condition.Id, LiveScriptFaultKind.PrimitiveNameTaken, "条件 Id " + condition.Id + " 与内置条件重名"));
            }

            foreach (RimRunnerLiveParam reserved in condition.Params.Where(param =>
                         RimRunnerDriveTable.Until.Params.Any(own => own.Name == param.Name)))
            {
                faults.Add(new(condition.Id, LiveScriptFaultKind.ConditionReservedParam,
                    "条件 " + condition.Id + " 的参数名 " + reserved.Name + " 是 live until 的保留名"));
            }

            faults.AddRange(ParamFaults(condition.Id, "条件 " + condition.Id, condition.Params));
        }

        return faults.DistinctBy(fault => fault.Text).ToList();
    }
}

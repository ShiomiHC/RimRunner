namespace RimRunner;

[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add), typeof(Window))]
internal static class Patch_WindowStack_Add_RimRunnerAutoName
{
    private static readonly FieldInfo? CurName = AccessTools.Field(typeof(Dialog_GiveName), "curName");
    private static readonly FieldInfo? UseSecondName = AccessTools.Field(typeof(Dialog_GiveName), "useSecondName");
    private static readonly FieldInfo? CurSecondName = AccessTools.Field(typeof(Dialog_GiveName), "curSecondName");
    private static readonly MethodInfo? IsValidName =
        AccessTools.Method(typeof(Dialog_GiveName), "IsValidName", [typeof(string)]);
    private static readonly MethodInfo? Named = AccessTools.Method(typeof(Dialog_GiveName), "Named", [typeof(string)]);
    private static readonly MethodInfo? IsValidSecondName =
        AccessTools.Method(typeof(Dialog_GiveName), "IsValidSecondName", [typeof(string)]);
    private static readonly MethodInfo? NamedSecond =
        AccessTools.Method(typeof(Dialog_GiveName), "NamedSecond", [typeof(string)]);

    private static readonly bool Unavailable = ReportUnavailable();

    private static bool ReportUnavailable()
    {
        List<string> missing = [];
        if (CurName == null)
            missing.Add("Dialog_GiveName.curName");
        if (UseSecondName == null)
            missing.Add("Dialog_GiveName.useSecondName");
        if (CurSecondName == null)
            missing.Add("Dialog_GiveName.curSecondName");
        if (IsValidName == null)
            missing.Add("Dialog_GiveName.IsValidName");
        if (Named == null)
            missing.Add("Dialog_GiveName.Named");
        if (IsValidSecondName == null)
            missing.Add("Dialog_GiveName.IsValidSecondName");
        if (NamedSecond == null)
            missing.Add("Dialog_GiveName.NamedSecond");
        if (missing.Count == 0)
        {
            return false;
        }

        RimRunnerEngineFault.Raise("无法解析 " + string.Join("、", missing)
                                   + "：自动命名未执行，命名弹窗仍会打开");
        return true;
    }

    private static bool Prefix(Window window)
    {
        if (!GenCommandLine.CommandLineArgPassed(RimRunnerEngineArgs.Enable)
            || window is not Dialog_GiveName dialog || Unavailable)
        {
            return true;
        }

        string? name = (CurName!.GetValue(dialog) as string)?.Trim();
        if (name == null || !(bool)IsValidName!.Invoke(dialog, [name])!)
        {
            return true;
        }

        bool useSecondName = (bool)UseSecondName!.GetValue(dialog)!;
        string? secondName = null;
        if (useSecondName)
        {
            secondName = (CurSecondName!.GetValue(dialog) as string)?.Trim();
            if (secondName == null
                || !(bool)IsValidSecondName!.Invoke(dialog, [secondName])!)
            {
                return true;
            }
        }

        Named!.Invoke(dialog, [name]);
        if (useSecondName)
        {
            NamedSecond!.Invoke(dialog, [secondName]);
        }

        return false;
    }
}

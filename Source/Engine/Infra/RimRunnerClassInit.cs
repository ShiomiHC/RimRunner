using System.Reflection.Emit;

namespace RimRunner;

internal static class RimRunnerClassInit
{
    private static readonly Func<IntPtr, bool>? isClassInited = Bind(out BindFault);

    internal static readonly string? BindFault;

    internal static bool Bound => isClassInited != null;

    internal static bool IsInitialized(Type type)
    {
        if (isClassInited == null)
        {
            throw new InvalidOperationException(BindFault);
        }

        return isClassInited(type.TypeHandle.Value);
    }

    private static Func<IntPtr, bool>? Bind(out string? fault)
    {
        const string Ns = "System.Runtime.CompilerServices.";
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Assembly core = typeof(object).Assembly;
        Type? methodTable = core.GetType(Ns + "MethodTable");
        Type? auxiliary = core.GetType(Ns + "MethodTableAuxiliaryData");
        FieldInfo? auxiliaryField = methodTable?.GetField("AuxiliaryData", Members);
        MethodInfo? inited = auxiliary?.GetProperty("IsClassInited", Members)?.GetGetMethod(true);
        if (auxiliaryField == null || auxiliaryField.FieldType != auxiliary?.MakePointerType()
                                   || inited == null || inited.ReturnType != typeof(bool))
        {
            fault = "运行时 " + core.GetName().Name + " " + core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    + " 里无法解析 " + Ns + "MethodTable.AuxiliaryData（MethodTableAuxiliaryData*）或 MethodTableAuxiliaryData.IsClassInited（bool）";
            return null;
        }

        try
        {
            DynamicMethod method = new("RimRunnerIsClassInited", typeof(bool), [typeof(IntPtr)],
                typeof(RimRunnerClassInit).Module, skipVisibility: true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, auxiliaryField);
            il.Emit(OpCodes.Call, inited);
            il.Emit(OpCodes.Ret);
            Func<IntPtr, bool> probe = (Func<IntPtr, bool>)method.CreateDelegate(typeof(Func<IntPtr, bool>));
            if (!probe(typeof(object).TypeHandle.Value))
            {
                fault = "MethodTableAuxiliaryData.IsClassInited 对 System.Object 读出未初始化：读到的不是初始化标志";
                return null;
            }

            fault = null;
            return probe;
        }
        catch (Exception exception)
        {
            fault = "按 MethodTable.AuxiliaryData.IsClassInited 生成判定方法失败：" + exception.GetType().Name + " " + exception.Message;
            return null;
        }
    }
}

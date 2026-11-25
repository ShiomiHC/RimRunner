using System.Reflection.Emit;

namespace RimRunner;

internal static class AbsentAssemblyProbeHost
{
    private const string LibName = "RimRunnerIls3ProbeLib";
    internal const string AbsentName = "RimRunnerIls3AbsentXX";
    private const string HostName = "RimRunnerIls3ProbeHost";
    private const string HostTypeName = "RimRunnerIls3.Host";
    internal const string AbsentPackage = "rimrunner.selftest.absent";

    private static Type? hostType;

    internal static Type HostType()
    {
        if (hostType != null) return hostType;

        Type absentType = Library().Class;

        PersistedAssemblyBuilder builder = new(new AssemblyName(HostName), typeof(object).Assembly);
        TypeBuilder type = builder.DefineDynamicModule(HostName)
            .DefineType(HostTypeName, TypeAttributes.Public | TypeAttributes.Class
                                                           | TypeAttributes.Abstract | TypeAttributes.Sealed);

        MethodBuilder isolated = type.DefineMethod("Isolated", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), Type.EmptyTypes);
        isolated.SetImplementationFlags(MethodImplAttributes.NoInlining);
        ILGenerator il = isolated.GetILGenerator();
        il.DeclareLocal(absentType);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ret);

        MethodBuilder typed = type.DefineMethod("Typed", MethodAttributes.Public | MethodAttributes.Static,
            absentType, Type.EmptyTypes);
        typed.SetImplementationFlags(MethodImplAttributes.NoInlining);
        il = typed.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        MethodBuilder callsTyped = type.DefineMethod("CallsTyped", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), Type.EmptyTypes);
        il = callsTyped.GetILGenerator();
        il.Emit(OpCodes.Call, typed);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        MethodBuilder run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), Type.EmptyTypes);
        il = run.GetILGenerator();
        il.Emit(OpCodes.Call, isolated);
        il.Emit(OpCodes.Ret);

        MethodBuilder exemptIsolated = type.DefineMethod("ExemptIsolated", MethodAttributes.Public | MethodAttributes.Static,
            typeof(void), Type.EmptyTypes);
        exemptIsolated.SetImplementationFlags(MethodImplAttributes.NoInlining);
        exemptIsolated.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(RimRunnerDirectGenerationAttribute).GetConstructor([typeof(string)])!,
            ["缺席程序集样本：名下有直调，本轮无法读取该方法体"]));
        il = exemptIsolated.GetILGenerator();
        il.DeclareLocal(absentType);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, typeof(RimRunnerPawns).GetMethod(nameof(RimRunnerPawns.GenerateIsolatedPawn))!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);

        type.CreateType();
        hostType = LoadPatched(builder).GetType(HostTypeName, true)!;
        return hostType;
    }

    private static (Type Class, Type Struct, Type Interface, Type Attribute)? library;

    private static (Type Class, Type Struct, Type Interface, Type Attribute) Library()
    {
        if (library != null) return library.Value;

        ModuleBuilder module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(LibName), AssemblyBuilderAccess.Run)
            .DefineDynamicModule(LibName);
        Type absentClass = module.DefineType("RimRunnerIls3.AbsentType", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        Type absentStruct = module.DefineType("RimRunnerIls3.AbsentStruct",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType)).CreateType();
        Type absentInterface = module.DefineType("RimRunnerIls3.IAbsent",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType();
        TypeBuilder absentAttribute = module.DefineType("RimRunnerIls3.AbsentAttr",
            TypeAttributes.Public | TypeAttributes.Class, typeof(Attribute));
        absentAttribute.DefineDefaultConstructor(MethodAttributes.Public);
        Type absentAttr = absentAttribute.CreateType();
        library = (absentClass, absentStruct, absentInterface, absentAttr);
        return library.Value;
    }

    private static byte[] SavePatched(PersistedAssemblyBuilder builder)
    {
        byte[] bytes;
        using (System.IO.MemoryStream stream = new())
        {
            builder.Save(stream);
            bytes = stream.ToArray();
        }

        if (Patch(bytes, LibName, AbsentName) == 0)
        {
            throw new InvalidOperationException("本次生成的宿主字节中未找到库名 " + LibName + "，无法改写程序集引用");
        }

        return bytes;
    }

    private static Assembly LoadPatched(PersistedAssemblyBuilder builder)
    {
        byte[] bytes = SavePatched(builder);
        Assembly loaded = Assembly.Load(bytes);
        RimRunnerAbsentReferences.RegisterImage(loaded, bytes);
        return loaded;
    }

    internal const string ShapesNamespace = "RimRunnerIls3Shapes";
    private static Assembly? shapesHost;

    internal static Assembly ShapesHost()
    {
        if (shapesHost != null) return shapesHost;

        (Type absentClass, _, _, Type absentAttribute) = Library();
        PersistedAssemblyBuilder builder = new(new AssemblyName("RimRunnerIls3ShapesHost"), typeof(object).Assembly);
        ModuleBuilder module = builder.DefineDynamicModule("RimRunnerIls3ShapesHost");
        const TypeAttributes Static = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed;
        const MethodAttributes StaticMethod = MethodAttributes.Public | MethodAttributes.Static;

        TypeBuilder refField = module.DefineType(ShapesNamespace + ".RefField", TypeAttributes.Public | TypeAttributes.Class);
        refField.DefineField("held", absentClass, FieldAttributes.Public);
        refField.DefineField("shared", absentClass, FieldAttributes.Public | FieldAttributes.Static);
        refField.CreateType();

        TypeBuilder locals = module.DefineType(ShapesNamespace + ".Locals", Static);
        ILGenerator il = locals.DefineMethod("Use", StaticMethod, typeof(void), Type.EmptyTypes).GetILGenerator();
        il.DeclareLocal(absentClass);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ret);
        locals.CreateType();

        TypeBuilder body = module.DefineType(ShapesNamespace + ".Body", Static);
        il = body.DefineMethod("Cast", StaticMethod, typeof(void), Type.EmptyTypes).GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Castclass, absentClass);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        body.CreateType();

        TypeBuilder signature = module.DefineType(ShapesNamespace + ".Signature", Static);
        MethodBuilder get = signature.DefineMethod("Get", StaticMethod, absentClass, Type.EmptyTypes);
        il = get.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        signature.CreateType();

        TypeBuilder attributedMethod = module.DefineType(ShapesNamespace + ".AttributedMethod", Static);
        MethodBuilder attributedUse = attributedMethod.DefineMethod("Use", StaticMethod, typeof(void), Type.EmptyTypes);
        attributedUse.SetCustomAttribute(new CustomAttributeBuilder(
            absentAttribute.GetConstructor(Type.EmptyTypes)!, []));
        il = attributedUse.GetILGenerator();
        il.Emit(OpCodes.Ret);
        attributedMethod.CreateType();

        TypeBuilder attributedType = module.DefineType(ShapesNamespace + ".AttributedType", TypeAttributes.Public | TypeAttributes.Class);
        attributedType.SetCustomAttribute(new CustomAttributeBuilder(
            absentAttribute.GetConstructor(Type.EmptyTypes)!, []));
        ConstructorBuilder attributedTypeCtor = attributedType.DefineDefaultConstructor(MethodAttributes.Public);
        attributedType.CreateType();

        TypeBuilder caller = module.DefineType(ShapesNamespace + ".Caller", Static);
        il = caller.DefineMethod("Call", StaticMethod, typeof(void), Type.EmptyTypes).GetILGenerator();
        il.Emit(OpCodes.Call, get);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Call, attributedUse);
        il.Emit(OpCodes.Newobj, attributedTypeCtor);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        caller.CreateType();

        TypeBuilder attributed = module.DefineType(ShapesNamespace + ".Attributed", TypeAttributes.Public | TypeAttributes.Class);
        attributed.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Diagnostics.DebuggerTypeProxyAttribute).GetConstructor([typeof(Type)])!, [absentClass]));
        attributed.CreateType();

        TypeBuilder clean = module.DefineType(ShapesNamespace + ".Clean", Static);
        il = clean.DefineMethod("One", StaticMethod, typeof(int), Type.EmptyTypes).GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        clean.CreateType();

        shapesHost = LoadPatched(builder);
        return shapesHost;
    }

    internal const string EjectingNamespace = "RimRunnerIls3Eject";
    private static byte[]? ejectingImage;

    internal static byte[] EjectingImage()
    {
        if (ejectingImage != null) return ejectingImage;

        (Type absentClass, Type absentStruct, Type absentInterface, _) = Library();
        PersistedAssemblyBuilder builder = new(new AssemblyName("RimRunnerIls3EjectHost"), typeof(object).Assembly);
        ModuleBuilder module = builder.DefineDynamicModule("RimRunnerIls3EjectHost");

        module.DefineType(EjectingNamespace + ".Derived", TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract, absentClass)
            .CreateType();
        TypeBuilder implements = module.DefineType(EjectingNamespace + ".Implements",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract);
        implements.AddInterfaceImplementation(absentInterface);
        implements.CreateType();
        TypeBuilder holder = module.DefineType(EjectingNamespace + ".Holder", TypeAttributes.Public | TypeAttributes.Class);
        holder.DefineField("value", absentStruct, FieldAttributes.Public);
        holder.CreateType();
        TypeBuilder refOnly = module.DefineType(EjectingNamespace + ".RefOnly", TypeAttributes.Public | TypeAttributes.Class);
        refOnly.DefineField("held", absentClass, FieldAttributes.Public);
        refOnly.CreateType();

        ejectingImage = SavePatched(builder);
        return ejectingImage;
    }

    private static int Patch(byte[] bytes, string from, string to)
    {
        byte[] source = System.Text.Encoding.ASCII.GetBytes(from);
        byte[] target = System.Text.Encoding.ASCII.GetBytes(to);
        int count = 0;
        for (int i = 0; i + source.Length <= bytes.Length; i++)
        {
            int j = 0;
            while (j < source.Length && bytes[i + j] == source[j]) j++;
            if (j < source.Length) continue;
            Array.Copy(target, 0, bytes, i, target.Length);
            count++;
        }

        return count;
    }
}

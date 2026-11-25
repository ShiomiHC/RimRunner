using System.Reflection.Emit;

namespace RimRunner;

internal static class RimRunnerGenericCensus
{
    private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.DeclaredOnly;

    private static List<Type>? cached;

    internal static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!).GroupBy(code => code.Value).ToDictionary(group => group.Key, group => group.First());

    internal static List<Type> ClosedGenericTypes()
    {
        return cached ??= ClosedGenericTypes(AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic));
    }

    internal static List<Type> ClosedGenericTypes(IEnumerable<Assembly> assemblies)
    {
        HashSet<Type> found = [];
        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in RimRunnerHolderScan.TypesOf(assembly))
            {
                try
                {
                    CollectType(type, found);
                }
                catch (Exception)
                {
                }
            }
        }

        return found.ToList();
    }

    internal static void CollectType(Type type, HashSet<Type> found)
    {
        foreach (FieldInfo field in RimRunnerAbsentReferences.ReadableFields(type, Declared))
        {
            Add(field.FieldType, found);
        }

        Type[]? typeArguments = type.IsGenericTypeDefinition ? type.GetGenericArguments() : null;
        foreach (MethodBase method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
        {
            if (RimRunnerAbsentReferences.Unreadable(method) != null)
            {
                continue;
            }

            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il != null)
            {
                CollectBody(il, method.Module, typeArguments, method.IsGenericMethodDefinition ? method.GetGenericArguments() : null, found);
            }
        }
    }

    private static void CollectBody(byte[] il, Module module, Type[]? typeArguments, Type[]? methodArguments, HashSet<Type> found)
    {
        int position = 0;
        while (position < il.Length)
        {
            short value = il[position++];
            if (value == 0xFE && position < il.Length)
            {
                value = (short)(0xFE00 | il[position++]);
            }

            if (!OpCodesByValue.TryGetValue(value, out OpCode code))
            {
                return;
            }

            int size = OperandSize(code.OperandType, il, position);
            if (size < 0)
            {
                return;
            }

            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok
                && position + 4 <= il.Length)
            {
                AddMember(module, BitConverter.ToInt32(il, position), typeArguments, methodArguments, found);
            }

            position += size;
        }
    }

    private static void AddMember(Module module, int token, Type[]? typeArguments, Type[]? methodArguments, HashSet<Type> found)
    {
        MemberInfo? member;
        try
        {
            member = module.ResolveMember(token, typeArguments, methodArguments);
        }
        catch (Exception)
        {
            return;
        }

        if (member is Type resolved)
        {
            Add(resolved, found);
            return;
        }

        if (member != null)
        {
            Add(member.DeclaringType, found);
            if (member is FieldInfo field)
            {
                Add(field.FieldType, found);
            }
        }
    }

    private static void Add(Type? type, HashSet<Type> found)
    {
        while (type != null && (type.IsArray || type.IsByRef || type.IsPointer))
        {
            type = type.GetElementType();
        }

        if (type == null || type.ContainsGenericParameters)
        {
            return;
        }

        if (type.IsGenericType && found.Add(type))
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                Add(argument, found);
            }
        }

        if (type.DeclaringType != null && type.DeclaringType.IsGenericType)
        {
            Add(type.DeclaringType, found);
        }
    }

    internal static int OperandSize(OperandType type, byte[] il, int position)
    {
        return type switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => position + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, position) : -1,
            _ => 4,
        };
    }

    internal static string Friendly(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        string name = type.GetGenericTypeDefinition().FullName ?? type.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name[..tick];
        }

        return name + "<" + string.Join(",", type.GetGenericArguments().Select(Friendly)) + ">";
    }
}

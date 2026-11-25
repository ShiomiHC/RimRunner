using System.Threading;

namespace RimRunner;

/// <summary>产品描述符的基类：测试程序集里派生恰好一个，引擎反射发现。</summary>
public abstract class RimRunnerProductDescriptor
{
    /// <summary>产品每个程序集各一个类型：类型所在的程序集进产品代码扫描面，所在的 mod 认作本产品。</summary>
    public abstract Type[] ProbeTypes { get; }

    /// <summary>本产品 Def 的 defName 前缀，Def 找不到所属 mod 时据此认领。</summary>
    /// <remarks>null 即不按前缀认领。空串或全空白是启动配置错误，拒批。</remarks>
    public abstract string DefNameFallbackPrefix { get; }

    /// <summary>登记本产品的能力；没有能力时方法体留空。</summary>
    /// <remarks>只登记类型、常量与委托，不读游戏状态、不读 RimRunnerProduct；方法体里不得出现可缺席包的类型；返回后登记面冻结。</remarks>
    public abstract void Configure(RimRunnerProductCapabilities c);
}

/// <summary>本产品描述符的取用入口。</summary>
public static class RimRunnerProduct
{
    private static readonly Lock DiscoverLock = new();
    private static volatile bool discovered;
    private static RimRunnerProductDescriptor? descriptor;
    private static RimRunnerProductCapabilities? capabilities;

    private static bool discovering;

    /// <summary>本产品的描述符实例。</summary>
    /// <exception cref="InvalidOperationException">描述符子类不是恰好一个，或在 Configure 里读本属性时抛；构造与 Configure 的异常照原样传出。</exception>
    public static RimRunnerProductDescriptor Descriptor => TryGetDescriptor() ?? throw Missing();

    internal static RimRunnerProductCapabilities Capabilities
    {
        get
        {
            TryGetDescriptor();
            return capabilities ?? throw Missing();
        }
    }

    private static InvalidOperationException Missing()
    {
        return new InvalidOperationException(
            RimRunnerLog.Prefix + " 找不到产品描述符：已加载程序集中没有任何 "
                                + nameof(RimRunnerProductDescriptor)
                                + " 的非抽象子类。测试程序集须提供恰好一个");
    }

    internal static RimRunnerProductDescriptor? TryGetDescriptor()
    {
        if (discovered)
        {
            return descriptor;
        }

        lock (DiscoverLock)
        {
            if (!discovered)
            {
                if (discovering)
                {
                    throw new InvalidOperationException(
                        RimRunnerLog.Prefix + " 产品描述符的 Configure 里读 RimRunnerProduct：登记期只声明，"
                                            + "描述符自身此刻尚未完成发现");
                }

                discovering = true;
                try
                {
                    Discover();
                }
                finally
                {
                    discovering = false;
                }

                discovered = true;
            }

            return descriptor;
        }
    }

    internal static RimRunnerProductCapabilities? TryGetCapabilities()
    {
        TryGetDescriptor();
        return capabilities;
    }

    internal static Assembly? TestAssembly
    {
        get
        {
            if (testAssemblyResolved)
            {
                return field;
            }

            lock (TestAssemblyLock)
            {
                if (!testAssemblyResolved)
                {
                    try
                    {
                        List<Type> implementors = FindImplementors();
                        field = implementors.Count == 1 ? implementors[0].Assembly : null;
                    }
                    catch (Exception)
                    {
                        field = null;
                    }

                    testAssemblyResolved = true;
                }

                return field;
            }
        }
    }

    private static readonly Lock TestAssemblyLock = new();
    private static volatile bool testAssemblyResolved;

    private static void Discover()
    {
        List<Type> implementors = FindImplementors();
        if (implementors.Count == 0)
        {
            return;
        }

        if (implementors.Count > 1)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " 发现 " + implementors.Count + " 个产品描述符实现："
                + string.Join(", ", implementors.Select(type => type.FullName))
                + "。已加载程序集里须恰好一个 " + nameof(RimRunnerProductDescriptor) + " 的非抽象子类");
        }

        RimRunnerProductDescriptor found = (RimRunnerProductDescriptor)Activator.CreateInstance(implementors[0])!;
        RimRunnerProductCapabilities registered = new();
        found.Configure(registered);
        registered.Freeze();
        capabilities = registered;
        descriptor = found;
    }

    private static List<Type> FindImplementors()
    {
        List<Type> implementors = [];
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException)
            {
                continue;
            }

            foreach (Type type in types)
            {
                if (!type.IsAbstract && typeof(RimRunnerProductDescriptor).IsAssignableFrom(type))
                {
                    implementors.Add(type);
                }
            }
        }

        return implementors;
    }
}

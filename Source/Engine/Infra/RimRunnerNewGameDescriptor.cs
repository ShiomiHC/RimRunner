using System.IO;
using System.Text.Json;

namespace RimRunner;

public sealed partial class RimRunnerNewGameDescriptor
{
    internal string SourcePath = "";

    public static RimRunnerNewGameDescriptor LoadOrThrow(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new FormatException(RimRunnerLog.Prefix + " 缺少 -"
                                      + RimRunnerEngineArgs.NewGame + "=<描述符路径>。该参数由编排层传入");
        }

        if (!File.Exists(path))
        {
            throw new FormatException(RimRunnerLog.Prefix + " 建档描述符不存在：" + path);
        }

        RimRunnerNewGameDescriptor descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<RimRunnerNewGameDescriptor>(File.ReadAllText(path), MergedJson)
                         ?? throw new JsonException("根元素为 null");
        }
        catch (JsonException exception)
        {
            throw new FormatException(RimRunnerLog.Prefix + " 建档描述符无法解析为模型：" + path + "：" + exception.Message);
        }

        List<string> problems = descriptor.Problems();
        if (problems.Count > 0)
        {
            throw new FormatException(RimRunnerLog.Prefix + " 建档描述符不合格：" + path + "：" + string.Join("；", problems));
        }

        descriptor.SourcePath = path;
        return descriptor;
    }
}

using System.Text.Json.Nodes;
using RimTooling.IO;

namespace RimRunnerCli;

internal static class TomlFile
{
    public static (bool Exists, TomlTable Table) Read(string path, string file, Func<string, Exception> fail)
    {
        string full = CommandContext.FullPath(path);
        try
        {
            return TomlDocument.TryLoad(full);
        }
        catch (TomlException exception)
        {
            throw fail(FormatError(exception, file, path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw fail($"无法读取 {file}：{exception.Message}；文件 {path}");
        }
    }

    public static TomlTable Parse(string text, string path, string file, Func<string, Exception> fail)
    {
        try
        {
            return TomlDocument.Parse(text, path);
        }
        catch (TomlException exception)
        {
            throw fail(FormatError(exception, file, path));
        }
    }

    public static JsonObject ToJson(TomlTable table, string path, string file, Func<string, Exception> fail)
    {
        try
        {
            return table.ToJsonObject(path);
        }
        catch (TomlException exception)
        {
            throw fail($"无法解析 {file}：{exception.Reason}；文件 {path}");
        }
    }

    private static string FormatError(TomlException exception, string file, string path) =>
        exception.Line > 0
            ? $"{file} 第 {exception.Line} 行 TOML 语法错：{exception.Reason}；文件 {path}"
            : $"无法解析 {file}：{exception.Reason}；文件 {path}";
}

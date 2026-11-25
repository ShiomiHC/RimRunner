using System.Text;
using RimRunnerFixtureIndex;

Console.OutputEncoding = new UTF8Encoding(false);

string? dir = null, profileName = null, outFile = null, stripTo = null;
bool strict = false, casesOnly = false;
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    string? Next() => i + 1 < args.Length ? args[++i] : null;
    switch (arg)
    {
        case "--profile": profileName = Next(); break;
        case "--out": outFile = Next(); break;
        case "--strict": strict = true; break;
        case "--cases-only": casesOnly = true; break;
        case "--strip-comments": stripTo = Next() ?? ""; break;
        default:
            if (arg.StartsWith("--", StringComparison.Ordinal) || dir != null) return Usage($"不认的参数：{arg}");
            dir = arg;
            break;
    }
}

if (stripTo != null)
{
    if (stripTo == "" || profileName != null || outFile != null || strict || casesOnly)
        return Usage("--strip-comments 带一个输出目录，不能与其余参数同时传入");
    if (dir == null || !Directory.Exists(dir)) return Usage("缺扫描目录或目录不存在");
    return CommentStrip.Run(dir, stripTo);
}

Profile profile;
switch (profileName)
{
    case "engine": profile = Profile.Engine; break;
    case "product": profile = Profile.Product; break;
    default: return Usage(profileName == null ? "缺 --profile" : $"--profile 只认 engine / product：{profileName}");
}
if (dir == null) return Usage("缺扫描目录");
if (!Directory.Exists(dir)) return Fail($"扫描目录不存在：{dir}");

bool formsAreProblems = profile == Profile.Engine || strict;
List<string> problems, warnings;
List<KeyValuePair<string, object?>> body;
string summaryLine;
if (casesOnly)
{
    CaseScan scan = CaseScanner.Scan(dir);
    problems = [.. scan.Problems, .. formsAreProblems ? scan.Forms : []];
    warnings = [.. formsAreProblems ? [] : scan.Forms, .. scan.Warnings];
    if (scan.Cases.Count == 0)
    {
        if (formsAreProblems) return Fail($"用例名册塌空：{dir} 下一个用例类都没认到");
        warnings.Add($"扫描面为空：{dir} 下一个用例类都没认到");
    }

    var cases = scan.Cases.Select(c => Obj([
        ("ids", c.Ids),
        ("parameterized", c.Parameterized),
        ("class", c.Class),
        ("file", c.File),
        ("line", c.Line),
        ("kind", c.Kind),
        ("realFrame", c.RealFrame),
        ("envCheck", c.EnvCheck),
        ("groups", c.Groups),
        .. DocFields(c.Doc)])).ToList();
    body = Obj(("cases", cases));
    summaryLine = $"用例 {scan.Cases.Count} 个";
}
else
{
    ScanResult scan = FixtureScanner.Scan(dir, profile);
    problems = [.. scan.Problems, .. formsAreProblems ? scan.Forms : []];
    warnings = formsAreProblems ? [] : [.. scan.Forms];
    if (scan.Units.Count == 0)
    {
        if (formsAreProblems)
        {
            return Fail(profile == Profile.Engine
                ? $"作者面塌空：{dir} 下没有一个类型头带摘要"
                : $"扫描面塌空：{dir} 下一个类型都没收到");
        }

        warnings.Add($"扫描面为空：{dir} 下一个类型都没收到");
    }

    var fixtures = scan.Units.Select(u => Obj([
        ("name", u.Name),
        ("file", u.File),
        ("line", u.Line),
        ("kind", u.Kind),
        .. DocFields(u.Doc),
        ("members", u.Members.Select(m => Obj([("signature", m.Signature), ("line", m.Line), .. DocFields(m.Doc)])).ToList())])).ToList();
    body = Obj(("fixtures", fixtures));
    summaryLine = $"作者面 {scan.Units.Count} 个类型、{scan.Units.Sum(u => u.Members.Count)} 个成员";
}

if (outFile == null)
{
    List<KeyValuePair<string, object?>> data = [new("schema", 1), .. body, new("problems", problems), new("warnings", warnings)];
    Console.Out.Write(Json.Write(data) + "\n");
    return 0;
}

if (warnings.Count > 0)
{
    Console.Error.WriteLine($"[FixtureIndex] 警告 {warnings.Count} 处（不挡名册，--strict 下算问题）：");
    foreach (string warning in warnings) Console.Error.WriteLine("  · " + warning);
}

if (problems.Count > 0)
{
    Console.Error.WriteLine($"[FixtureIndex] {problems.Count} 处注释形态不合格：");
    foreach (string problem in problems) Console.Error.WriteLine("  · " + problem);
    Console.Error.WriteLine(casesOnly
        ? "\n每个用例类都要有一条 \"/// <summary>一句话。</summary>\"，可选一条 \"/// <remarks>作用范围。</remarks>\"，别的标签不认；写在声明正上方，每行一个完整标签。"
        : profile == Profile.Engine
            ? "\n类型头带摘要 = 作者面，它的每个 public 成员与枚举值都要有一条 \"/// <summary>一句话。</summary>\"，其余标签见 Source/FixtureIndex/README.md；不带 = 引擎内部、整块不扫，也不许写 ///。"
            : "\n每个类型与每个 public/internal 成员都要有一条 \"/// <summary>一句话。</summary>\"，写在声明正上方、特性之上；每行一个完整标签。");
    return 1;
}

File.WriteAllText(outFile, Json.Write(Schema(body)) + "\n", new UTF8Encoding(false));
Console.WriteLine($"[FixtureIndex] {summaryLine}");
return 0;

static List<(string, object?)> DocFields(Doc doc)
{
    var fields = new List<(string, object?)> { ("summary", doc.Summary) };
    if (doc.Remarks != null) fields.Add(("remarks", doc.Remarks));
    if (doc.Params.Count > 0) fields.Add(("params", doc.Params.Select(p => Obj(("name", p.Name), ("text", p.Text))).ToList()));
    if (doc.Returns != null) fields.Add(("returns", doc.Returns));
    if (doc.Exceptions.Count > 0) fields.Add(("exceptions", doc.Exceptions.Select(e => Obj(("cref", e.Cref), ("text", e.Text))).ToList()));
    return fields;
}

static List<KeyValuePair<string, object?>> Obj(params (string Key, object? Value)[] fields) =>
    fields.Select(f => new KeyValuePair<string, object?>(f.Key, f.Value)).ToList();

static int Usage(string message)
{
    Console.Error.WriteLine($"[FixtureIndex] {message}");
    Console.Error.WriteLine("用法：RimRunnerFixtureIndex <目录> --profile engine|product [--strict] [--cases-only] [--out <文件>]");
    return 2;
}

static int Fail(string message)
{
    Console.Error.WriteLine($"[FixtureIndex] {message}");
    return 1;
}

static List<KeyValuePair<string, object?>> Schema(List<KeyValuePair<string, object?>> body) =>
    [new("schema", 1), .. body];

#requires -Version 7
# 编引擎与 CLI，落成安装目录 dist/
# -Release：另打 release/rimrunner-<标签>.zip。要求工作树干净、HEAD 上有标签 v<CLI 版本号>

param([string]$Configuration = 'Release', [switch]$Release)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$engineDir = Join-Path $root 'Source\Engine'
$engineProj = Join-Path $root 'Source\EngineBuild\RimRunner.Engine.csproj'
$engineBin = Join-Path $root 'Source\EngineBuild\bin'
$toolProj = Join-Path $root 'Source\FixtureIndex\RimRunnerFixtureIndex.csproj'
$toolBin = Join-Path $root "Source\FixtureIndex\bin\$Configuration\net10.0"
$analyzerProj = Join-Path $root 'Source\Analyzers\RimRunnerAnalyzers.csproj'
$analyzerDll = Join-Path $root "Source\Analyzers\bin\$Configuration\netstandard2.0\RimRunner.Analyzers.dll"
$cliProj = Join-Path $root 'Source\Cli\RimRunnerCli.csproj'
$cliBin = Join-Path $root "Source\Cli\bin\$Configuration\net10.0"
$dist = Join-Path $root 'dist'
$staging = Join-Path $root 'obj\dist-staging'
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-Success([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what 失败（退出码 $LASTEXITCODE）" }
}

function Copy-Tree([string]$from, [string]$to, [string[]]$options = @('/E')) {
    robocopy $from $to @options /NJH /NJS /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "拷贝 $from 失败（robocopy $LASTEXITCODE）" }
}

# 运行中的程序集不能覆写，但能改名：被占用的先改名，原进程继续用改名后的那份
function Move-Locked([string]$dir) {
    if (-not (Test-Path $dir)) { return }
    Get-ChildItem $dir -Recurse -File -Filter '*.old-*' | Remove-Item -Force -ErrorAction Ignore
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    foreach ($file in Get-ChildItem $dir -Recurse -File -Include *.exe, *.dll) {
        try { [IO.File]::Open($file.FullName, 'Open', 'ReadWrite', 'None').Dispose() }
        catch { Move-Item $file.FullName "$($file.FullName).old-$stamp" }
    }
}

# CLI 按产品构建导入的文件的写入时刻判断产物是否陈旧，内容不变时不覆盖
function Copy-IfChanged([string]$from, [string]$to) {
    if ((Test-Path $to) -and ((Get-FileHash $from).Hash -eq (Get-FileHash $to).Hash)) { return }
    Copy-Item $from $to -Force
}

if ($Release) {
    $dirty = git -C $root status --porcelain
    Assert-Success '读取工作树状态'
    if ($dirty) { throw '-Release 要求工作树干净' }
    $version = (dotnet msbuild $cliProj -getProperty:Version | ForEach-Object Trim)
    Assert-Success '读取 CLI 版本号'
    $tag = "v$version"
    if ($tag -notin @(git -C $root tag --points-at HEAD)) { throw "HEAD 上没有标签 $tag" }
}

if (-not (Test-Path (Join-Path $root 'RimTooling\Sources'))) { throw '子模块 RimTooling 未取出，先执行 git submodule update --init' }

dotnet build $engineProj -c $Configuration -nologo -v q
Assert-Success '编译引擎'
$gamePaths = (dotnet msbuild $engineProj -getProperty:RimWorldDir | ForEach-Object Trim)
Assert-Success '读取游戏目录'
. (Join-Path $root 'Tools\CliBuild\game-fingerprint.ps1')
$game = Get-GameFingerprint $gamePaths

dotnet build $toolProj -c $Configuration -nologo -v q
Assert-Success '编译名册工具'
dotnet build $analyzerProj -c $Configuration -nologo -v q
Assert-Success '编译分析器'
dotnet build $cliProj -c $Configuration -nologo -v q
Assert-Success '编译 CLI'

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $staging 'engine\ref') | Out-Null

$fixtureTool = Join-Path $toolBin 'RimRunnerFixtureIndex.exe'
$stagedSource = Join-Path $staging 'src'
& $fixtureTool $engineDir --strip-comments $stagedSource
Assert-Success '生成随包源码'
& $fixtureTool $stagedSource --profile engine --out (Join-Path $staging 'authoring.json')
Assert-Success '生成作者面名册'
& $fixtureTool (Join-Path $stagedSource 'Checks') --profile engine --cases-only --out (Join-Path $staging 'checks.json')
Assert-Success '生成随包检查名册'

$refDll = Join-Path $engineBin 'ref\RimRunner.dll'
$refHash = (Get-FileHash $refDll -Algorithm SHA256).Hash
$analyzerHash = (Get-FileHash $analyzerDll -Algorithm SHA256).Hash
$targets = @"
<!-- RimRunner 构建生成，勿手改。产品工程 Import 它引用引擎 ref 程序集 -->
<Project>
  <PropertyGroup>
    <RimRunnerEngineRef>`$(MSBuildThisFileDirectory)engine\ref\RimRunner.dll</RimRunnerEngineRef>
    <RimRunnerAnalyzer>`$(MSBuildThisFileDirectory)analyzers\RimRunner.Analyzers.dll</RimRunnerAnalyzer>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="RimRunner" HintPath="`$(RimRunnerEngineRef)" Private="False" />
    <Analyzer Include="`$(RimRunnerAnalyzer)" />
    <Compile Include="`$(MSBuildThisFileDirectory)engine\ref\RimRunnerRef.cs" Link="RimRunnerRef.cs" Visible="false" />
    <CoreCompileCache Include="RimRunnerRef:$refHash" />
    <CoreCompileCache Include="RimRunnerAnalyzer:$analyzerHash" />
  </ItemGroup>
  <Target Name="RimRunnerAssertEngineRef" BeforeTargets="ResolveAssemblyReferences">
    <Error Condition="!Exists('`$(RimRunnerEngineRef)')"
           Text="RimRunner 安装目录不完整，缺引擎 ref 程序集：`$(RimRunnerEngineRef)" />
    <Error Condition="!Exists('`$(MSBuildThisFileDirectory)engine\ref\RimRunnerRef.cs')"
           Text="RimRunner 安装目录不完整，缺引擎 ref 的元数据源文件：`$(MSBuildThisFileDirectory)engine\ref\RimRunnerRef.cs" />
    <Error Condition="!Exists('`$(RimRunnerAnalyzer)')"
           Text="RimRunner 安装目录不完整，缺分析器：`$(RimRunnerAnalyzer)" />
  </Target>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $staging 'RimRunner.targets'), $targets, $utf8)
[IO.File]::WriteAllText((Join-Path $staging 'engine\ref\RimRunnerRef.cs'),
    '[assembly: System.Reflection.AssemblyMetadata("RimRunnerRef", "' + $refHash + '")]', $utf8)

foreach ($name in 'rimrunner', 'rimrunner-authoring') {
    Copy-Tree (Join-Path $root "skills\$name") (Join-Path $staging "skills\$name")
}
Copy-Tree (Join-Path $root 'package') $staging @('/E', '/XD', 'bin', 'obj')

# 描述符样板由 CLI 生成。bin 里的 CLI 不在安装目录里，只在隔离根下运行
$template = Join-Path $staging 'template'
$isolated = Join-Path $root 'obj\isolated'
New-Item -ItemType Directory -Force $isolated | Out-Null
[IO.File]::WriteAllText((Join-Path $isolated 'config.toml'),
    "gameExe = `"$((Join-Path $gamePaths 'RimWorld.exe').Replace('\', '/'))`"`n", $utf8)
$encoding = [Console]::OutputEncoding
[Console]::OutputEncoding = $utf8
$env:RIMRUNNER_ISOLATED_ROOT = $isolated
Push-Location $template
try {
    foreach ($pair in @(@('manifest', 'rimrunner.toml'), @('runtime', 'RimRunner/runtime.toml'), @('preflight', 'RimRunner/preflight.toml'),
                        @('excluded', 'RimRunner/excluded.toml'), @('newgame', 'RimRunner/data/newgame.toml'))) {
        $generated = & (Join-Path $cliBin 'rimrunner.exe') descriptor-template $pair[0] 2>&1
        Assert-Success "生成样板 $($pair[1])"
        $target = Join-Path $template $pair[1]
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        [IO.File]::WriteAllText($target, (@($generated) -join "`n") + "`n", $utf8)
    }
}
finally {
    Pop-Location
    Remove-Item Env:RIMRUNNER_ISOLATED_ROOT
    [Console]::OutputEncoding = $encoding
}

# 缺 package.json 的安装目录 CLI 拒用：先删除，全部写完再写回
$manifest = Join-Path $dist 'package.json'
if (Test-Path $manifest) { Remove-Item $manifest -Force }
$distEngine = Join-Path $dist 'engine'
$distTool = Join-Path $dist 'tools\FixtureIndex'
$distAnalyzers = Join-Path $dist 'analyzers'
New-Item -ItemType Directory -Force (Join-Path $distEngine 'ref'), $distTool, $distAnalyzers | Out-Null
Move-Locked $dist
Copy-Tree $cliBin $dist
Copy-Tree $toolBin $distTool
Copy-Item $analyzerDll $distAnalyzers -Force
Copy-Item (Join-Path $engineBin 'RimRunner.dll') $distEngine -Force
Copy-Item $refDll, (Join-Path $engineBin 'RimRunner.xml') (Join-Path $distEngine 'ref') -Force
Copy-IfChanged (Join-Path $staging 'engine\ref\RimRunnerRef.cs') (Join-Path $distEngine 'ref\RimRunnerRef.cs')
Copy-Item (Join-Path $staging 'authoring.json'), (Join-Path $staging 'checks.json') $distEngine -Force
Copy-Tree (Join-Path $engineDir 'Content') (Join-Path $distEngine 'Content') @('/MIR')
Copy-Tree $stagedSource (Join-Path $distEngine 'src') @('/MIR')
Copy-IfChanged (Join-Path $staging 'RimRunner.targets') (Join-Path $dist 'RimRunner.targets')
Copy-Item (Join-Path $staging 'README.md'), (Join-Path $staging 'THIRD-PARTY-NOTICES.txt'), (Join-Path $root 'LICENSE') $dist -Force
foreach ($name in 'template', 'examples', 'skills') { Copy-Tree (Join-Path $staging $name) (Join-Path $dist $name) @('/MIR') }

$describe = git -C $root describe --tags --always --dirty 2>$null
if ($LASTEXITCODE -ne 0) { $describe = 'unknown' }
$json = [ordered]@{
    commit = "$describe"; built = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'; edition = 'release'
    includes = [ordered]@{ observations = $false; selfTests = $false }
    game = [ordered]@{ version = $game.Version; files = $game.Files }
} | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($manifest, ($json -replace "`r`n", "`n") + "`n", $utf8)
Write-Host "[dist] $dist  $describe"
if (-not $Release) { exit 0 }

$releaseDir = Join-Path $root 'release'
$releaseStaging = Join-Path $root 'obj\release-staging'
if (Test-Path $releaseStaging) { Remove-Item $releaseStaging -Recurse -Force }
Copy-Tree $dist $releaseStaging @('/E', '/XF', '*.old-*')
New-Item -ItemType Directory -Force $releaseDir | Out-Null
$zip = Join-Path $releaseDir "rimrunner-$tag.zip"
Compress-Archive -Path (Join-Path $releaseStaging '*') -DestinationPath $zip -Force
Write-Host "[release] $zip  SHA256 $((Get-FileHash $zip).Hash)"
exit 0

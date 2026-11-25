# 写一个场面：展台、镜头与路径录像

场面 = 立起、装好、相机聚焦、保持暂停的一幕。展台实例按开发机真实取值生成画面与声音；拍摄实例取自动实例的取值（`uiScale` 钉 1），只有音量取原版缺省（mp4 的音轨见下文「镜头」），分辨率按镜头或描述符的 `windowSize` 设。
能断言的点写用例，一张静态图能判的写读数用例交截图（派生 `RimRunnerReadout`，`ctx.AddScreenshot`）；场面只服务「人要连续看、动手操作、或在多件之间主观比较」的那一面。

场面类派生 `RimRunnerScene`，再按用途实现角色接口：

| 角色 | 用途 |
|---|---|
| `IRimRunnerShowcase` | 进展台名册：`rimrunner showcase <Id>` 在展台实例里立场，面板上点步骤、接管操作 |
| `IRimRunnerFilm` | 声明 `Shots`，`rimrunner film` 拍成静帧 / GIF / MP4（下文「镜头」） |

路径录像（`film --paths`，下文「路径录像」）只看步骤表：任何场面都能按步骤表派生片段。

```csharp
public sealed class Showcase_Example : RimRunnerScene, IRimRunnerShowcase, IRimRunnerFilm
```

场面的 `Assemble` 与每一步动作拿到的是 `RimRunnerRegressionContext`，与回归用例同一份夹具与原语。
`Id`、`Title` 与 `Steps` 由 CLI 从编译出的 DLL 读进场面名册，只能写常量形状（`contract.md` §1.1）。

场面的看点与判读说明写在场面类头的 `//` 注释里，不写进 `ctx.AddDiagnostic`。场面里的 `AddDiagnostic` 作为消息输出到展台面板与 CLI，只记运行期测得的单值或分支事实（如某可选包未加载、某段推进的 tick 数）。

场面类上的可选声明（种子、步骤缺省时限、立在哪张图）用 `rimrunner engine find RimRunnerScene.` 查。

## 步骤怎么推进

一步 = 动作 → 等完成条件 → 观察，三段都完成后才进入下一步。条件成立后要给效果留时间，写 `dwellTicks`。参数见 `rimrunner engine find RimRunnerSceneStep.`。

```csharp
public IReadOnlyList<RimRunnerSceneStep> Steps => new[]
{
    new RimRunnerSceneStep("悬停殖民者", ctx => ctx.HoverOn(ctx.GetPawn(PawnKey))),
    new RimRunnerSceneStep("等 tooltip", _ => { }, after: "悬停殖民者", dwellTicks: 30),
};
```

**完成条件按位置、状态与 tick 数判定**：拍摄实例已钉住 `Time.time` 与 `realtimeSinceStartup`，随推进的 tick 或出帧变化，与展台实例的墙钟不同步，同一条读墙钟的条件在拍摄实例与展台实例上成立的时刻不同。

**动作留下带 `forcePause` 的窗口时，展台无法收到 tick**：展台的观察段因此视为完成，直接进入下一步；`until` 当场判一次，不成立即本步中止并写出暂停来源。长事件、逆重飞船起降过场这类会自行结束的强制暂停照常等到结束。拍摄（含路径录像）自己推 tick，不受影响，`dwellTicks` 在拍摄里就是窗口在片段里停留的时长。窗口步骤需要等条件时，把条件写成动作结束时就能当场判定。

**要拍到过程，动作只下命令，等待写进 `until`**：动作里调 `RimRunnerTimeDriver.AdvanceBurst` 一类夹具同步推的 tick 在一帧里推完，片段里看不到。

## 镜头：从场面出静帧 / GIF / MP4

实现 `IRimRunnerFilm` 的场面用 `Shots` 声明能拍哪几个镜头，`rimrunner film` 拍。用途是宣传素材（封面、功能演示）。

```csharp
public IReadOnlyList<RimRunnerShot> Shots => new[]
{
    RimRunnerShot.Still("cover", new RimRunnerShotOutput()),
    RimRunnerShot.Clip("demo", new RimRunnerShotOutput(1280, 720, maxBytes: 3_000_000), RimRunnerClipFormat.Gif | RimRunnerClipFormat.Mp4,
        through: "冷却完成", fps: 30, speed: 1, setup: "手里拿起一叠钢材", tailSeconds: 1f,
        camera: RimRunnerShotCamera.Focus().PanTo(RimRunnerShotCamera.On(PawnKey), 3f)),   // PawnKey：装配里经 ctx.RegisterPawn 登记的键
};
```

`Clip` 的片段时长由 `through` 那一步何时完成决定。`RimRunnerShotOutput` 的像素尺寸即拍摄窗口尺寸，未指定时取描述符 `windowSize`，路径录像的窗口尺寸同样取自 `windowSize`。界面档与用例截图同一套（`contract.md` §2.1），镜头缺省 `Scene`。逐项签名与取值范围：`rimrunner engine find RimRunnerShot. RimRunnerShotCamera. RimRunnerShotOutput.`。

**声音**：mp4 片段带音轨，gif 与静帧不带音轨。带 `--mute` 时 mp4 也不带音轨。音量取原版缺省。片段里经 `SoundDef` 或 `AudioSource` 播放的声音都进音轨，产品 mod 自己的音效也一样，不用声明。
音乐只播放 `music:` 声明的曲目（SongDef 的 defName），从第一帧起整曲播放，镜头里不切换曲目。未指定 `music:` 时没有音乐。`music:` 为空或镜头格式不带 Mp4 时，场面整个被拒，报错正文点名该镜头的 `music`。声明的 defName 在本名单里查不到时，开拍前拒拍，终端输出 `拍摄失败：镜头声明的音乐「…」不是已加载的 SongDef`，本镜头不产出。音轨与画面一样逐次相同。
终端输出 `mp4 不带音轨`、`音轨缺一个 clip` 或 `音轨未重放事件表里的这些行` 一类警告时，按 `rimrunner` skill 的 `troubleshooting.md` 查。

**每个镜头从同一个世界出发**：世界被改动过即先回载基线档，再立场、执行到 `setup`、钉镜头种子。
同一镜头连拍两遍要产出同样的拍摄产物，场面里的随机须走 `Rand`；`UnityEngine.Random`、按哈希排序的集合、墙钟读数不受种子控制。

**挑片段里发生的事**：镜头种子（`Clip` 的 `seed:`，缺省按名字推）定第一帧之后的随机流，场面种子定装配。
`rimrunner film <场面Id>/<镜头名> --roll N` 用 N 个不同的镜头种子各拍一条，出到本次拍摄目录的 `<镜头名>.<种子>.<格式>`，第 1 条是当前种子。
挑中的那条把文件名里的种子写进 `seed:`。`--roll` 只接受片段。静帧的第一帧还没推 tick，换种子拍出的是同一张。

**拍摄目录**：每次拍摄进 `film/<场面Id>/runs/<时间>-<runId>[-<tag>]/`。场面目录上的 `<镜头名>.latest.<格式>` 是下一次拍摄就覆盖的最新副本（`--roll` 的候选只落本次 `runs/`，最新副本仍属上一次拍摄）。发布拍摄产物、改前改后对比用 `runs/` 下的路径。`rimrunner film compare <场面Id>[/<镜头名>]` 把上一次与这一次（`--a` / `--b` 指定 runId 或 tag）左右拼接成一份。
左下角带画面标识（runId、提交、tag），宣传素材加 `--clean` 出不带标识的版本。

**种子只定随机流**：装配消耗的随机数一变（产品代码、名单），同一个 `SceneSeed` 生成的就是另一个人，拍摄产物照常产出。
主角的性别、发型、名字这类须固定的，在 `Assemble` 里生成之后直接设。

## 路径录像：按步骤表核对效果

`rimrunner film --paths [<场面Id>…]` 把场面的每条叶路径（从立场到一个叶步骤）拍成一段片段与逐步静帧，用来逐步核对效果是否出现。产出路径与帧率见 `film --help`。

- 倍速取每步的 `filmSpeed`，缺省 1：推几千 tick 的步骤自己写 `filmSpeed`。
- 每张静帧在该步推满观察时长那一帧取，只含这一步及之前的结果。
- 每条路径与镜头一样从基线档立场，同一条路径连拍两遍逐帧相同。
- 机位、尺寸、界面档固定。要宣传用的画面写镜头。

`rimrunner film --test` 只立场、不出帧，逐条叶路径执行一遍，用来发现产品改动让久未打开的场面无法装配。

展台面板、拍摄结果与 live 回执按来源分标列出场面里的问题，与用例报告里两类分列同形。场面代码里失败的断言标「失败断言」，装配与动作继续执行；场面期出的引擎警告标「带键警告」，label 是警告键，场面无法认领，出现即改场面代码；拍摄另记「拍摄提示」，指镜头本身的问题（悬停目标在画面外、动作内同步推进的 tick 未录入片段等）。`film --test` 把失败断言与带键警告判红。展台里由操作者手动让原版拆掉的地图，不输出拆图警告。

## 悬停：拍 tooltip、格读数与温度

拍摄实例不显示窗口，鼠标平时停在画面外。动作里调 `ctx.HoverOn(thing)` / `ctx.HoverAt(cell)`，鼠标停到目标上，可拍出原版的悬停提示；`ctx.EndHover()` 撤销。设置之后跨步骤有效，每次立场从没有悬停开始。只在拍摄实例上生效；展台实例上只落一条面板消息，鼠标仍归操作者；自动实例上调用即抛。

- **悬停之后写 `dwellTicks: 30` 再取像**：原版 tooltip 在鼠标落到目标上 0.45 秒（约 28 tick）后才出现，拍摄期的时钟按 tick 推进。
- **手写镜头显式 `.WithUi(RimRunnerUiLevel.Screen)`**：tooltip、格读数、右下角的温度读数只有 `Screen` 档能画出（温度覆盖层 `Map` 档也画），镜头缺省是 `Scene`。路径录像固定 `Screen`。

悬停不改选中、不拖框、不触发边缘滚动。

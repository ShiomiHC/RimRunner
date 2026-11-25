# 名单

## 格式与继承

`<dir>/modlists/<名单名>.toml`，文件名去掉扩展名即 `--modlist` 的取值，也是 `defaultModList` 写的名字。
任一收 `--modlist` 的命令不带取值时列出全部名单（带 `--tree` 时为那棵树的，否则为本树的；mod 数、继承、进档用被引名单的档与缺省标在行里），命令不执行，退出码同用法错误。
名单名只能用字母数字与 `-` `_`：存档与台账按名单名命名。
名单是 TOML 文档：`mods = ["packageId", ...]` 按加载顺序写启用清单。`mods_from = "<原名单>"` 写在顶层，继承规则见 `rimrunner run --help`。
`brrainz.harmony` 写第一行：排在它前面的 mod 自带旧版 `0Harmony` 时，引擎无法装入，冷启动等不到引擎就绪。
RimRunner 专属指令写在 `[rimrunner]` 段，可写键：`textures`、`save_from`、`omit_required`、`groups`、`known_failing`、`acknowledge_startup_error`、`acknowledge`、`component_exemption`。
只改 `[rimrunner]` 配置、不改清单的名单（如只改 `textures`）在 `[rimrunner]` 写 `save_from = "<原名单>"`，进档用原名单的档，本名单不建档。两份名单的生效清单须同序相同，否则冷启动前就拒

DLC 也是 `mods` 数组里的一行，id 是 `ludeon.rimworld.royalty`、`.ideology`、`.biotech`、`.anomaly`、`.odyssey`，写在 `ludeon.rimworld` 之后。实例只启用名单里写出的 DLC。

名单里写错的行无法装入，冷启动时 `[WARN] mod 名单 … 上的项未装入进程` 点名该行；写错的是必需集合（非可选单元与 `requiredMods`）里的 id 时，启动游戏前即以 `生效 mod 名单里缺少必需 packageId：…` 拒批。
同一个 id 写两次按第一次的位置装，`[名单]` 行留一条说明。测试 mod 由 CLI 追加到名单末尾

## 按组选用例

某份名单平时只执行几组用例时在 `[rimrunner]` 写 `groups = ["<组>", "<组>"]`（组由用例类上的 `[RimRunnerGroup]` 声明，见 `rimrunner-authoring` 的 `contract.md`）：
这份名单下的回归批只选带所列任一组的产品用例，随包检查照常执行；`--env-checks` 不受所列组过滤。命令行 `--group` 覆盖名单的 `[rimrunner].groups`，`--group all` 不按组过滤。
每个产品用例类都须有组（环境体检除外），未标组的类编译即报 RR0001。接入时先给全部用例类标组，`rimrunner cases groups` 看各组的用例。
组名写错（用例名册里无产品用例含该组）时冷启动前即拒。按组跳过的用例不入台账，报告另记，终端的 `[按组跳过]` 行只输出条数与组。
给已有台账的名单首次加 `[rimrunner].groups` 时，按组跳过的那批用例在本名单下从此不再有新格，要在台账上留痕就对它们 `rimrunner history mark --modlist <名单> <Id…>` 一次。

## 许可与认领

### `known_failing`

DAY1（用例期天光恒亮）、STQ1（叙事者事件扣发）在某份名单下失败、且已确认本名单的用例不受影响时，
在 `[rimrunner]` 用 `known_failing = ["<Id> => <理由>"]` 许可，一项一条：DAY1 与 STQ1 的失败降为警告 `ENV-CHECK-KNOWN-FAILING`，体检照常执行、照列。
许可只计数。`[rimrunner].known_failing` 只收 DAY1 与 STQ1，指定别的 Id 出 `INERT-DECLARATION`，该条仍参与判定。

### `acknowledge_startup_error`

启动错误级日志批次拒绝时，先判该日志属应修复的缺陷还是名单固有：只有名单固有的（有意不带某 DLC、名单里某个 mod 在本版本上无法正常装载）才用
`[rimrunner].acknowledge_startup_error = ["<子串> => <理由>"]` 许可。一项一条，一条许可可认领多条错误级日志；子串按字面（区分大小写）比对错误级日志正文。子串取正文里稳定的部分（类型名、def 名），
避开 pawn 名、数值这类逐轮会变的部分。许可只计数。零命中不拦截批次

### `acknowledge`

批级警告（`[WARN]` 带键警告组里主语为「窗外」的条目，例如 `MODLIST-NOT-LOADED`、`BASELINE-HEADER`，
以及冷启动、回基线、clean-save 载档读档本体段的 `SAVE-WINDOW-WARNING`）
在 `[rimrunner]` 用 `acknowledge = ["<键> => <理由>"]` 认领，同样一项一条、只计数。主语为用例 Id 的条目由用例类上的 `[RimRunnerAcknowledge]` 认领，名单的 `[rimrunner].acknowledge` 无法认领

他 mod 挂在原版代码上、在测试图上读当前地图的那类读，`CURRENT-MAP-READ` 行首方括号后标 `环境链`，本批内按链并成一行，列涉及几条用例并标出链锚。
确认他 mod 的补丁读到的值只决定画面、声源、视角这类表现，不流进用例判定的量，再在名单 `[rimrunner]` 按归属与链锚认领：
归属照抄行首方括号里的字符串，链锚照抄该行标出的内容，写法为 `acknowledge = ["CURRENT-MAP-READ @ mod:<packageId> / <类型.方法> => <理由>"]`。
链锚是他 mod 被原版调进来的那一帧，只认领经这一帧进来的链；同一读点被产品代码直接调用的那一条不是环境链，仍逐用例输出；
要认领，先确认读到的值是否被产品使用，再在用例类上认领。`CURRENT-MAP-READ` 在名单里只能这样写，写成整键认领不生效

### `component_exemption`

他 mod 的整局、世界、图组件到某个游戏年龄才写一次的状态（如按开局后 tick 数发教程信、记下已发），落在批里越过那个时刻的那条用例上，
引擎报 `RESIDUE`「整局组件 X 的存档状态与开始执行前不匹配」；批序改变即落到另一条用例上，用例类上的认领无法准确挂上。
确认该状态非用例造成之后，在 `[rimrunner]` 用 `component_exemption = ["<组件类型全名>:<元素>[,<元素>…] => <理由>"]` 使这几个存档元素退出比对，一项一个组件。
元素名取报告正文里「顶层元素」后的名字。只收他 mod 的组件，被测产品自己的组件在产品描述符里经 `ComponentStateExemption` 登记；
类型无法识别、或指定的组件属产品组件时，出 `INERT-DECLARATION`；理由为空出 `EMPTY-REASON`；两种情形下声明都不生效。随时间走的计数器由 `build-save` 自动量出、自动豁免

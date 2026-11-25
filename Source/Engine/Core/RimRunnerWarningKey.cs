namespace RimRunner;

/// <summary>引擎警告的稳定键：认领按键点名。</summary>
/// <remarks>用例级的在用例类上挂 RimRunnerAcknowledge 认领，批级的在 mod 名单写 [rimrunner].acknowledge 认领。</remarks>
public static class RimRunnerWarningKey
{
    /// <summary>Level 申报非 World，调用闭包触碰到 World 层 API。</summary>
    public const string LevelUnder = "LEVEL-UNDER";

    /// <summary>Level 申报高于调用闭包触碰到的层。</summary>
    /// <remarks>闭包看不见经 vanilla 基类型、object 或接口调用的本产品与引擎的 override。</remarks>
    public const string LevelOver = "LEVEL-OVER";

    /// <summary>断言的条件实参是字面量 true。</summary>
    /// <remarks>不可认领：要降级改用 AddDegradedNote，整条不适用用 NotApplicableReason。</remarks>
    public const string AssertConstantTrue = "ASSERT-CONSTANT-TRUE";

    /// <summary>按图开销计量未钉 tick 分档，被测图上同一 def 的物交付档位随相机分岔。</summary>
    /// <remarks>运行期出：计量窗前后被测图含当前地图时查，同一 def 既有视野内档又有离屏档 15 即出。钉档用 ctx.PinTickRate。</remarks>
    public const string TickRateUnpinned = "TICK-RATE-UNPINNED";

    /// <summary>全局随机精神态开关收尾未还原，或关闭该开关期间未钉住的 pawn 仍进入非强制精神态。</summary>
    /// <remarks>全局开关挡不住劝化失败直调的社交斗殴与 hediff 精神态。按 pawn 用 ctx.PinAgency，全图用 ctx.SuppressRandomMentalStates。</remarks>
    public const string MentalSwitchWrite = "MENTAL-SWITCH-WRITE";

    /// <summary>用例代码调 PawnUtility.ForceWait 当窗口钉。</summary>
    /// <remarks>到期拍数固定。无期限站到被打断用 RimRunnerPawnGen.StandUntilInterrupted；钉住与放开用 ctx.PinAgency / ctx.ReleaseAgency。</remarks>
    public const string ForceWaitWindow = "FORCE-WAIT-WINDOW";

    /// <summary>非 Readout 用例写错误级日志许可。</summary>
    public const string JudgingLogExemption = "JUDGING-LOG-EXEMPTION";

    /// <summary>可分片用例执行到结束时，认领到的格有租约未调 Complete。</summary>
    /// <remarks>运行期出。用例体中止时不出这条警告。</remarks>
    public const string ShardLeaseOpen = "SHARD-LEASE-OPEN";

    /// <summary>可分片用例不分片执行到结束时，有格没取：枚举出的格还有没取的，或一次都没取。</summary>
    /// <remarks>运行期出。用例体中止时不出这条警告；一次都没取的只写这一句，不列格：没枚举过就不知道有哪些格。</remarks>
    public const string ShardCellsUntaken = "SHARD-CELLS-UNTAKEN";

    /// <summary>理由或说明文字为空。</summary>
    public const string EmptyReason = "EMPTY-REASON";

    /// <summary>AssertNoneOffending 的说明与 label 去掉空白与标点后相同。</summary>
    public const string ExplanationEchoesLabel = "EXPLANATION-ECHOES-LABEL";

    /// <summary>读数正文为空或未写标题。</summary>
    public const string EmptyText = "EMPTY-TEXT";

    /// <summary>截图说明或读数正文标题超过 40 字。</summary>
    /// <remarks>说明与标题只写辨别这一份产出所需的内容。</remarks>
    public const string LongCaption = "LONG-CAPTION";

    /// <summary>声明在本用例的档位或形态下不起作用。</summary>
    /// <remarks>写空串、写重复、挂在不量这项声明的档上、指定不存在的键一类：删掉声明不改任何执行。</remarks>
    public const string InertDeclaration = "INERT-DECLARATION";

    /// <summary>日志许可的写法让这条许可无法认领任何日志，或与另一条重复。</summary>
    public const string ClaimShape = "CLAIM-SHAPE";

    /// <summary>日志许可的子串过宽：整串是异常类型名，或本轮认领到的正文把数字归一后不止一种。</summary>
    /// <remarks>收窄：子串取抛出点消息里的片段，或加 StackAnchors。</remarks>
    public const string AckBroad = "ACK-BROAD";

    /// <summary>认领在用例执行到尾的轮里连续未命中，轮数超过它的 Tolerance。</summary>
    /// <remarks>不可认领。认领的现象已不再出现就删掉认领；出现得稀疏的调高 Tolerance；按参数够不到的轮在用例体里调 ctx.MarkAckUnreachable 或 ctx.MarkLogAckUnreachable。分片组合起各片按组判。</remarks>
    public const string AckNoHit = "ACK-NO-HIT";

    /// <summary>本轮声明够不到的认领仍然命中。</summary>
    /// <remarks>不可认领：改正声明够不到的那个条件。这次命中照常计入。</remarks>
    public const string AckReachHit = "ACK-REACH-HIT";

    /// <summary>测试程序集在装载期引用本进程里只由可缺席包提供的程序集。</summary>
    /// <remarks>批级，引擎登记期出。</remarks>
    public const string AbsentableLoadTime = "ABSENTABLE-LOAD-TIME";

    /// <summary>用例可达的方法引用本进程里只由可缺席包提供的程序集，未声明依赖且未隔离。</summary>
    public const string AbsentableUnlabeled = "ABSENTABLE-UNLABELED";

    /// <summary>沾染可缺席包的隔离方法在方法体内判包在场。</summary>
    public const string PresenceInIsolated = "PRESENCE-IN-ISOLATED";

    /// <summary>用例可达的代码用 ModsConfig 判包在场。</summary>
    public const string ModsConfigActive = "MODSCONFIG-ACTIVE";

    /// <summary>AddSample 的行为空。</summary>
    public const string SampleNoRows = "SAMPLE-NO-ROWS";

    /// <summary>未声明 EmitsSamples 却调 AddSample。</summary>
    public const string SampleUndeclared = "SAMPLE-UNDECLARED";

    /// <summary>AddSample 交的样本窗内改过 tick 分档钉。</summary>
    public const string SamplePinChanged = "SAMPLE-PIN-CHANGED";

    /// <summary>读数档的调用闭包有计时调用，未声明 QuietMachine。</summary>
    public const string TimingUndeclared = "TIMING-UNDECLARED";

    /// <summary>引擎生成的 pawn 施法时不在名册，读 ObservedAbilities 时出。</summary>
    /// <remarks>不读观测缓冲的用例不出此警告。</remarks>
    public const string UnobservedCaster = "UNOBSERVED-CASTER";

    /// <summary>已观测 pawn 射出的弹落地不在名册，读 ObservedProjectiles 时出。</summary>
    /// <remarks>不读观测缓冲的用例不出此警告。</remarks>
    public const string UnobservedShooter = "UNOBSERVED-SHOOTER";

    /// <summary>观测缓冲同一类事件超过上限，此后的事件丢弃。</summary>
    public const string ObservationOverflow = "OBSERVATION-OVERFLOW";

    /// <summary>加直接关系，该关系的 worker 不按直接关系表判。</summary>
    /// <remarks>血缘关系用 Parent / Child 一类直接关系。</remarks>
    public const string DerivedRelation = "DERIVED-RELATION";

    /// <summary>有殖民地亲属的 pawn 在迷雾里，见到亲属的通知被 vanilla 跳过。</summary>
    /// <remarks>生成后调 map.fogGrid.Unfog(cell) 即不出。</remarks>
    public const string FoggedKin = "FOGGED-KIN";

    /// <summary>下令时排队键读作按下、未传 requestQueueing 且键态改变排入结果。</summary>
    /// <remarks>要排队时显式传 requestQueueing: true；自造 Event 不带修饰键与排队键，用完还原 Event.current。</remarks>
    public const string QueueKey = "QUEUE-KEY";

    /// <summary>容器 TryAcceptThing 返回 true 而物仍在地图上。</summary>
    public const string CasketAccept = "CASKET-ACCEPT";

    /// <summary>用例期内往已有植入体的部位再挂植入体。</summary>
    /// <remarks>正文带部位、旧件与新件的 defName。</remarks>
    public const string AddedPartStack = "ADDED-PART-STACK";

    /// <summary>拆图时该图生成后一 tick 都未推过，且有 GameComponent 持有该图或图上的物。</summary>
    /// <remarks>正文带引用路径。ctx.GenerateMap 登记的图与 RimRunnerMapTeardown 拆的图拆前先推 1 tick，不出此警告；自行拆图的，拆前推 1 tick 即不出。</remarks>
    public const string ZeroTickMap = "ZERO-TICK-MAP";

    /// <summary>落图物的交互格在图外。</summary>
    public const string InteractionCell = "INTERACTION-CELL";

    /// <summary>自愿加入编队的起始成员因派系与 lord 派系不匹配而离队。</summary>
    /// <remarks>运行期出：只建队不推 tick 不出，仪式编队不在射程。lord 派系取成员派系；自造夹具 pawn 传 Faction.OfPlayer。</remarks>
    public const string LordFaction = "LORD-FACTION";

    /// <summary>已回收的 pawn 连续两次采样仍被引用。</summary>
    /// <remarks>正文每条持有路径行首标根字段所在程序集的归属。根在第三方 mod 的，产品描述符登记 StaleReferenceSweeper 清；持有者为收尾闭包时，收尾改用 ctx.DeferUntilRecycled。</remarks>
    public const string Retention = "RETENTION";

    /// <summary>假人用未声明行为的自定义思维树。</summary>
    public const string UndeclaredThinkTree = "UNDECLARED-THINK-TREE";

    /// <summary>假人的思维树声明会走动、会出手或主动出手，身体无法做到。</summary>
    /// <remarks>会走动配 doesntMove 的种族，会走动或主动出手配非 Mobile 行动档，会出手配没有近战手段的 kind。</remarks>
    public const string DummyTreeMismatch = "DUMMY-TREE-MISMATCH";

    /// <summary>存读档窗里出现已知存档完整性族以外的警告级日志。</summary>
    /// <remarks>checkpoint 屏障窗里的记在用例名下，由用例类认领。冷启动、回基线与 clean-save 载档的读档本体段由 mod 名单的 [rimrunner].acknowledge 认领。run --save 批里不记此键，只列成观测。</remarks>
    public const string SaveWindowWarning = "SAVE-WINDOW-WARNING";

    /// <summary>收尾后口袋图入口未还原，引擎已在收尾时清掉。</summary>
    /// <remarks>口袋图入口在 RimRunnerPocketPortals.EnterSpawnContext 的 using 内生成即不出。</remarks>
    public const string PortalResidue = "PORTAL-RESIDUE";

    /// <summary>收尾后不可摧毁物留在图上，引擎已在收尾时移出。</summary>
    /// <remarks>借来的 vanilla 参照物用 ctx.TrackBorrowed 登记：收尾只 DeSpawn。</remarks>
    public const string IndestructibleResidue = "INDESTRUCTIBLE-RESIDUE";

    /// <summary>收尾后夹具的进程级状态未回到用例开始执行前，引擎已在收尾时复位。</summary>
    /// <remarks>多阶段用例按 phase 核；交棒对象名下的按 pawn 登记留到末 phase 再核。</remarks>
    public const string FixtureResidue = "FIXTURE-RESIDUE";

    /// <summary>收尾后信件仍持有已回收的 pawn，引擎已在收尾时摘除。</summary>
    public const string LetterResidue = "LETTER-RESIDUE";

    /// <summary>收尾后仍有孤儿 pawn，引擎已在收尾时丢弃。</summary>
    /// <remarks>vanilla 连带生成的 pawn（亲属、奖励、艺术品、书）在用例内用 RimRunnerOrphanSweep.Snapshot 与 SweepAddedSince 自收。</remarks>
    public const string OrphanPawn = "ORPHAN-PAWN";

    /// <summary>被测产品的组件存档状态收尾时与开始执行前不匹配。</summary>
    /// <remarks>图层的差随回载基线档或弃图消失；世界与整局层不回档，差带进其后的用例。</remarks>
    public const string ProductComponentDrift = "PRODUCT-COMPONENT-DRIFT";

    /// <summary>靶规格写 death: Forbidden，用例期间死亡且未豁免。</summary>
    public const string TargetDeath = "TARGET-DEATH";

    /// <summary>行动档 AwakeImmobile 的靶在仍被钉住时失去清醒。</summary>
    /// <remarks>正文带失去清醒的 tick，以及当拍的意识与疼痛。</remarks>
    public const string TargetAwakeLost = "TARGET-AWAKE-LOST";

    /// <summary>规格靶受用例开始执行前已在图上的 pawn 或建筑的伤。</summary>
    /// <remarks>正文按靶聚合来源、次数与实伤。</remarks>
    public const string TargetForeignDamage = "TARGET-FOREIGN-DAMAGE";

    /// <summary>DisableThreat 持有者推 tick 期间重新算威胁。</summary>
    /// <remarks>keepApplied 为假的不记；逐 tick 采样，每 pawn 每段至多记一次。</remarks>
    public const string ThreatDisableLord = "THREAT-DISABLE-LORD";

    /// <summary>用例期内发生 JobDriver_Wait 邻格自动近战且涉及本用例 pawn。</summary>
    /// <remarks>正文带攻击方、目标与当拍 tick；只放落点而未出手，或主动 AttackMelee 均不出此警告。</remarks>
    public const string SpawnAdjacentHostile = "SPAWN-ADJACENT-HOSTILE";

    /// <summary>RimRunnerTickDelivery 的窗内超过 1 个 tick 未经 DoTick。</summary>
    /// <remarks>正文带窗长与进 DoTick 的次数。</remarks>
    public const string TickDeliveryGap = "TICK-DELIVERY-GAP";

    /// <summary>驱动期间拆未经登记且世界池新增 pawn 的图。</summary>
    /// <remarks>拆后未向世界池新增 pawn 的空图不报。场面里只在场面代码执行作用域与步骤链推进期判。</remarks>
    public const string UnregisteredMapDeinit = "UNREGISTERED-MAP-DEINIT";

    /// <summary>局部场面当场改过的全局状态在换场清理后没回到立场前。</summary>
    /// <remarks>只在展台与拍摄的场面清单里出现，用例里没有这条。正文带改过的指纹键与前后值。</remarks>
    public const string SceneScopeResidue = "SCENE-SCOPE-RESIDUE";

    /// <summary>假人规格的思维树与 DefDatabase 同名项不是同一对象。</summary>
    public const string ThinkTreeUnregistered = "THINK-TREE-UNREGISTERED";

    /// <summary>调到 1.7 仿制面（UnityStubs）的空桩成员。</summary>
    /// <remarks>行首标调用方归属，正文带桩名、源位置与首次命中时的调用方。同一桩在一条用例里只记一行带次数，按归属认领这个键的用例按调用方归属分行。用例窗外的命中归批级。认领须写 Origin。</remarks>
    public const string UnityStub = "UNITY-STUB";

    /// <summary>假人定义写会激活 vanilla 行为的字段，且未在 def 上声明。</summary>
    /// <remarks>列在 def 上 RimRunnerDummyIntentExtension 的 fields 里的字段不记。</remarks>
    public const string DummyDefActivates = "DUMMY-DEF-ACTIVATES";

    /// <summary>当前地图读取：独立小图用例在 AsCurrentMap 作用域外读当前地图或视野矩形。</summary>
    /// <remarks>只记调用栈经过用例代码的读；推 tick、生图期间与 vanilla 表现层的读不记。行首标读点归属，同处合并带 ×N。认领须写 Origin，认领的归属改道后只计次。未观测段另记 CURRENT-MAP-PROBE-BLIND。</remarks>
    public const string CurrentMapRead = "CURRENT-MAP-READ";

    /// <summary>当前地图读取有一段未观测：这条用例的 CURRENT-MAP-READ 不全。</summary>
    /// <remarks>两种段各一行、带 ×N（未观测的读数）：截栈触顶（每条用例 500 个不同读者帧）之后；用例体内生成独立小图期间，地图组件初始化读错图在此看不见。两键皆无才等于这条在作用域外没读当前地图。不带归属，认领它即放弃那一段观测。</remarks>
    public const string CurrentMapProbeBlind = "CURRENT-MAP-PROBE-BLIND";

    /// <summary>独立小图上的用例体内有东西生成到冻结的图上。</summary>
    /// <remarks>记用例体内（含体内推的 tick）落到冻结图上的物，按 def 与发起方合并带 ×N，行首标发起方归属，认领须写 Origin；此期间 Find.AnyPlayerHomeMap 一族与任务挑图挑不中冻结的主图。</remarks>
    public const string FrozenMapSpawn = "FROZEN-MAP-SPAWN";

    /// <summary>调用闭包里有 ctx.AddScreenshot，MinTextures 申报低于 native。</summary>
    /// <remarks>闭包可达不等于运行时会拍；真拍时低档实例的截图入口照拒。</remarks>
    public const string ScreenshotTexturesUndeclared = "SCREENSHOT-TEXTURES-UNDECLARED";

    /// <summary>同一 phase 里两张截图取景全同，出的是同一张图。</summary>
    /// <remarks>截图都在 phase 返回后拍。</remarks>
    public const string ScreenshotSameShot = "SCREENSHOT-SAME-SHOT";

    /// <summary>相机锁在整个 phase 里未起过作用：锁的目标图从未成为当前地图。</summary>
    /// <remarks>小图锁要在 AsCurrentMap 作用域内读视野。目标图在本段已是当前地图的锁，未读视野也不报。</remarks>
    public const string CameraLockInert = "CAMERA-LOCK-INERT";

    /// <summary>叙事者照发时，目标不归本用例（冻结的主图、池里空闲的小图）的事件被拦截、未执行。</summary>
    /// <remarks>行首标发起方归属，认领须写 Origin。用例窗外拦截的归批级。</remarks>
    public const string StorytellerBlocked = "STORYTELLER-BLOCKED";

    /// <summary>用例结束后该用例留下的东西仍在。</summary>
    /// <remarks>报完由引擎关掉留在栈上的阻断窗，并扑灭图上的火。留主图的 World 级用例，主图物件在最后一个 phase 收尾时还原，多阶段的按屏障批。留痕的小图弃用。</remarks>
    public const string Residue = "RESIDUE";

    /// <summary>用例窗外进栈的窗口，到某条用例开始执行时仍未关闭。</summary>
    /// <remarks>批级，每扇窗只报一次。</remarks>
    public const string StrayWindow = "STRAY-WINDOW";

    /// <summary>用例之间入队的叙事者事件，被引擎清出队列。</summary>
    /// <remarks>批级。</remarks>
    public const string StorytellerResidue = "STORYTELLER-RESIDUE";

    /// <summary>用例读 IL 有读取失败，这份扫描统计未过 RimRunnerIlScanGate。</summary>
    /// <remarks>零读取失败且未过闸时不出此警告。</remarks>
    public const string IlScanUngated = "ILSCAN-UNGATED";

    /// <summary>冻结已拦下过需求的自然推进之后，用例又读该 pawn 的需求或心情值。</summary>
    /// <remarks>冻结未拦下过推进（未满一次需求结算间隔）时读数与自然值一致，不出。验冻结本身读 IsFrozen。</remarks>
    public const string NeedsFrozenRead = "NEEDS-FROZEN-READ";

    /// <summary>同一条 mote 观测被两处非同心的邻域计数目标命中。</summary>
    /// <remarks>同心包含与完全重复的读取不报；几何相交但交集内无 mote 也不报。</remarks>
    public const string MoteNeighborOverlap = "MOTE-NEIGHBOR-OVERLAP";

    /// <summary>读到的 job 观测为空，本窗有该 pawn 的 job 被缺省过滤掉。</summary>
    public const string JobsFilteredEmpty = "JOBS-FILTERED-EMPTY";

    /// <summary>测试代码直调 CALL1 表里禁直调的 vanilla 方法。</summary>
    public const string ForbiddenCallee = "FORBIDDEN-CALLEE";

    /// <summary>测试代码绕过生成根直接生成 pawn，或豁免声明陈旧、缺理由、无处可挂。</summary>
    /// <remarks>豁免声明即方法或构造函数上的 RimRunnerDirectGeneration。</remarks>
    public const string DirectGeneration = "DIRECT-GENERATION";

    /// <summary>IL 型扫描用例的扫描面里有方法无法读取 IL，这些方法里的违例无法检出。</summary>
    /// <remarks>ILS1 量产品探针程序集，随包的调用点守卫量各自的扫描面。</remarks>
    public const string IlScanUnread = "ILSCAN-UNREAD";

    /// <summary>环境体检的对照段未成立，主判据在本名单下无法判断。</summary>
    public const string EnvCheckInconclusive = "ENV-CHECK-INCONCLUSIVE";

    /// <summary>环境体检失败，本批没有用例用到这条体检查的设施。</summary>
    /// <remarks>有用例用到时体检本条判失败并点名这些用例。</remarks>
    public const string EnvCheckUnused = "ENV-CHECK-UNUSED";

    /// <summary>环境体检失败，mod 名单以 [rimrunner].known_failing 许可这条。</summary>
    public const string EnvCheckKnownFailing = "ENV-CHECK-KNOWN-FAILING";

    /// <summary>用例名册与本实例的运行时注册表没有逐字段核对。</summary>
    /// <remarks>批级，引擎批前核对出。实例装载的 MVID 与名册记录的不匹配，或进程未由 CLI 启动。</remarks>
    public const string RosterUnverified = "ROSTER-UNVERIFIED";

    /// <summary>用例名册里有重复的 Id，按首条读。</summary>
    /// <remarks>批级，CLI 开始执行前出。</remarks>
    public const string RosterDuplicateId = "ROSTER-DUPLICATE-ID";

    /// <summary>Mods 目录下有 mod 缺 About.xml 或无法读取 packageId。</summary>
    /// <remarks>批级，CLI 开始执行前出。</remarks>
    public const string ModsUnreadable = "MODS-UNREADABLE";

    /// <summary>基线档的档头里，mod 清单与 mod 名单不匹配。</summary>
    /// <remarks>批级，CLI 开始执行前出。</remarks>
    public const string BaselineHeader = "BASELINE-HEADER";

    /// <summary>启动快照拍下时 vanilla 日志队列已到上限。</summary>
    /// <remarks>批级。队列会合并连续重复消息，无法算出确切淘汰条数，只报已到上限；快照与启动错误拒批只看到之后的条目。</remarks>
    public const string StartupLogEvicted = "STARTUP-LOG-EVICTED";

    /// <summary>mod 名单上必需项以外的 packageId 未装进实例。</summary>
    /// <remarks>批级，CLI 开始执行前出。</remarks>
    public const string ModListNotLoaded = "MODLIST-NOT-LOADED";

    /// <summary>RimRunner 构建时的游戏与机器级配置 gameExe 所在的那份不匹配。</summary>
    /// <remarks>批级，CLI 开始执行前出。比的是版本串与引擎所依的游戏程序集的 sha256，正文列出已改变与缺的文件。</remarks>
    public const string PackageGameMismatch = "PACKAGE-GAME-MISMATCH";

    /// <summary>无法取得产品工作树的来源身份（commit、树名与改动表）。</summary>
    /// <remarks>批级，CLI 开始执行前出。仍发车，读数的封批行只记原因。</remarks>
    public const string SourceUnavailable = "SOURCE-UNAVAILABLE";
}

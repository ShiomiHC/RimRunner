using LudeonTK;

namespace RimRunner;

public sealed class Window_RimRunnerShowcase : EditWindow
{
    private const float RowHeight = 24f;
    private const float Gap = 4f;
    private const float ButtonWidth = 80f;
    private const float ScrollBarWidth = 16f;
    private const float Indent = 12f;
    private const float TreeIndent = 14f;
    private const float TreeRowGap = 2f;
    private const float TreeButtonPadding = 16f;
    private const float TimelineShare = 0.6f;
    private const float SectionLabelHeight = 20f;
    private const float ClearConfirmSeconds = 3f;

    private const float RightReserve = 180f;
    private const float TopReserve = 90f;
    private const float BottomReserve = 60f;
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.45f);
    private static readonly Color WarningColor = new(1f, 0.85f, 0.3f);
    private static readonly Color ReadColor = new(0.6f, 0.6f, 0.6f);
    private static readonly Color StepDoneFill = new(0.2f, 0.42f, 0.24f);
    private static readonly Color StepAbortFill = new(0.5f, 0.18f, 0.18f);
    private static readonly Color StepNextColor = new(0.55f, 0.75f, 1f);
    private static readonly Color StepCandidateFill = new(0.16f, 0.3f, 0.55f);
    private static readonly Color StepRunningFill = new(0.55f, 0.35f, 0.1f);
    private static readonly Color StepRunningColor = new(1f, 0.7f, 0.3f);
    private static readonly Color StepLastColor = new(1f, 0.85f, 0.3f);
    private static readonly Color TimelineLineColor = new(0.5f, 0.5f, 0.5f);
    private static readonly Color StepPathColor = new(0.45f, 0.85f, 0.5f);
    private static readonly Color StageIdleFill = new(0.25f, 0.25f, 0.25f);
    private static readonly Color SectionLabelColor = new(0.7f, 0.7f, 0.7f);
    private static readonly Color ClosedTint = new(1f, 1f, 1f, 0.45f);

    private string filter = string.Empty;
    private Vector2 listScroll;
    private Vector2 recordScroll;
    private Vector2 timelineScroll;
    private float clearArmedUntil = -1f;
    private string? candidateAnchor;
    private int candidateIndex;

    public override Vector2 InitialSize => new(480f, 760f);

    public Window_RimRunnerShowcase()
    {
        optionalTitle = "RimRunner 展台";
        layer = WindowLayer.SubSuper;
    }

    protected override void SetInitialSizeAndPosition()
    {
        Vector2 size = InitialSize;
        float height = Mathf.Min(size.y, UI.screenHeight - TopReserve - BottomReserve);
        windowRect = new Rect(Mathf.Max(0f, UI.screenWidth - size.x - RightReserve), TopReserve, size.x, height).Rounded();
    }

    internal static void OpenIfClosed()
    {
        if (Find.WindowStack.WindowOfType<Window_RimRunnerShowcase>() == null)
        {
            Find.WindowStack.Add(new Window_RimRunnerShowcase());
        }
    }

    internal static void Toggle()
    {
        Window_RimRunnerShowcase? open = Find.WindowStack.WindowOfType<Window_RimRunnerShowcase>();
        if (open != null)
        {
            open.Close();
        }
        else
        {
            Find.WindowStack.Add(new Window_RimRunnerShowcase());
        }
    }

    internal static void HandleHotkeys()
    {
        if (GUIUtility.keyboardControl != 0)
        {
            return;
        }

        Window_RimRunnerShowcase? window = Find.WindowStack.WindowOfType<Window_RimRunnerShowcase>();
        if (window == null)
        {
            return;
        }

        if (RimRunnerKeyBindingDefOf.RimRunner_ShowcaseNextStep.KeyDownEvent)
        {
            window.RunNext();
            Event.current.Use();
        }
        else if (RimRunnerKeyBindingDefOf.RimRunner_ShowcaseCycleNext.KeyDownEvent && window.CycleNext())
        {
            Event.current.Use();
        }
    }

    private (List<string> Candidates, int Index) NextState(RimRunnerScene current)
    {
        IReadOnlyList<(string Name, bool Ok)> trail = RimRunnerSceneHost.StepTrail;
        List<string> candidates = RimRunnerSceneStepTree.NextCandidates(current.Steps, trail);
        string anchor = current.Id + "\n" + trail.LastOrDefault(run => run.Ok).Name;
        if (anchor != candidateAnchor)
        {
            candidateAnchor = anchor;
            candidateIndex = 0;
        }

        candidateIndex = candidates.Count == 0 ? 0 : candidateIndex % candidates.Count;
        return (candidates, candidateIndex);
    }

    private void RunNext()
    {
        RimRunnerScene? current = RimRunnerShowcaseSession.CurrentShowcase;
        if (current == null)
        {
            Refuse(false, "无法执行步骤：当前没有已立场的场面");
            return;
        }

        (List<string> candidates, int index) = NextState(current);
        if (candidates.Count == 0)
        {
            Refuse(false, current.Steps.Count == 0 ? "本场没有步骤" : "已到叶步骤，没有下一步；在步骤树里另选一步可换一支");
            return;
        }

        Refuse(RimRunnerShowcaseSession.RequestStep(candidates[index], out string? refusal), refusal);
    }

    private bool CycleNext()
    {
        RimRunnerScene? current = RimRunnerShowcaseSession.CurrentShowcase;
        if (current == null || current.Steps.Count == 0 || RimRunnerShowcaseSession.CurrentPhase != RimRunnerShowcaseSession.Phase.Staged)
        {
            return false;
        }

        (List<string> candidates, int index) = NextState(current);
        if (candidates.Count > 1)
        {
            candidateIndex = (index + 1) % candidates.Count;
        }

        return true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Color colorOnEntry = GUI.color;
        try
        {
            Text.Font = GameFont.Small;
            Draw(inRect);
        }
        finally
        {
            GUI.color = colorOnEntry;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;
        }
    }

    private void Draw(Rect inRect)
    {
        float y = inRect.y;
        bool armed = RimRunnerRealtime.Seconds < clearArmedUntil;
        filter = Widgets.TextField(new Rect(inRect.x, y, inRect.width - ButtonWidth - Gap, RowHeight), filter);
        if (Widgets.ButtonText(new Rect(inRect.xMax - ButtonWidth, y, ButtonWidth, RowHeight), armed ? "确认清空？" : "清空已阅"))
        {
            if (armed)
            {
                RimRunnerShowcaseReadMarks.Clear();
                clearArmedUntil = -1f;
            }
            else
            {
                clearArmedUntil = RimRunnerRealtime.Seconds + ClearConfirmSeconds;
            }
        }

        y += RowHeight + Gap;

        if (RimRunnerSceneRegistry.Faults.Count > 0)
        {
            GUI.color = ErrorColor;
            foreach (string fault in RimRunnerSceneRegistry.Faults)
            {
                y = DrawWrapped(inRect, y, "名册错误：" + fault);
            }

            return;
        }

        float environmentTop = inRect.yMax - (Gap + SectionLabelHeight + RowHeight * 2f + Gap);
        float listHeight = Mathf.Floor((environmentTop - y) * 0.3f);
        DrawList(new Rect(inRect.x, y, inRect.width, listHeight));
        y += listHeight + Gap;
        y = DrawCurrent(inRect, y);
        y = DrawTimeline(inRect, y, (environmentTop - Gap - y) * TimelineShare);
        DrawRecord(new Rect(inRect.x, y, inRect.width, environmentTop - Gap - y));
        DrawEnvironment(inRect, environmentTop);
    }

    private void DrawList(Rect outRect)
    {
        List<RimRunnerScene> matched = RimRunnerSceneRegistry.Showcases.Where(Matches).ToList();
        List<RimRunnerScene> unread = matched
            .Where(showcase => !RimRunnerShowcaseReadMarks.IsRead(showcase.Id))
            .OrderBy(showcase => showcase.Id, StringComparer.Ordinal)
            .ToList();
        List<RimRunnerScene> read = matched
            .Where(showcase => RimRunnerShowcaseReadMarks.IsRead(showcase.Id))
            .OrderBy(showcase => showcase.Id, StringComparer.Ordinal)
            .ToList();
        float contentHeight = (unread.Count + (read.Count > 0 ? read.Count + 1 : 0)) * RowHeight;
        Rect view = new(0f, 0f, outRect.width - ScrollBarWidth, contentHeight);
        Widgets.BeginScrollView(outRect, ref listScroll, view);
        float y = 0f;
        foreach (RimRunnerScene showcase in unread)
        {
            DrawListRow(view.width, y, showcase, false);
            y += RowHeight;
        }

        if (read.Count > 0)
        {
            Color headerBefore = GUI.color;
            GUI.color = ReadColor;
            Widgets.Label(new Rect(0f, y, view.width, RowHeight), "已阅");
            GUI.color = headerBefore;
            y += RowHeight;
            foreach (RimRunnerScene showcase in read)
            {
                DrawListRow(view.width, y, showcase, true);
                y += RowHeight;
            }
        }

        Widgets.EndScrollView();
    }

    private static void DrawListRow(float width, float y, RimRunnerScene showcase, bool read)
    {
        Rect box = new(Indent, y + 1f, RowHeight - 4f, RowHeight - 4f);
        Widgets.DrawBox(box);
        if (read)
        {
            GUI.DrawTexture(box, Widgets.CheckboxOnTex);
        }

        TooltipHandler.TipRegion(box, read ? "取消已阅" : "标为已阅");
        if (Widgets.ButtonInvisible(box))
        {
            RimRunnerShowcaseReadMarks.Toggle(showcase.Id);
        }

        float buttonX = Indent + RowHeight + Gap;
        Rect button = new(buttonX, y, width - buttonX, RowHeight - 2f);
        string full = showcase.Id + " · " + showcase.Title;
        string label = full.Truncate(button.width - 8f);
        Color colorBefore = GUI.color;
        if (read)
        {
            GUI.color = ReadColor;
        }

        if (Widgets.ButtonText(button, label))
        {
            Refuse(RimRunnerShowcaseSession.RequestStage(showcase.Id, out string? stageRefusal), stageRefusal);
        }

        if (label != full)
        {
            TooltipHandler.TipRegion(button, full);
        }

        if (read)
        {
            float lineWidth = Mathf.Min(Text.CalcSize(label).x, button.width);
            Widgets.DrawLineHorizontal(button.center.x - lineWidth / 2f, button.center.y, lineWidth, ReadColor);
        }

        GUI.color = colorBefore;
    }

    private bool Matches(RimRunnerScene showcase)
    {
        return filter.Length == 0
               || showcase.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
               || showcase.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private float DrawCurrent(Rect inRect, float y)
    {
        RimRunnerScene? current = RimRunnerShowcaseSession.CurrentShowcase;
        string state = DescribePhase(RimRunnerShowcaseSession.CurrentPhase)
                       + (RimRunnerShowcaseSession.Reloading ? "（回载中）" : string.Empty)
                       + (RimRunnerSceneHost.WorldDirty ? " · 世界已脏，下一场先回载"
                           : RimRunnerSceneHost.SceneTouchedWorld ? " · 场内改动全局状态，换场时核对是否复原"
                           : " · 世界干净");
        y = DrawWrapped(inRect, y, current == null
            ? "当前无场 · " + state
            : "当前：" + current.Id + " · " + current.Title + " · " + state);

        float x = inRect.x;
        if (current != null)
        {
            if (Widgets.ButtonText(new Rect(x, y, ButtonWidth, RowHeight), "重立"))
            {
                Refuse(RimRunnerShowcaseSession.RequestStage(current.Id, out string? restageRefusal), restageRefusal);
            }

            x += ButtonWidth + Gap;
        }

        var resetRect = new Rect(x, y, ButtonWidth, RowHeight);
        TooltipHandler.TipRegion(resetRect, "拆场：执行清理栈并回载基线档。换场面不必先点重置世界：世界干净时，局部场面之间换场不回载");
        if (Widgets.ButtonText(resetRect, "重置世界"))
        {
            Refuse(RimRunnerShowcaseSession.RequestTeardown(out string? teardownRefusal), teardownRefusal);
        }

        x += ButtonWidth + Gap;
        int operationErrors = RimRunnerShowcaseSession.OperationErrorCount;
        Color colorBefore = GUI.color;
        if (operationErrors > 0)
        {
            GUI.color = ErrorColor;
        }

        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(new Rect(x, y, inRect.xMax - x, RowHeight), "操作段错误级日志 " + operationErrors + " 条");
        Text.Anchor = TextAnchor.UpperLeft;
        GUI.color = colorBefore;
        return y + RowHeight + Gap;
    }

    private void DrawEnvironment(Rect inRect, float y)
    {
        Widgets.DrawLineHorizontal(inRect.x, y, inRect.width, TimelineLineColor);
        y += Gap;
        Color labelBefore = GUI.color;
        GUI.color = SectionLabelColor;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inRect.x, y, inRect.width, SectionLabelHeight), "通用（跨立场保持）");
        Text.Font = GameFont.Small;
        GUI.color = labelBefore;
        y += SectionLabelHeight;
        var times = new[] { RimRunnerSceneTime.Morning, RimRunnerSceneTime.Dusk, RimRunnerSceneTime.Midnight };
        float width = (inRect.width - Gap * times.Length) / (times.Length + 1);
        float x = inRect.x;
        foreach (RimRunnerSceneTime time in times)
        {
            if (Widgets.ButtonText(new Rect(x, y, width, RowHeight), "跳到" + RimRunnerSceneEnvironment.TimeLabel(time)))
            {
                string? refusal = EnvironmentRefusal()
                                 ?? RimRunnerSceneEnvironment.JumpTo(RimRunnerCurrentMapProbe.EngineCurrentMap()!, time);
                Refuse(refusal == null, refusal);
            }

            x += width + Gap;
        }

        if (Widgets.ButtonText(new Rect(x, y, width, RowHeight),
                RimRunnerSceneEnvironment.LightLocked ? "解锁光照" : "锁光照"))
        {
            string? refusal = EnvironmentRefusal();
            if (refusal == null)
            {
                RimRunnerSceneEnvironment.ToggleLightLock();
            }

            Refuse(refusal == null, refusal);
        }

        y += RowHeight + Gap;
        Map? map = RimRunnerCurrentMapProbe.EngineCurrentMap();
        WeatherDef? locked = RimRunnerSceneEnvironment.LockedWeather;
        string weatherLabel = map == null
            ? "天气：—"
            : "天气：" + map.weatherManager.curWeather.LabelCap + (locked != null ? "（已锁）" : "（点此更换并锁定）");
        float weatherWidth = inRect.width - ButtonWidth - Gap;
        if (Widgets.ButtonText(new Rect(inRect.x, y, weatherWidth, RowHeight), weatherLabel))
        {
            string? refusal = EnvironmentRefusal();
            if (refusal == null)
            {
                Find.WindowStack.Add(new FloatMenu(DefDatabase<WeatherDef>.AllDefsListForReading
                    .OrderBy(def => def.label, StringComparer.Ordinal)
                    .Select(def => new FloatMenuOption(def.LabelCap + "（" + def.defName + "）",
                        () => RimRunnerSceneEnvironment.LockWeather(RimRunnerCurrentMapProbe.EngineCurrentMap()!, def)))
                    .ToList()));
            }

            Refuse(refusal == null, refusal);
        }

        if (locked != null && Widgets.ButtonText(new Rect(inRect.xMax - ButtonWidth, y, ButtonWidth, RowHeight), "解锁天气"))
        {
            RimRunnerSceneEnvironment.UnlockWeather();
        }
    }

    private static string? EnvironmentRefusal()
    {
        return RimRunnerCurrentMapProbe.EngineCurrentMap() == null
            ? "没有当前地图"
            : RimRunnerShowcaseSession.StepBusyRefusal();
    }

    private float DrawTimeline(Rect inRect, float y, float maxHeight)
    {
        RimRunnerScene? current = RimRunnerShowcaseSession.CurrentShowcase;
        if (current == null)
        {
            return y;
        }

        IReadOnlyList<RimRunnerSceneStep> steps = current.Steps;
        int count = steps.Count;
        if (count == 0)
        {
            return y;
        }

        IReadOnlyList<(string Name, bool Ok)> trail = RimRunnerSceneHost.StepTrail;
        var lastRun = new Dictionary<string, (int Order, bool Ok)>(StringComparer.Ordinal);
        for (int i = 0; i < trail.Count; i++)
        {
            lastRun[trail[i].Name] = (i + 1, trail[i].Ok);
        }

        string? lastName = trail.Count > 0 ? trail[trail.Count - 1].Name : null;
        RimRunnerShowcaseSession.Phase phase = RimRunnerShowcaseSession.CurrentPhase;
        bool accepting = phase == RimRunnerShowcaseSession.Phase.Staged;
        bool open = accepting || phase == RimRunnerShowcaseSession.Phase.Stepping;

        (List<string> candidates, int candidateAt) = NextState(current);
        string? candidate = candidates.Count > 0 ? candidates[candidateAt] : null;
        string nextLabel = "下一步（" + RimRunnerKeyBindingDefOf.RimRunner_ShowcaseNextStep.MainKeyLabel + "）";
        float nextWidth = Text.CalcSize(nextLabel).x + TreeButtonPadding;
        if (Widgets.ButtonText(new Rect(inRect.x, y, nextWidth, RowHeight), nextLabel,
                active: accepting && candidate != null) && accepting)
        {
            RunNext();
        }

        string candidateText = candidate == null
            ? "已到叶步骤，在步骤树里另选一步可换一支"
            : "候选：" + candidate + (candidates.Count > 1
                ? "（" + (candidateAt + 1) + "/" + candidates.Count + "，"
                  + RimRunnerKeyBindingDefOf.RimRunner_ShowcaseCycleNext.MainKeyLabel + " 另选一支）"
                : string.Empty);
        Text.Anchor = TextAnchor.MiddleLeft;
        Widgets.Label(new Rect(inRect.x + nextWidth + Gap, y, inRect.width - nextWidth - Gap, RowHeight), candidateText);
        Text.Anchor = TextAnchor.UpperLeft;
        y += RowHeight + Gap;
        maxHeight -= RowHeight + Gap;

        int stage = count;
        var predecessors = new int[count];
        var children = new List<int>[count + 1];
        for (int i = 0; i <= count; i++)
        {
            children[i] = [];
        }

        for (int i = 0; i < count; i++)
        {
            predecessors[i] = RimRunnerSceneStepTree.PredecessorIndex(steps, i);
            children[predecessors[i] < 0 ? stage : predecessors[i]].Add(i);
        }

        var order = new List<int>(count + 1);
        var pending = new Stack<int>();
        pending.Push(stage);
        while (pending.Count > 0)
        {
            int node = pending.Pop();
            order.Add(node);
            for (int c = children[node].Count - 1; c >= 0; c--)
            {
                pending.Push(children[node][c]);
            }
        }

        string[] labels = new string[count + 1];
        labels[stage] = "立场";
        for (int i = 0; i < count; i++)
        {
            labels[i] = (lastRun.TryGetValue(steps[i].Name, out (int Order, bool Ok) run) ? run.Order + ". " : string.Empty) + steps[i].Name;
        }

        float rowStride = RowHeight + TreeRowGap;
        float contentHeight = order.Count * rowStride;
        float viewportHeight = Mathf.Min(contentHeight, Mathf.Max(rowStride, maxHeight));
        bool scrolls = contentHeight > viewportHeight;
        float viewWidth = inRect.width - (scrolls ? ScrollBarWidth : 0f);
        int[] depths = RimRunnerSceneStepTree.Depths(steps);
        var rects = new Rect[count + 1];
        for (int row = 0; row < order.Count; row++)
        {
            int node = order[row];
            float x = node == stage ? 0f : (depths[node] + 1) * TreeIndent;
            float width = Mathf.Min(Text.CalcSize(labels[node]).x + TreeButtonPadding, viewWidth - x);
            rects[node] = new Rect(x, row * rowStride, width, RowHeight);
        }

        Rect outRect = new(inRect.x, y, inRect.width, viewportHeight);
        Widgets.BeginScrollView(outRect, ref timelineScroll, new Rect(0f, 0f, viewWidth, contentHeight));
        bool RanOk(int node) => lastRun.TryGetValue(steps[node].Name, out (int Order, bool Ok) run) && run.Ok;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int node = 0; node <= count; node++)
            {
                if (children[node].Count == 0)
                {
                    continue;
                }

                Rect parent = rects[node];
                float lineX = parent.x + TreeIndent / 2f;
                foreach (int child in children[node])
                {
                    bool lit = RanOk(child);
                    if (pass == 0 || lit)
                    {
                        Color color = pass == 0 ? TimelineLineColor : StepPathColor;
                        float width = pass == 0 ? 1f : 2f;
                        float childY = rects[child].center.y;
                        Widgets.DrawLine(new Vector2(lineX, parent.yMax), new Vector2(lineX, childY), color, width);
                        Widgets.DrawLine(new Vector2(lineX, childY), new Vector2(rects[child].x, childY), color, width);
                    }
                }
            }
        }

        Color colorBefore = GUI.color;
        Rect stageRect = rects[stage];
        Widgets.DrawBoxSolid(stageRect, open ? StepDoneFill : phase == RimRunnerShowcaseSession.Phase.Aborted ? StepAbortFill : StageIdleFill);
        Text.Anchor = TextAnchor.MiddleCenter;
        Widgets.Label(stageRect, labels[stage]);
        Text.Anchor = TextAnchor.UpperLeft;
        for (int i = 0; i < count; i++)
        {
            RimRunnerSceneStep step = steps[i];
            Rect rect = rects[i];
            bool ran = lastRun.TryGetValue(step.Name, out (int Order, bool Ok) run);
            bool next = !ran && (predecessors[i] < 0 || RanOk(predecessors[i]));
            bool isCandidate = step.Name == candidate && accepting;
            bool running = phase == RimRunnerShowcaseSession.Phase.Stepping
                           && step.Name == RimRunnerShowcaseSession.SteppingName;
            GUI.color = open ? Color.white : ClosedTint;
            if (running)
            {
                Widgets.DrawBoxSolid(rect, StepRunningFill);
            }
            else if (ran)
            {
                Widgets.DrawBoxSolid(rect, run.Ok ? StepDoneFill : StepAbortFill);
            }
            else if (isCandidate)
            {
                Widgets.DrawBoxSolid(rect, StepCandidateFill);
            }

            if (Widgets.ButtonText(rect, labels[i].Truncate(rect.width - 8f),
                    drawBackground: !running && !ran && !isCandidate, active: accepting,
                    overrideTextAnchor: TextAnchor.MiddleCenter) && accepting)
            {
                Refuse(RimRunnerShowcaseSession.RequestStep(step.Name, out string? stepRefusal), stepRefusal);
            }

            if (running)
            {
                GUI.color = StepRunningColor;
                Widgets.DrawBox(rect, 2);
            }
            else if (step.Name == lastName)
            {
                GUI.color = StepLastColor;
                Widgets.DrawBox(rect, 2);
            }
            else if (isCandidate)
            {
                GUI.color = Color.white;
                Widgets.DrawBox(rect, 2);
            }
            else if (next && accepting)
            {
                GUI.color = StepNextColor;
                Widgets.DrawBox(rect);
            }

            TooltipHandler.TipRegion(rect, step.Name
                                           + (running ? "（进行中）"
                                               : ran ? (run.Ok ? "（已执行，第 " + run.Order + " 步）" : "（中止）")
                                               : string.Empty));
        }

        GUI.color = colorBefore;
        Widgets.EndScrollView();
        return y + viewportHeight + Gap * 2f;
    }

    private void DrawRecord(Rect outRect)
    {
        var lines = new List<(string Text, Color Color)>();
        if (RimRunnerDrive.Count is var drives and > 0)
        {
            lines.Add(("本进程已被 live 驱动 " + drives + " 次，驱动改过的进程状态不还原", WarningColor));
        }

        if (RimRunnerSceneHost.AbortLine != null)
        {
            lines.Add(("中止：" + RimRunnerSceneHost.AbortLine, ErrorColor));
        }

        lines.AddRange(RimRunnerSceneHost.StagingErrors.Select(line => ("装配段错误级日志：" + line, ErrorColor)));
        lines.AddRange(RimRunnerSceneHost.StepAborts.Select(line => (line, ErrorColor)));
        lines.AddRange(RimRunnerShowcaseSession.StartupOwnErrors.Select(line => ("本产品侧启动错误级日志：" + line, ErrorColor)));
        lines.AddRange(RimRunnerShowcaseSession.StartupForeignErrors.Select(line => ("他 mod 启动错误级日志：" + line, WarningColor)));
        lines.AddRange(RimRunnerShowcaseSession.StartupUnattributedErrors.Select(line => ("来源未定的启动错误级日志：" + line, WarningColor)));
        lines.AddRange(RimRunnerShowcaseSession.ColdLoadErrors.Concat(RimRunnerShowcaseSession.ReloadErrors)
            .Select(line => ("载档错误级日志：" + line, WarningColor)));
        foreach (RimRunnerAssertEntry entry in RimRunnerSceneHost.Warnings)
        {
            lines.Add((RimRunnerLiveContract.SceneFinding.Tag(entry.sceneKind) + "：" + entry.label + "：" + entry.actual,
                WarningColor));
            lines.AddRange(entry.offending.Select(item => ("    " + item, WarningColor)));
        }

        lines.AddRange(RimRunnerSceneHost.Messages.Select(line => (line, Color.white)));

        float width = outRect.width - ScrollBarWidth;
        float contentHeight = lines.Sum(line => Text.CalcHeight(line.Text, width) + 2f);
        Widgets.BeginScrollView(outRect, ref recordScroll, new Rect(0f, 0f, width, contentHeight));
        Color colorBefore = GUI.color;
        float y = 0f;
        foreach ((string text, Color color) in lines)
        {
            float height = Text.CalcHeight(text, width);
            GUI.color = color;
            Widgets.Label(new Rect(0f, y, width, height), text);
            y += height + 2f;
        }

        GUI.color = colorBefore;
        Widgets.EndScrollView();
    }

    private static float DrawWrapped(Rect inRect, float y, string text)
    {
        float height = Text.CalcHeight(text, inRect.width);
        Widgets.Label(new Rect(inRect.x, y, inRect.width, height), text);
        return y + height + Gap;
    }

    private static string DescribePhase(RimRunnerShowcaseSession.Phase phase)
    {
        string label = PhaseLabel(phase);
        string? name = RimRunnerShowcaseSession.SteppingName;
        if (phase != RimRunnerShowcaseSession.Phase.Stepping || name == null)
        {
            return label;
        }

        int ticks = RimRunnerShowcaseSession.SteppingTicks;
        int within = RimRunnerShowcaseSession.SteppingWithinTicks;
        return within > 0
            ? label + "：" + name + "（已推进 " + ticks + " / 时限 " + within + " tick）"
            : label + "：" + name + "（已推进 " + ticks + " tick / 观察剩余 "
              + RimRunnerShowcaseSession.SteppingDwellRemaining + " tick）";
    }

    private static string PhaseLabel(RimRunnerShowcaseSession.Phase phase)
    {
        return phase switch
        {
            RimRunnerShowcaseSession.Phase.None => "未立场",
            RimRunnerShowcaseSession.Phase.Staging => "立场中",
            RimRunnerShowcaseSession.Phase.TearingDown => "拆场中",
            RimRunnerShowcaseSession.Phase.Staged => "已立场",
            RimRunnerShowcaseSession.Phase.Stepping => "进行中",
            RimRunnerShowcaseSession.Phase.Aborted => "装配中止",
            _ => throw new InvalidOperationException("展台阶段出现未处理的取值：" + phase),
        };
    }

    private static void Refuse(bool accepted, string? refusal)
    {
        if (!accepted)
        {
            Messages.Message(refusal, MessageTypeDefOf.RejectInput, false);
        }
    }
}

[DefOf]
internal static class RimRunnerKeyBindingDefOf
{
    public static KeyBindingDef RimRunner_ShowcaseNextStep = null!;
    public static KeyBindingDef RimRunner_ShowcaseCycleNext = null!;

    static RimRunnerKeyBindingDefOf()
    {
        DefOfHelper.EnsureInitializedInCtor(typeof(RimRunnerKeyBindingDefOf));
    }
}

public static class RimRunnerShowcaseDebugActions
{
    [DebugAction("RimRunner", "展台面板", allowedGameStates = AllowedGameStates.Playing)]
    private static void OpenShowcasePanel()
    {
        if (!RimRunnerProcessMode.Showcase)
        {
            Messages.Message("本实例不是展台实例：展台面板只在 rimrunner showcase 启动的实例里可用",
                MessageTypeDefOf.RejectInput, false);
            return;
        }

        Window_RimRunnerShowcase.OpenIfClosed();
    }
}

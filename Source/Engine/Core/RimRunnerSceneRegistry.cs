using System.IO;

namespace RimRunner;

internal enum RimRunnerSceneFaultKind
{
    EmptySceneId,
    DuplicateSceneId,
    ReservedSceneId,
    NoRole,
    BadIsolatedSize,
    DuplicateStepName,
    BadInspectTab,
    BadDefaultWithinTicks,
    BadPredecessor,
    NoActionOrUntil,
    NegativeDwellTicks,
    NegativeWithinTicks,
    StrayWithinTicks,
    NegativeFilmSpeed,
    NullShot,
    BadShotName,
    DuplicateShotName,
    SetupNotStep,
    ThroughNotStep,
    SetupOffPath,
    NoOutput,
    PartialOutputSize,
    NonPositiveOutputSize,
    OutputBelowFloor,
    BadClipFormats,
    OddMp4Size,
    BadMaxBytes,
    EmptyCameraKey,
    BadCameraSize,
    BadFpsOrSpeed,
    NegativeLeadTail,
    TooFewFrames,
    NonPositivePanSeconds,
    BadMusic,
    ShotFpsNotWhole,
    StepFpsNotWhole,
    StepZeroSpeed,
}

internal sealed record RimRunnerSceneFault(RimRunnerSceneFaultKind Kind, string Owner, string Text);

internal static class RimRunnerSceneRegistry
{
    internal static readonly string[] ReservedIds = ["step", "teardown", "stop", "list"];

    internal static readonly RimRunnerScene[] All;
    internal static readonly RimRunnerScene[] Showcases;
    internal static readonly RimRunnerScene[] Films;
    internal static readonly List<string> Faults;

    internal static readonly List<string> Warnings;

    internal static readonly IReadOnlyDictionary<string, string> AbsentTypes;

    static RimRunnerSceneRegistry()
    {
        var faults = new List<string>();
        Type[] candidates = RimRunnerDefScope.GaugeTypes()
            .Where(type => !type.IsAbstract
                           && typeof(RimRunnerScene).IsAssignableFrom(type)
                           && !IsReplica(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        RimRunnerPackagePresence.Judgement packages = RimRunnerPackages.Judge(candidates);
        AbsentTypes = packages.Absent;
        var warnings = new List<string>();
        foreach (Type type in candidates)
        {
            warnings.AddRange(RimRunnerPackages.DeclarationFaults(type)
                .Select(text => RimRunnerWarningKey.InertDeclaration + "：" + text));
        }

        faults.AddRange(packages.Faults);
        All = Discover(candidates.Where(type => packages.Admits(type.FullName!)), faults);
        Showcases = WithRole<IRimRunnerShowcase>(All);
        Films = WithRole<IRimRunnerFilm>(All);
        faults.AddRange(GateFaults(All).Select(fault => fault.Text));
        warnings.AddRange(SiteWarnings(All));
        Faults = faults;
        Warnings = warnings;
        if (RimRunnerProcessMode.Stages)
        {
            foreach (string fault in Faults)
            {
                RimRunnerLog.Category.Error(RimRunnerLog.Prefix + " 场面名册：" + fault);
            }

            foreach (string warning in Warnings)
            {
                RimRunnerEngineLog.Warn("场面名册：" + warning);
            }
        }
    }

    internal static RimRunnerScene? Find(string id)
    {
        return All.FirstOrDefault(showcase => string.Equals(showcase.Id, id, StringComparison.Ordinal));
    }

    internal static RimRunnerScene? FindShowcase(string id)
    {
        return Showcases.FirstOrDefault(showcase => string.Equals(showcase.Id, id, StringComparison.Ordinal));
    }

    internal static RimRunnerScene[] WithRole<TRole>(IEnumerable<RimRunnerScene> scenes)
    {
        return scenes.Where(scene => scene is TRole).ToArray();
    }

    internal static string UnknownIdRefusal(string id)
    {
        return "未知场面 Id：" + id + RimRunnerPackages.AbsentNote(AbsentTypes, "场面类");
    }

    internal static string NotShowcaseRefusal(string id)
    {
        return "场面 " + id + " 没有实现 IRimRunnerShowcase，不进展台面板";
    }

    internal static bool IsReplica(Type type)
    {
        return type.DeclaringType != null && typeof(RimRunnerTestCase).IsAssignableFrom(type.DeclaringType);
    }

    private static RimRunnerScene[] Discover(IEnumerable<Type> types, List<string> faults)
    {
        var found = new List<RimRunnerScene>();
        foreach (Type type in types)
        {
            try
            {
                found.Add((RimRunnerScene)Activator.CreateInstance(type)!);
            }
            catch (Exception exception)
            {
                faults.Add(type.Name + " 构造失败：" + exception.GetType().Name + " " + exception.Message);
            }
        }

        return found.ToArray();
    }

    internal static List<string> SiteWarnings(IEnumerable<RimRunnerScene> showcases)
    {
        return showcases
            .Where(showcase => showcase.MapSite is { Kind: RimRunnerMapSite.Choice.Isolated, Ground: RimRunnerMapGround.Concrete } site
                               && site.Size == RimRunnerIsolatedMap.SizeOf(RimRunnerMapTier.Default))
            .Select(showcase => RimRunnerWarningKey.InertDeclaration + "：场面 " + (showcase.Id ?? showcase.GetType().Name)
                                + " 声明 MapSite = Isolated()（缺省档、混凝土）：场面缺省即如此")
            .ToList();
    }

    internal static List<RimRunnerSceneFault> GateFaults(IEnumerable<RimRunnerScene> showcases)
    {
        var faults = new List<RimRunnerSceneFault>();
        List<RimRunnerScene> list = showcases.ToList();
        foreach (IGrouping<string, RimRunnerScene> group in list
                     .GroupBy(showcase => showcase.Id ?? string.Empty, StringComparer.Ordinal))
        {
            string owners = string.Join("、", group.Select(showcase => showcase.GetType().Name));
            if (string.IsNullOrWhiteSpace(group.Key))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.EmptySceneId, group.Key,
                    "场面 Id 为空：" + owners));
                continue;
            }

            if (group.Count() > 1)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.DuplicateSceneId, group.Key,
                    "场面 Id 重复：" + group.Key + "（" + owners + "）"));
            }

            if (ReservedIds.Contains(group.Key))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.ReservedSceneId, group.Key,
                    "场面 Id 与 showcase 子动作同名：" + group.Key + "（" + owners + "）"));
            }
        }

        foreach (RimRunnerScene showcase in list)
        {
            string id = showcase.Id ?? showcase.GetType().Name;
            if (showcase is not IRimRunnerShowcase && showcase is not IRimRunnerFilm)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NoRole, id,
                    "场面 " + id + " 没有实现 IRimRunnerShowcase 或 IRimRunnerFilm，没有入口能立场"));
            }

            if (showcase.MapSite.Kind == RimRunnerMapSite.Choice.Isolated
                && RimRunnerIsolatedMap.SizeFault(showcase.MapSite.Size) is { } sizeFault)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadIsolatedSize, id,
                    "场面 " + id + " 声明 MapSite = Isolated：" + sizeFault));
            }

            foreach (IGrouping<string, RimRunnerSceneStep> step in showcase.Steps
                         .GroupBy(step => step.Name, StringComparer.Ordinal)
                         .Where(group => group.Count() > 1))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.DuplicateStepName, id,
                    "场面 " + id + " 步骤名重复：" + step.Key));
            }

            if (showcase.InspectTab != null && !typeof(InspectTabBase).IsAssignableFrom(showcase.InspectTab))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadInspectTab, id,
                    "场面 " + id + " 的 InspectTab 不是检视标签页类型：" + showcase.InspectTab.FullName));
            }

            if (showcase.DefaultWithinTicks <= 0)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadDefaultWithinTicks, id,
                    "场面 " + id + " 的 DefaultWithinTicks 须为正：" + showcase.DefaultWithinTicks));
            }

            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (RimRunnerSceneStep step in showcase.Steps)
            {
                if (step.After != null && step.After != RimRunnerSceneStep.StageRoot && !declared.Contains(step.After))
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadPredecessor, id,
                        "场面 " + id + " 步骤「" + step.Name + "」的前驱「" + step.After + "」不是排在它之前的步骤"));
                }

                if (step.Action == null && step.Until == null)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NoActionOrUntil, id,
                        "场面 " + id + " 步骤「" + step.Name + "」既没有动作也没有完成条件"));
                }

                if (step.DwellTicks < 0)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NegativeDwellTicks, id,
                        "场面 " + id + " 步骤「" + step.Name + "」的 dwellTicks 不得为负：" + step.DwellTicks));
                }

                if (step.WithinTicks < 0)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NegativeWithinTicks, id,
                        "场面 " + id + " 步骤「" + step.Name + "」的 withinTicks 不得为负：" + step.WithinTicks));
                }

                if (step.Until == null && step.WithinTicks != 0)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.StrayWithinTicks, id,
                        "场面 " + id + " 步骤「" + step.Name + "」没有完成条件，withinTicks 须为 0：" + step.WithinTicks));
                }

                if (step.FilmSpeed < 0)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NegativeFilmSpeed, id,
                        "场面 " + id + " 步骤「" + step.Name + "」的 filmSpeed 不得为负：" + step.FilmSpeed));
                }

                declared.Add(step.Name);
            }

            faults.AddRange(ShotFaults(id, showcase.Steps,
                showcase is IRimRunnerFilm film ? film.Shots ?? [] : []));
        }

        return faults;
    }

    internal static List<RimRunnerSceneFault> ShotFaults(string id, IReadOnlyList<RimRunnerSceneStep> steps,
        IReadOnlyList<RimRunnerShot> shots)
    {
        var faults = new List<RimRunnerSceneFault>();
        var stepNames = new HashSet<string>(steps.Select(step => step.Name), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RimRunnerShot shot in shots)
        {
            if (shot == null)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NullShot, id,
                    "场面 " + id + " 的镜头表里有 null"));
                continue;
            }

            string where = "场面 " + id + " 镜头「" + shot.Name + "」";
            if (string.IsNullOrWhiteSpace(shot.Name) || shot.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadShotName, shot.Name,
                    "场面 " + id + " 的镜头名为空或含文件名不允许的字符：「" + shot.Name + "」"));
            }
            else if (!seen.Add(shot.Name))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.DuplicateShotName, shot.Name,
                    where + "重名"));
            }

            if (shot.Setup != null && !stepNames.Contains(shot.Setup))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.SetupNotStep, shot.Name,
                    where + "的 Setup 不是本场面的步骤：" + shot.Setup));
            }

            if (shot.Through != null && !stepNames.Contains(shot.Through))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.ThroughNotStep, shot.Name,
                    where + "的 Through 不是本场面的步骤：" + shot.Through));
            }

            List<string>? path = null;
            if (shot.Through != null && stepNames.Contains(shot.Through))
            {
                try
                {
                    path = RimRunnerSceneStepTree.PathTo(steps, shot.Through);
                }
                catch (InvalidOperationException)
                {
                    path = null;
                }
            }
            if (shot.Setup != null && stepNames.Contains(shot.Setup) && path != null && !path.Contains(shot.Setup))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.SetupOffPath, shot.Name,
                    where + "的 Setup「" + shot.Setup + "」不在 Through「" + shot.Through + "」的路径上"));
            }

            RimRunnerShotOutput output = shot.Output;
            if (output == null)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NoOutput, shot.Name,
                    where + "没有 Output"));
            }
            else
            {
                if (output.Partial)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.PartialOutputSize, shot.Name,
                        where + "的输出尺寸宽高只指定一个：宽高须同时指定，或同时留空（取 windowSize）"));
                }
                else if (output.Declared && (output.Width <= 0 || output.Height <= 0))
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NonPositiveOutputSize, shot.Name,
                        where + "的输出尺寸须为正：" + output.Width + "×" + output.Height));
                }
                else if (output.Declared && output.Width < 1024 && output.Height < 768)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.OutputBelowFloor, shot.Name,
                        where + "的输出尺寸宽 < 1024 与高 < 768 不得同时成立（收到 " + output.Width + "×" + output.Height
                        + "）：原版此时重置 UI 缩放并记错误级日志"));
                }

                const RimRunnerClipFormat known = RimRunnerClipFormat.Gif | RimRunnerClipFormat.Mp4;
                if (shot.IsClip && (shot.ClipFormats == 0 || (shot.ClipFormats & ~known) != 0))
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadClipFormats, shot.Name,
                        where + "的格式须为 Gif 与 Mp4 的组合：" + shot.ClipFormats));
                }

                if (output.Declared && (shot.ClipFormats & RimRunnerClipFormat.Mp4) != 0 && (output.Width % 2 != 0 || output.Height % 2 != 0))
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.OddMp4Size, shot.Name,
                        where + "出 Mp4，宽高须为偶数（yuv420p）：" + output.Width + "×" + output.Height));
                }

                if (output.MaxBytes is <= 0)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadMaxBytes, shot.Name,
                        where + "的 MaxBytes 须为正"));
                }
            }

            RimRunnerClipCamera camera = shot.Camera;
            foreach (RimRunnerShotCamera point in new[] { camera.Start, camera.End }.OfType<RimRunnerShotCamera>())
            {
                if (point.Kind == RimRunnerShotCamera.TargetKind.Key && string.IsNullOrWhiteSpace(point.Key))
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.EmptyCameraKey, shot.Name,
                        where + "的机位登记键为空"));
                }

                if (point.Size < 0f || (point.Kind == RimRunnerShotCamera.TargetKind.Cell && point.Size <= 0f))
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadCameraSize, shot.Name,
                        where + "的机位视距须为正、留白不得为负：" + point.Size));
                }
            }

            if (!shot.IsClip)
            {
                continue;
            }

            if (shot.Fps <= 0 || shot.Speed < 0)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadFpsOrSpeed, shot.Name,
                    where + "的 fps 须为正、speed 不得为负：fps=" + shot.Fps + " speed=" + shot.Speed));
                continue;
            }

            if (shot.LeadSeconds < 0f || shot.TailSeconds < 0f)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NegativeLeadTail, shot.Name,
                    where + "的 leadSeconds 与 tailSeconds 不得为负：lead=" + shot.LeadSeconds
                    + " tail=" + shot.TailSeconds));
            }

            if (shot.Through == null && (shot.LeadSeconds + shot.TailSeconds) * shot.Fps < 2f)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.TooFewFrames, shot.Name,
                    where + "不足两帧：（leadSeconds " + shot.LeadSeconds + " + tailSeconds " + shot.TailSeconds
                    + "）× fps " + shot.Fps));
            }

            if (shot.Camera is RimRunnerShotPan pan && pan.Seconds <= 0f)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.NonPositivePanSeconds, shot.Name,
                    where + "的摇镜时长须为正：" + pan.Seconds));
            }

            if (shot.Music != null && (string.IsNullOrWhiteSpace(shot.Music) || (shot.ClipFormats & RimRunnerClipFormat.Mp4) == 0))
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.BadMusic, shot.Name,
                    where + "的 music 须是非空的 SongDef defName，且格式带 Mp4（只有 Mp4 有音轨）：「" + shot.Music + "」"));
            }

            if (shot.Speed * GenTicks.TicksPerRealSecond % shot.Fps != 0)
            {
                faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.ShotFpsNotWhole, shot.Name,
                    where + "每帧推进的 tick 数不是整数：speed " + shot.Speed + " × "
                    + GenTicks.TicksPerRealSecond + " / fps " + shot.Fps));
            }

            if (path == null)
            {
                continue;
            }

            int from = 0;
            if (shot.Setup != null)
            {
                int setupIndex = path.IndexOf(shot.Setup);
                if (setupIndex >= 0)
                {
                    from = setupIndex + 1;
                }
            }

            for (int i = from; i < path.Count; i++)
            {
                RimRunnerSceneStep step = steps.First(item => string.Equals(item.Name, path[i], StringComparison.Ordinal));
                int speed = shot.SpeedForStep(step);
                if (step.FilmSpeed > 0 && speed != shot.Speed && speed * GenTicks.TicksPerRealSecond % shot.Fps != 0)
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.StepFpsNotWhole, shot.Name,
                        where + "步骤「" + step.Name + "」每帧推进的 tick 数不是整数：倍速 " + speed + " × "
                        + GenTicks.TicksPerRealSecond + " / fps " + shot.Fps));
                }

                if (speed == 0 && (step.Until != null || step.DwellTicks > 0))
                {
                    faults.Add(new RimRunnerSceneFault(RimRunnerSceneFaultKind.StepZeroSpeed, shot.Name,
                        where + "步骤「" + step.Name + "」的有效倍速为 0，不能带完成条件或 dwellTicks"));
                }
            }
        }

        return faults;
    }
}

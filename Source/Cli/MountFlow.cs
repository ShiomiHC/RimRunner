using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RimRunnerCli;

public sealed class MountFlow(
    OrchestratorDescriptor descriptor,
    string productRoot,
    ClaimStore store,
    string saveDataRoot,
    string? localAppDataOverride = null)
{
    private readonly OrchestratorDescriptor descriptor = descriptor;
    private readonly string productRoot = productRoot;
    private readonly ClaimStore store = store;
    private readonly string saveDataRoot = saveDataRoot;
    private readonly string? localAppDataOverride = localAppDataOverride;

    public ClaimRecord Mount(Action<string>? log,
        string purpose = ClaimRecord.PurposeBatch)
    {
        string modsFolder = descriptor.ModsFolder;
        if (!Directory.Exists(modsFolder))
        {
            throw new StoreException($"游戏 Mods 目录不存在：{modsFolder}");
        }

        PurposePolicy policy = PurposePolicies.ForWire(purpose);
        ThrowIfProductRootClaimed(policy);

        foreach (OrchestratorDescriptor.UnitDescriptor unit in descriptor.Units)
        {
            string aboutPath = Path.Combine(productRoot,
                unit.SourceRel.Replace('/', Path.DirectorySeparatorChar), "About", "About.xml");
            if (!File.Exists(aboutPath))
            {
                throw new StoreException($"不是可挂载的 {descriptor.Product} 产品根，缺少：{aboutPath}");
            }
        }

        bool firstOfTree = store.FindByProductRoot(productRoot).Count == 0;
        int preflight = policy.SkipsMountPreflight || !firstOfTree ? 0 : PreflightGate.Run(descriptor.MountPreflightCommands, productRoot, log);
        if (preflight > 0)
        {
            CliLog.Info($"[mount] 挂载前置闸通过：{preflight} 条");
        }

        using (store.RegisterLock())
        {
            ThrowIfProductRootClaimed(policy);

            int number = store.NextFreeNumber();
            CliLog.Info($"[mount] 批出实例号 {number}");

            ClaimRecord record = RecordBuilder.Build(
                descriptor, productRoot, number,
                ClaimRecord.StatePreparing, saveDataRoot, purpose);
            record.MainProductRoot = MainWorktree.Of(productRoot);
            store.AssignNames(record);

            if (record.SaveData.Contains('='))
            {
                throw new StoreException($"savedata 路径含 '='，无法作为游戏命令行参数：{record.SaveData}");
            }

            if (!store.TryClaim(record))
            {
                throw new StoreException($"实例号 {number} 在分配之后被另一条登记占用");
            }

            CliLog.Info($"[mount] 登记已写入：{Path.Combine(store.Root, number + ".json")}");

            try
            {
                if (record.OnWorktree)
                    WorktreeOutputLink.Ensure(descriptor, productRoot, log);

                string? modList = descriptor.ResolveModList(null);
                ModListDefinition? definition = modList == null
                    ? null
                    : ModListDefinition.Load(
                        Path.Combine(productRoot, descriptor.ModListsDir), modList);
                using (store.SweepLock())
                    new SaveDataInitializer(descriptor, productRoot, localAppDataOverride)
                        .Initialize(record, null, definition?.ModIds ?? [],
                            descriptor.BaselineSaveNameFor(definition?.SaveOwner ?? modList),
                            descriptor.ResolveSavesDir(productRoot), saveDataRoot,
                            SaveDataInitializer.SavesMode.Empty, modListName: definition?.Name);
                CliLog.Info($"[mount] 独立 savedata 已备好：{record.SaveData}");
            }
            catch (Exception failure)
            {
                if (record.OnWorktree && store.FindByProductRoot(productRoot).All(other => other.Number == number))
                {
                    try
                    {
                        WorktreeOutputLink.RemoveLink(descriptor, productRoot);
                    }
                    catch (Exception rollbackFailure)
                    {
                        log?.Invoke($"[mount] 回滚未完全：{rollbackFailure.Message}");
                    }
                }

                try
                {
                    string saveData = SaveDataInitializer.AssertManagedSaveDataPath(record, saveDataRoot);
                    if (Directory.Exists(saveData))
                    {
                        SaveDataInitializer.DeleteTree(saveData);
                    }
                }
                catch (Exception rollbackFailure)
                {
                    log?.Invoke($"[mount] 回滚未完全：{rollbackFailure.Message}");
                }

                try
                {
                    store.Remove(number);
                }
                catch (Exception rollbackFailure)
                {
                    log?.Invoke($"[mount] 回滚未完全：{rollbackFailure.Message}");
                }

                throw new StoreException($"{failure.Message}\n[mount] 已尝试回滚");
            }

            record.State = ClaimRecord.StateReady;
            store.Write(record);
            return record;
        }
    }

    private void ThrowIfProductRootClaimed(PurposePolicy policy)
    {
        if (policy.AllowsMultiplePerTree) return;
        List<ClaimRecord> sameProductRoot = store.FindByProductRoot(productRoot, policy.WireName);
        if (sameProductRoot.Count > 0)
        {
            throw new ProductRootClaimedException(sameProductRoot[0], productRoot);
        }
    }
}

public sealed class UnmountFlow(OrchestratorDescriptor? descriptor, ClaimStore store, string saveDataRoot,
    string? mainProductRootHint = null)
{
    private readonly OrchestratorDescriptor? descriptor = descriptor;
    private readonly ClaimStore store = store;
    private readonly string saveDataRoot = saveDataRoot;
    private readonly string? mainProductRootHint = mainProductRootHint;

    public bool Unmount(
        ClaimRecord claim,
        bool forceBusy,
        Action<string>? log,
        Func<List<GameProcessProbe.GameProcess>>? probeOverride = null)
    {
        bool resident = claim.MainTreeResident;
        if (resident && !ProductRootGone(claim.ProductRoot))
        {
            log?.Invoke($"[FAIL] 实例 {claim.DisplayName} 是主树的常驻登记，不随 unmount 卸载");
            return false;
        }

        if (descriptor == null && !DescriptorOptional(claim))
            throw new InvalidOperationException($"实例 {claim.DisplayName} 卸载须带同产品的编排描述符");

        int number = claim.Number;
        log?.Invoke(resident
            ? $"[unmount] 实例 {claim.DisplayName} → {claim.ProductRoot}（主树登记，产品根已不在盘上）"
            : $"[unmount] 实例 {claim.DisplayName} → {claim.ProductRoot}");

        ColdStartLock.Owners owners = ColdStartLock.OwnersOf(store.Root, number);
        ColdStartLock.Holder? starting = owners.Starting;
        if (owners.Any && !forceBusy)
        {
            log?.Invoke("  " + (starting != null
                ? ColdStartLock.RefusalLine(store.Root, number, starting)
                : ColdStartLock.GroupRefusalLine(claim.DisplayName, owners.Group!, DateTimeOffset.Now)));
            return false;
        }

        if (starting != null && !ColdStartLock.TakeOver(store.Root, number, starting, line => log?.Invoke("  " + line)))
            return false;

        ColdStartLock.Result unmounting = ColdStartLock.TryAcquire(store.Root, number, claim.ProductRoot,
            DateTimeOffset.Now, "卸载");
        if (unmounting.Lock == null)
        {
            log?.Invoke("  " + ColdStartLock.RefusalLine(store.Root, number, unmounting.HeldBy!, bypass: !forceBusy));
            return false;
        }

        using ColdStartLock? heldUnmount = unmounting.Lock;

        if (!forceBusy && ColdStartLock.OwnersOf(store.Root, number).Group is { } group)
        {
            log?.Invoke("  " + ColdStartLock.GroupRefusalLine(claim.DisplayName, group, DateTimeOffset.Now));
            return false;
        }

        List<GameProcessProbe.GameProcess> processes =
            probeOverride?.Invoke() ?? GameProcessProbe.Enumerate();
        List<GameProcessProbe.GameProcess> blockers =
        [
            .. processes
                .Where(process => string.IsNullOrWhiteSpace(process.CommandLine)
                                  || GameProcessProbe.ClaimsNumber(process.CommandLine,
                                      RimRunner.RimRunnerEngineArgs.Instance, number))
        ];
        if (blockers.Count > 0)
        {
            foreach (GameProcessProbe.GameProcess process in blockers)
            {
                string reason = string.IsNullOrWhiteSpace(process.CommandLine)
                    ? "命令行不可判定，归属未知"
                    : "该实例正在运行";
                log?.Invoke($"  [FAIL] {reason}：PID={process.Pid}");
            }

            return false;
        }

        using IDisposable sameTree = store.TreeUnmountLock(claim.ProductRoot);

        bool gone = WorktreeOutputLink.TreeGone(claim.ProductRoot);
        List<int> siblings =
        [
            .. store.FindByProductRoot(claim.ProductRoot).Where(other => other.Number != claim.Number).Select(other => other.Number)
        ];
        if (claim.OnWorktree && !Harvest(claim, gone, siblings, log))
        {
            return false;
        }

        if (claim.Policy.ArchivesDiagnosticsOnUnmount && !ArchiveNocompatDiagnostics(claim, log))
        {
            return false;
        }

        foreach (string wrapper in claim.Wrappers ?? [])
        {
            LegacyWrappers.RemoveWrapper(wrapper);
            CliLog.Info($"[unmount] 旧形态载入目录已删除：{wrapper}");
        }

        string saveData = SaveDataInitializer.AssertManagedSaveDataPath(claim, saveDataRoot);
        if (Directory.Exists(saveData))
        {
            SaveDataInitializer.DeleteTree(saveData);
            CliLog.Info($"[unmount] savedata 已删除：{saveData}");
        }

        if (claim.TestMod != null)
            TestModSynthesizer.Unlink(store, claim.TestMod);
        if (descriptor != null)
            new TestModSynthesizer(descriptor, store).Remove(claim.ModsFolder, number);
        TestModSynthesizer.DeleteInstanceDir(store, number);

        bool lastInModsFolder = store.FindByProductRoot(claim.ProductRoot)
            .All(other => other.Number == claim.Number || !PathIdentity.Same(other.ModsFolder, claim.ModsFolder));
        if (lastInModsFolder)
        {
            using (store.SweepLock())
                LoadDirPool.RemoveTree(claim.ModsFolder, claim.ProductRoot, log);
        }

        if (claim.OnWorktree && siblings.Count == 0)
        {
            if (gone)
            {
                WorktreeOutputLink.RemoveShell(descriptor!, claim.ProductRoot);
            }
            else if (WorktreeOutputLink.RemoveLink(OwnDescriptor(claim), claim.ProductRoot))
            {
                CliLog.Info($"[unmount] 产出根链接已摘除：{OwnDescriptor(claim).ResolveOutputDir(claim.ProductRoot)}");
            }
        }

        store.Remove(number);
        CliLog.Info("[unmount] 登记已删除");
        if (CompatMirror.MirrorOf(claim.ModsFolder) is string mirror)
            CompatMirror.Collect(store, CompatMirror.GameDirOf(mirror), log);
        return true;
    }

    public static bool ProductRootGone(string productRoot)
    {
        string? drive = Path.GetPathRoot(CommandContext.FullPath(productRoot));
        return WorktreeOutputLink.TreeGone(productRoot) && !string.IsNullOrEmpty(drive) && Directory.Exists(drive);
    }

    public static (OrchestratorDescriptor Descriptor, string Root)? SourceOf(ClaimRecord claim, string? searchDir = null)
    {
        bool gone = ProductRootGone(claim.ProductRoot);
        if (!gone && File.Exists(Path.Combine(claim.ProductRoot, OrchestratorDescriptor.FileName))
                  && SameProduct(claim, claim.ProductRoot) is { } own)
            return own;
        if (!gone && searchDir == null)
            return null;

        if (claim.MainProductRoot != null && File.Exists(Path.Combine(claim.MainProductRoot, OrchestratorDescriptor.FileName))
                                   && !MainWorktree.IsWorktree(claim.MainProductRoot)
                                   && SameProduct(claim, claim.MainProductRoot) is { } recorded)
            return recorded;

        string? parent = Path.GetDirectoryName(CommandContext.FullPath(claim.ProductRoot).TrimEnd('\\', '/'));
        if (parent != null && OrchestratorDescriptor.FindProductRoot(parent) is string root
                           && Directory.Exists(Path.Combine(root, ".git"))
                           && SameProduct(claim, root) is { } above)
            return above;

        return searchDir != null && OrchestratorDescriptor.FindProductRoot(searchDir) is string here
            ? SameProduct(claim, here)
            : null;
    }

    private static (OrchestratorDescriptor, string)? SameProduct(ClaimRecord claim, string root)
    {
        OrchestratorDescriptor descriptor = OrchestratorDescriptor.Load(root).Descriptor;
        return descriptor.Product == claim.Product ? (descriptor, root) : null;
    }

    public static UnmountFlow For((OrchestratorDescriptor Descriptor, string Root)? source, ClaimStore store,
        string saveDataRoot) =>
        new(source?.Descriptor, store, saveDataRoot,
            source == null ? null : TreeScope.Of(source.Value.Root).MainOrSelfProductRoot);

    public static bool DescriptorOptional(ClaimRecord claim) =>
        !claim.OnWorktree && ProductRootGone(claim.ProductRoot);

    internal static string NocompatDiagnosticsDir(ClaimRecord claim, OrchestratorDescriptor? descriptor,
        string? mainProductRootHint = null)
    {
        string? root = mainProductRootHint
                       ?? TreeScope.Of(claim.ProductRoot).MainProductRoot
                       ?? (Directory.Exists(claim.ProductRoot) ? claim.ProductRoot : null);
        OrchestratorDescriptor? reports = descriptor != null && root != null
                                          && PathIdentity.Same(root, claim.ProductRoot)
            ? descriptor
            : null;
        if (root != null && reports == null && File.Exists(Path.Combine(root, OrchestratorDescriptor.FileName)))
        {
            try
            {
                reports = OrchestratorDescriptor.Load(root).Descriptor;
            }
            catch (DescriptorException)
            {
            }
        }

        return root != null && reports != null
            ? Path.Combine(reports.ResolveReportsDir(root), RimRunner.RimRunnerDataLayout.RimRunnerLogSubDirName)
            : Path.Combine(MachineRoot.Resolve(), RimRunner.RimRunnerDataLayout.RimRunnerLogSubDirName);
    }

    internal static string NocompatDiagnosticsStem(ClaimRecord claim)
    {
        string logPath = GameArgsBuilder.LogPathFor(claim);
        DateTime at = claim.StoppedAt?.ToLocalTime().DateTime
                      ?? (File.Exists(logPath) ? File.GetLastWriteTime(logPath) : DateTime.Now);
        return $"{at:yyyyMMdd-HHmmss}-nocompat-{claim.Number}";
    }

    private bool ArchiveNocompatDiagnostics(ClaimRecord claim, Action<string>? log)
    {
        string logPath = GameArgsBuilder.LogPathFor(claim);
        string stem = NocompatDiagnosticsStem(claim);
        string? stopNote = IdleExit.StopNote(claim);
        try
        {
            string dir = DiagnosticsDir(claim);
            Directory.CreateDirectory(dir);
            if (File.Exists(logPath))
            {
                string archived = WriteArchive(dir, stem, "-Player.log", File.ReadAllBytes(logPath));
                CliLog.Info($"[unmount] 实例 {claim.DisplayName} 的 Player.log 已归档：{archived}");
            }
            else
            {
                CliLog.Info($"[unmount] 实例 {claim.DisplayName} 的 Player.log 不存在：{logPath}");
            }

            string note = string.Join("\n",
            [
                $"实例：{claim.DisplayName}",
                $"实例号：{claim.Number}",
                $"产品：{claim.Product}",
                $"用途：{claim.EffectivePurpose}",
                claim.StoppedAt is { } at ? $"停止时刻：{at:O}" : "停止时刻：未记录",
                "停因：" + (stopNote ?? "未记录（登记与 Player.log 都未记录停止原因）"),
                "原 savedata：" + claim.SaveData,
                "Player.log：" + (File.Exists(logPath) ? "已归档" : "不存在"),
            ]) + "\n";
            string notePath = WriteArchive(dir, stem, "-stop.log", new UTF8Encoding(false).GetBytes(note));
            CliLog.Info($"[unmount] 实例 {claim.DisplayName} 的停因已归档：{notePath}");
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException
                                            or DescriptorException or StoreException)
        {
            log?.Invoke($"[FAIL] 实例 {claim.DisplayName} 的诊断归档未写成（{failure.Message}）；未卸载，登记与 savedata 保留");
            return false;
        }
    }

    private string DiagnosticsDir(ClaimRecord claim)
    {
        OrchestratorDescriptor? own = File.Exists(Path.Combine(claim.ProductRoot, OrchestratorDescriptor.FileName))
            ? OrchestratorDescriptor.Load(claim.ProductRoot).Descriptor
            : descriptor;
        return NocompatDiagnosticsDir(claim, own, mainProductRootHint);
    }

    private static string WriteArchive(string dir, string stem, string suffix, byte[] bytes)
    {
        string temp = Path.Combine(dir, $".{stem}{suffix}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            string path = Path.Combine(dir, stem + suffix);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    File.Move(temp, path);
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                        return path;
                    string hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..8];
                    path = Path.Combine(dir, $"{stem}-{hash}{suffix}");
                }
            }

            throw new IOException($"归档文件已存在且内容不匹配：{path}");
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private OrchestratorDescriptor OwnDescriptor(ClaimRecord claim) =>
        File.Exists(Path.Combine(claim.ProductRoot, OrchestratorDescriptor.FileName))
            ? OrchestratorDescriptor.Load(claim.ProductRoot).Descriptor
            : descriptor!;

    private bool Harvest(ClaimRecord claim, bool gone, List<int> siblings, Action<string>? log)
    {
        string? mainProductRoot = gone ? mainProductRootHint : TreeScope.Of(claim.ProductRoot).MainProductRoot;
        if (mainProductRoot == null)
        {
            log?.Invoke(gone
                ? $"  [归集] worktree 已不在盘上，未找到该 worktree 的主树，无可归集：{claim.ProductRoot}"
                : MainWorktree.IsWorktree(claim.ProductRoot)
                    ? $"  [归集] 未找到该 worktree 的主树，无可归集：{claim.ProductRoot}"
                    : $"  [归集] 不是 git worktree，无主树可归集：{claim.ProductRoot}");
            return true;
        }

        if (siblings.Count > 0)
        {
            log?.Invoke($"  [归集] 本树另有实例 {string.Join("、", siblings.Select(store.Name))}，未归集");
            return true;
        }

        try
        {
            string target = OrchestratorDescriptor.Load(mainProductRoot).Descriptor.ResolveOutputDir(mainProductRoot);
            string source = gone
                ? WorktreeOutputLink.CellOf(target, claim.ProductRoot)
                : OwnDescriptor(claim).ResolveOutputDir(claim.ProductRoot);
            if (gone && !Directory.Exists(source))
            {
                CliLog.Info($"[归集] worktree 已不在盘上，主树里没有该 worktree 的产出格，无可归集：{claim.ProductRoot}");
                return true;
            }

            string cellName = Path.GetFileName(gone ? source : WorktreeOutputLink.CellOf(target, claim.ProductRoot));
            WorktreeHarvest.Outcome outcome = WorktreeHarvest.Collect(source, target, cellName,
                SampleStore.KindFor(mainProductRoot));

            log?.Invoke($"  [归集] {source} → {target}");
            CliLog.Info($"[归集] artifacts：复制 {outcome.ArtifactsCopied} 份，同名相同跳过 {outcome.ArtifactsSkipped} 份");
            CliLog.Info($"[归集] 读数库：追加 {outcome.RunIdsAppended} 个 runId，涉及 {outcome.SampleFilesTouched} 份文件"
                        + (outcome.RunIdsAlreadyPresent > 0 ? $"；{outcome.RunIdsAlreadyPresent} 个 runId 主树已有逐行相同的行，未重复写" : "")
                        + (outcome.UnparsedSampleLines > 0 ? $"；无法读取 {outcome.UnparsedSampleLines} 行的 runId，未复制" : ""));
            log?.Invoke($"    报告归档：复制 {outcome.ReportsCopied} 份，同名相同跳过 {outcome.ReportsSkipped} 份 → {outcome.ReportsTarget}");
            foreach (WorktreeHarvest.SupersededSave save in outcome.SavesSuperseded)
                log?.Invoke($"    导出档同名而内容不同：原名留给{(save.FromWorktree ? "本树" : "主树")}那份 {save.Current}，另一份改名留存为 {Path.GetFileName(save.Retained)}");
            foreach (string failure in outcome.PruneFailures)
                CliLog.Info($"[归集] 留存的导出档未修剪：{failure}");
            if (outcome.Conflicts.Count == 0)
            {
                if (gone)
                {
                    Directory.Delete(source, recursive: true);
                    CliLog.Info($"[归集] 产出格已删除：{source}");
                }

                return true;
            }

            foreach (WorktreeHarvest.Conflict conflict in outcome.Conflicts)
            {
                log?.Invoke("  [FAIL] 同名而内容不匹配，未覆盖：");
                log?.Invoke($"    本树 {conflict.Source}");
                log?.Invoke($"    主树 {conflict.Target}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or DescriptorException or InvalidDataException
                                              or Microsoft.Data.Sqlite.SqliteException)
        {
            log?.Invoke($"  [FAIL] 归集失败：{exception.Message}");
        }

        log?.Invoke($"  [FAIL] 实例 {claim.DisplayName} 未卸载，登记保留");
        return false;
    }
}

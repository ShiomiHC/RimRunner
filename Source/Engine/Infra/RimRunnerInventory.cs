namespace RimRunner;

/// <summary>背包装货：往 pawn 背包装一件卸货标记可回读的货。</summary>
public static class RimRunnerInventory
{
    /// <summary>装货：往背包装 count 件 def 作为单独一堆，并核验装完后卸货标记写得进去。</summary>
    /// <remarks>返回的就是背包里新装的那一堆，不与已有的同 def 堆合并。核验后卸货标记还原为 false。</remarks>
    /// <exception cref="InvalidOperationException">背包拒收，或装完后 UnloadEverything 写不进时抛。</exception>
    public static Thing LoadCargoEligibleForUnload(Pawn pawn, ThingDef def, int count)
    {
        Thing cargo = ThingMaker.MakeThing(def);
        cargo.stackCount = count;
        if (!pawn.inventory.innerContainer.TryAdd(cargo, canMergeWithExistingStacks: false))
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + pawn.LabelShort
                + " 拒绝装货 " + def.defName + "×" + count);
        }

        pawn.inventory.UnloadEverything = true;
        bool sticks = pawn.inventory.UnloadEverything;
        pawn.inventory.UnloadEverything = false;
        if (!sticks)
        {
            throw new InvalidOperationException(RimRunnerLog.Prefix + " " + pawn.LabelShort
                + " 装入 " + def.defName + " 后 UnloadEverything 无法写入：背包内容 = "
                + string.Join("、", pawn.inventory.innerContainer.Select(t => t.LabelShort))
                + "。这件货物按原版保留规则留在背包里，不算可卸货");
        }

        return cargo;
    }
}

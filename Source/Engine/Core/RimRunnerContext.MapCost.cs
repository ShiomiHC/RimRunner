namespace RimRunner;

public partial class RimRunnerContext
{
    /// <summary>对 maps 开按图分桶的 tick 开销计量：每 tick 轮换各图先后、各图同温，收尾自动卸。</summary>
    /// <remarks>只在用例体内可调，同时只开一份；同温即计量开启期间各图的 MapTemperature.OutdoorTemp 与 SeasonalTemp 取首张图的值。</remarks>
    public RimRunnerMapCostMeter OpenMapCostMeter(params Map[] maps) => OpenMapCostMeter(true, true, maps);

    /// <summary>对 maps 开按图分桶的开销计量：rotateOrder 定先后是否轮换，各图同温，收尾自动卸。</summary>
    /// <remarks>只在用例体内可调，同时只开一份。轮换要 DoSingleTick 里 MapPreTick 与 MapPostTick 各恰好一处调用。</remarks>
    /// <param name="rotateOrder">true 每 tick 轮换各图先后，false 按 vanilla 的固定先后推。</param>
    /// <param name="maps">被测的图：至少一张、互不重复、在场且未冻结。</param>
    public RimRunnerMapCostMeter OpenMapCostMeter(bool rotateOrder, params Map[] maps) => OpenMapCostMeter(rotateOrder, true, maps);

    /// <summary>对 maps 开按图分桶的开销计量：先后轮换与各图同温都由参数定，收尾自动卸。</summary>
    /// <remarks>只在用例体内可调，同时只开一份。轮换要 DoSingleTick 里 MapPreTick 与 MapPostTick 各恰好一处调用。</remarks>
    /// <param name="rotateOrder">true 每 tick 轮换各图先后，false 按 vanilla 的固定先后推。</param>
    /// <param name="sharedTemperature">true 时各图的 MapTemperature.OutdoorTemp 与 SeasonalTemp 取首张图的值。</param>
    /// <param name="maps">被测的图：至少一张、互不重复、在场且未冻结。</param>
    public RimRunnerMapCostMeter OpenMapCostMeter(bool rotateOrder, bool sharedTemperature, params Map[] maps)
    {
        RequireBody("OpenMapCostMeter");
        RejectInSceneUnwoundAtCaseEnd("OpenMapCostMeter");

        return RimRunnerMapCostMeter.Open(maps, rotateOrder, sharedTemperature);
    }
}

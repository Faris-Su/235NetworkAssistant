using System.Runtime.CompilerServices;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Resources;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 启动后的**空闲预热**（2026-09-23 新增，面向外勤小电脑）。
///
/// 思路：把"第一次用到才付钱"的纯 CPU 路径，在启动后用一个 **BelowNormal 后台线程**先走一遍 ——
/// 花的是空闲 CPU（外勤机目标：空闲 ≤ 单核 5%），买的是"现场点哪里都秒开"。
///
/// **刻意不做的事**：不在 UI 线程上预建页面。N4120 这类机器真正的瓶颈是单核与渲染，
/// 空闲预热如果去抢 UI 线程，等于把卡顿从"点的时候"提前到"启动的时候"，得不偿失。
///
/// 预热内容都是**无副作用**的：正则编译、类型初始化、资源库搜索串预计算 ——
/// 不发网络包、不碰设备、不写盘。任何一步失败都只记 Debug 日志，绝不影响功能。
/// </summary>
public static class IdleWarmup
{
    /// <summary>延迟多久开始（给启动/首屏渲染让路）。</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    public static void Start(ILogService log)
    {
        var thread = new Thread(() => Run(log))
        {
            IsBackground = true,
            Name = "235IdleWarmup",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>
    /// 同步跑一遍预热（诊断用：`--perf --perf-warm` 模拟"启动后空闲 3 秒再点页面"的场景，
    /// 用来量预热前后的"各页首次打开耗时"差多少）。
    /// </summary>
    public static void RunNow(ILogService log) => Run(log);

    private static void Run(ILogService log)
    {
        try
        {
            Thread.Sleep(Delay);

            // ① 资源库：预先计算交换机、VLAN 与场所记录的"归一化搜索串"，
            //    第一次搜索/筛选时就不用现算（见 JsonResourceRepository 的 ConditionalWeakTable 缓存）。
            WarmUpStep(log, "资源库搜索串", () =>
            {
                // 只有 JSON 资源库有"预计算搜索串"这回事；其它实现（自检桩）跳过即可。
                if (AppServices.ResourceRepository is JsonResourceRepository jsonRepository)
                {
                    jsonRepository.WarmUp();
                }
            });

            // ② 输出解析器：GeneratedRegex 是"第一次调用才编译"，先各点一遍（纯 CPU、无副作用）。
            WarmUpStep(log, "CLI 输出解析器", () =>
            {
                _ = ShowOutputParser.ParseInterfaceStatus(string.Empty);
                _ = ShowOutputParser.ParseVlan(string.Empty);
                _ = ShowOutputParser.ParseTrunk(string.Empty);
                _ = ShowOutputParser.ParseLldpNeighbors(string.Empty);
                _ = ShowOutputParser.ParseMacAddressTable(string.Empty);
                _ = ShowOutputParser.ParseArp(string.Empty);
                _ = ShowOutputParser.ParseDhcpSnoopingBinding(string.Empty);
                _ = ShowOutputParser.ParseIpInterfaceBrief(string.Empty);
                _ = ShowOutputParser.ParseVersion(string.Empty);
            });

            // ③ Excel 导入器：依赖的 OpenXML 程序集/类型先初始化（第一次导入 Excel 时的装配开销）。
            WarmUpStep(log, "Excel 导入器类型", () => RuntimeHelpers.RunClassConstructor(typeof(ExcelImporter).TypeHandle));

            log.Debug("空闲预热完成");
        }
        catch (Exception ex)
        {
            log.Debug($"空闲预热整体失败（忽略）：{ex.Message}");
        }
    }

    private static void WarmUpStep(ILogService log, string name, Action action)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            action();
            watch.Stop();
            log.Debug($"空闲预热：{name} 完成（{watch.ElapsedMilliseconds} ms）");
        }
        catch (Exception ex)
        {
            log.Debug($"空闲预热：{name} 失败（忽略）：{ex.Message}");
        }
    }
}

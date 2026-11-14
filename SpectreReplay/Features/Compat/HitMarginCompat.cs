using System;
using System.Collections.Generic;
using UnityEngine;

namespace SpectreReplay.Features.Compat;

/// <summary>
/// r141–148 / r150 双版本 HitMargin 兼容层。
///
/// r150 重排并扩充了 HitMargin：删除 <c>Perfect</c>，新增 XPerfect / PerfectPlus /
/// PerfectMinus / Midspin / FailedFloor，其后所有成员索引整体后移
/// （LatePerfect 4→6、VeryLate 5→7、TooLate 6→8、Multipress 7→9、
/// FailMiss 8→10、FailOverload 9→11、Auto 10→12、OverPress 11→13）。
///
/// 枚举成员是编译期内联常量：同一个 DLL 只能携带一套数值，所以任何直接写下的
/// <c>HitMargin.Xxx</c> 在另一个版本上都会静默指向别的成员。跨版本语义因此一律
/// 在运行期按“名字”解析，绝不直接引用会随版本漂移的枚举成员。
/// 索引 0–3 两版本一致（旧 Perfect = 新 PerfectMinus = 3），是唯一可安全内联的巧合。
/// </summary>
internal static class HitMarginCompat
{
    private static readonly Dictionary<string, HitMargin> Resolved = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);

    /// <summary>按名解析枚举值；该名字在本游戏版本不存在时返回 false（不抛异常）。</summary>
    internal static bool TryResolve(string name, out HitMargin value)
    {
        value = default;
        if (string.IsNullOrEmpty(name))
            return false;
        // 拒绝数字/符号串：Enum.TryParse 会把 "6" 当合法值，那是版本相关的陷阱
        char head = name[0];
        if (char.IsDigit(head) || head == '-' || head == '+')
            return false;
        if (Resolved.TryGetValue(name, out value))
            return true;
        if (Missing.Contains(name))
            return false;
        if (Enum.TryParse(name, false, out HitMargin parsed)
            && Enum.IsDefined(typeof(HitMargin), parsed))
        {
            Resolved[name] = parsed;
            value = parsed;
            return true;
        }

        Missing.Add(name);
        return false;
    }

    internal static HitMargin Resolve(string name, HitMargin fallback)
        => TryResolve(name, out var value) ? value : fallback;

    /// <summary>本游戏版本是否具备该判定（用于按版本决定行为，而非直接引用成员）。</summary>
    internal static bool Has(string name) => TryResolve(name, out _);

    /// <summary>本游戏版本是否已是 r150+ 的重排枚举（以 XPerfect 的存在为标志）。</summary>
    internal static bool HasXPerfect { get; } = Has("XPerfect");

    // ── 逐名取用的成员别名 ─────────────────────────────────────
    // 调用点一律用这些属性，源文件里就不出现会随版本漂移的枚举字面量。
    //
    // 缺失成员退回到 Absent（-1）而不是 default（= TooEarly，0）：退回 0 会让
    // 旧版本上 `margin == Midspin` 之类的比较对真正的 TooEarly 命中成立，是静默错判。
    // -1 不可能等于任何真实枚举值，于是"缺失即永不匹配"，调用点不必再逐处门控。

    /// <summary>本游戏版本不存在的判定：永不等于任何真实枚举值。</summary>
    internal static readonly HitMargin Absent = (HitMargin)(-1);

    internal static HitMargin TooEarly => Resolve("TooEarly", Absent);
    internal static HitMargin VeryEarly => Resolve("VeryEarly", Absent);
    internal static HitMargin EarlyPerfect => Resolve("EarlyPerfect", Absent);
    internal static HitMargin LatePerfect => Resolve("LatePerfect", Absent);
    internal static HitMargin VeryLate => Resolve("VeryLate", Absent);
    internal static HitMargin TooLate => Resolve("TooLate", Absent);
    internal static HitMargin Multipress => Resolve("Multipress", Absent);
    internal static HitMargin FailMiss => Resolve("FailMiss", Absent);
    internal static HitMargin FailOverload => Resolve("FailOverload", Absent);
    internal static HitMargin OverPress => Resolve("OverPress", Absent);
    internal static HitMargin Midspin => Resolve("Midspin", Absent);
    internal static HitMargin XPerfect => Resolve("XPerfect", Absent);
    internal static HitMargin PerfectPlus => Resolve("PerfectPlus", Absent);
    internal static HitMargin FailedFloor => Resolve("FailedFloor", Absent);

    /// <summary>“最完美”档（旧 Perfect / 新 PerfectMinus），旧语义下的默认替身。</summary>
    internal static HitMargin PerfectEquivalent => Resolve("PerfectMinus", Resolve("Perfect", Absent));

    /// <summary>Auto 档（两版本都存在）。</summary>
    internal static HitMargin Auto => Resolve("Auto", Absent);

    private static HitMargin[] Set(params string[] names)
    {
        var list = new List<HitMargin>(names.Length);
        foreach (var name in names)
        {
            if (TryResolve(name, out var value) && !list.Contains(value))
                list.Add(value);
        }

        return list.ToArray();
    }

    // ── 语义集合：按名构建，缺名自动忽略 → 每个版本各自得到本版本的正确集合 ──

    // 完美族：旧 = {Perfect, Auto}；新 = {XPerfect, PerfectMinus, PerfectPlus, Auto, Midspin, Multipress}
    private static readonly HitMargin[] PerfectFamily =
        Set("XPerfect", "PerfectMinus", "Perfect", "PerfectPlus", "Auto", "Midspin", "Multipress");

    // 次完美：EarlyPerfect / LatePerfect。r150 起不再计入“纯完美”，但仍计入“经典完美”
    private static readonly HitMargin[] SemiPerfectFamily = Set("EarlyPerfect", "LatePerfect");

    // 计入但不完美：VeryEarly / VeryLate
    private static readonly HitMargin[] VeryEarlyLateFamily = Set("VeryEarly", "VeryLate");

    // 失败：FailMiss / FailOverload
    private static readonly HitMargin[] FailFamily = Set("FailMiss", "FailOverload");

    // 最内完美族（不含 Auto / 中旋 / 多压这类无时机语义的完美）：
    // 旧 = {Perfect}；新 = {XPerfect, PerfectMinus, PerfectPlus}——三者都是“最内档”
    private static readonly HitMargin[] InnerPerfectFamily =
        Set("XPerfect", "PerfectMinus", "Perfect", "PerfectPlus");

    // Legacy Accuracy 分子族（跨口径 + r148 本机主行共用）：3.3.1 把中旋命中
    // 记成 Perfect（用户裁定 + r148 源码 scrPlanet 强制 Perfect），故在 r150
    // 枚举上 = 三档 Perfect + Auto + Midspin；r148 枚举上解析为 {Perfect, Auto}。
    private static readonly HitMargin[] LegacyAccuracyFamily =
        Set("XPerfect", "PerfectMinus", "Perfect", "PerfectPlus", "Auto", "Midspin");

    // 带时机语义、可参与自定义分档的命中：
    // 旧 = VeryEarly..VeryLate 五档；新 = 再加 XPerfect / PerfectPlus
    private static readonly HitMargin[] TierCountedFamily =
        Set("VeryEarly", "EarlyPerfect", "XPerfect", "PerfectMinus", "Perfect", "PerfectPlus",
            "LatePerfect", "VeryLate");

    // 无时机语义的命中（自定义分档一律排除）
    private static readonly HitMargin[] TimelessFamily = Set("Auto", "Midspin", "Multipress", "FailedFloor");

    private static bool In(HitMargin margin, HitMargin[] set)
    {
        for (int i = 0; i < set.Length; i++)
        {
            if (set[i] == margin)
                return true;
        }

        return false;
    }

    /// <summary>完美判定。classic = 把 EarlyPerfect/LatePerfect 也算作完美（原版经典口径）。</summary>
    internal static bool IsPerfect(HitMargin margin, bool classic = false)
        => In(margin, PerfectFamily) || (classic && In(margin, SemiPerfectFamily));

    /// <summary>最内档完美（旧 Perfect / 新三档 Perfect）。</summary>
    internal static bool IsInnerPerfect(HitMargin margin) => In(margin, InnerPerfectFamily);

    /// <summary>
    /// 全 P 连击口径：旧版 MaxCombo 的集合 = {EarlyPerfect, Perfect, LatePerfect, Auto}。
    /// 按名表达后在新版自然成为 {EarlyPerfect, PerfectMinus, LatePerfect, Auto}——
    /// 刻意不含 Multipress：旧版该档不参与连击，新版同档也应保持不参与。
    /// </summary>
    internal static bool IsComboPerfect(HitMargin margin)
        => In(margin, SemiPerfectFamily) || margin == PerfectEquivalent || margin == Auto;

    /// <summary>
    /// 是否计入命中。逐版本逐字复刻，而不是取其一：
    ///   旧版 = <c>scrMisc.IsValidHit</c>（r150 已删除）
    ///   新版 = <c>HitMarginHelper.IsCounted</c>（r150 新增）
    /// 两者并不等价（旧版 PurePerfectOnly 只认 Perfect，新版认整个完美族；
    /// 旧版不认 Multipress，新版认），所以必须按版本分开实现。
    /// </summary>
    internal static bool IsCounted(HitMargin margin)
    {
        if (HasXPerfect)
        {
            // r150+：复刻 HitMarginHelper.IsCounted
            switch (GCS.hitMarginLimit)
            {
                case HitMarginLimit.PerfectsOnly:
                    return IsPerfect(margin, classic: true);
                case HitMarginLimit.PurePerfectOnly:
                    return IsPerfect(margin);
                case HitMarginLimit.None:
                    return IsPerfect(margin, classic: true) || In(margin, VeryEarlyLateFamily);
                default:
                    return true;
            }
        }

        // r141–148：复刻 scrMisc.IsValidHit（Auto 恒真，且只有 Perfect 一档算"纯完美"）
        if (margin == Auto)
            return true;
        switch (GCS.hitMarginLimit)
        {
            case HitMarginLimit.PerfectsOnly:
                return In(margin, SemiPerfectFamily) || margin == PerfectEquivalent;
            case HitMarginLimit.PurePerfectOnly:
                return margin == PerfectEquivalent;
            default:
                return In(margin, SemiPerfectFamily) || In(margin, VeryEarlyLateFamily)
                    || margin == PerfectEquivalent;
        }
    }

    internal static bool IsFail(HitMargin margin) => In(margin, FailFamily);

    /// <summary>无时机语义的命中（中旋、自动、多压、漏块）。</summary>
    internal static bool IsTimeless(HitMargin margin) => In(margin, TimelessFamily);

    /// <summary>
    /// 过滤归并名：r150 三档完美（XPerfect/PerfectMinus/PerfectPlus）归并回旧 Perfect 槽。
    /// “隐藏判定”按原名 + 归并名双重匹配：勾选 Perfect = 三档完美飘字全部隐藏；
    /// 勾选 XPerfect（独立槽）= 只隐藏 XPerfect。r148 下恒等（Perfect 本身在 InnerPerfect 族）。
    /// </summary>
    internal static string FilterName(HitMargin margin)
        => IsInnerPerfect(margin) ? "Perfect" : CachedName(margin);

    // 枚举名缓存：Enum.ToString() 每次调用都**分配一个新字符串**（名字不做缓存），
    // 而它在"隐藏判定飘字"过滤里每次命中要用两次（原名 + 归并名）——节奏游戏里
    // 每秒数十次的小对象分配纯属浪费。枚举值→名字是不可变映射，缓存语义完全等价。
    // 用数组按 (int)margin 索引：比 Dictionary 更省一次哈希，且值域小、命中率高。
    //
    // 无需加锁：CLR 内存模型下引用写入具备 release 语义，读到引用即见完整构造好的
    // String；即使两个线程同时未命中，也只是各算一份内容相等的字符串（原子引用写，
    // 不可能读到半构造对象）。最坏情况是重复分配一次，不是正确性问题。
    private static readonly string[] NameCache = new string[64];

    /// <summary>带缓存的枚举名（等价于 <c>margin.ToString()</c>，但不重复分配）。</summary>
    internal static string CachedName(HitMargin margin)
    {
        int i = (int)margin;
        if ((uint)i >= (uint)NameCache.Length)
            return margin.ToString();   // 越界（未来版本新增成员）直接走原路径
        return NameCache[i] ?? (NameCache[i] = margin.ToString());
    }

    /// <summary>
    /// 判定配色/文案的槽位（0..9，与 <see cref="Settings.VanillaMarginColors"/> 同序）。
    /// 旧版 10 槽直接就是枚举序；r150 枚举重排且拆出三档 Perfect，
    /// 必须按名归并回旧槽位，否则整排颜色错位。
    /// 归并规则：三档 Perfect（XPerfect/PerfectMinus/PerfectPlus）共用旧 Perfect 槽；
    /// 中旋/漏块无对应槽位（-1）。
    /// </summary>
    internal static int ColorIndex(HitMargin margin)
    {
        if (margin == TooEarly) return 0;
        if (margin == VeryEarly) return 1;
        if (margin == EarlyPerfect) return 2;
        if (IsInnerPerfect(margin)) return 3; // 旧 Perfect / 新 XPerfect + PerfectMinus + PerfectPlus
        if (margin == LatePerfect) return 4;
        if (margin == VeryLate) return 5;
        if (margin == TooLate) return 6;
        if (margin == Multipress) return 7;
        if (IsFail(margin) || margin == OverPress) return 8;
        if (margin == Auto) return 9;
        return -1;
    }

    /// <summary>
    /// 判定分档流水线（GetHitMargin 记录 / AddHit 落账）关心的命中集合。
    /// r150 起中旋有独立成员，必须显式纳入，否则中旋命中不再进账。
    /// </summary>
    internal static bool IsJudgeTracked(HitMargin margin)
        => In(margin, TierCountedFamily) || margin == Auto || margin == Midspin;

    /// <summary>该判定是否具备 xacc 权重（= 参与玩家命中统计；未命中返回 null）。</summary>
    internal static double? XAccWeight(HitMargin margin)
        => XAccWeights.TryGetValue(margin, out double weight) ? weight : (double?)null;

    internal static bool IsPlayerHitMargin(HitMargin margin) => XAccWeights.ContainsKey(margin);

    // ── 原版 xacc 权重表（逐版本，r148 反编译校正）─────────────
    // Legacy = r148（adofai-libs feeed7081，scrMarginTracker.CalculatePercentAcc）：
    //   分子 = 1.0×(Perfect+Auto) + 0.75×(Early,Late) + 0.4×(Very) + 0.2×(Too)
    //     + 0.2×deadTiles；分母 = hitMargins.Count + deadTiles（多压/Auto/失败
    //     全在内）；×0.9875^检查点。中旋无独立档、命中被强制记成 Perfect
    //     （scrPlanet 两处 `if (player.midspinInfiniteMargin)
    //     hitMargin2 = HitMargin.Perfect`，2026-11-14 与用户记忆互相印证）。
    //   deadTiles 不是判定档、无法从分布重建 → 旧口径缺 0.2×deadTiles 分子项
    //     与扣减项（r150 把漏砖变成了 FailedFloor 判定档，Modern 口径才有）。
    // Modern = r150（HitMarginHelper.PlayerHitMarginWeights）：Auto/中旋/多压
    //   不计入，1.0×{XPerfect,PerfectMinus,PerfectPlus} + 0.75 + 0.4
    //   + 0.2×{Too,FailedFloor}；分母 = playerHitMarginCount + FailedFloor
    //   （官方式子的重复计数）。
    // 2026-11-14 用户裁定收敛展示范围：跨口径对照**只在 3.4.0 上展示**（3.3.1
    // 口径，见 Patch_CrossEraAccuracy / 回放信息面板）；3.3.1 本机**不展示**
    // 3.4.0 口径。Legacy 表同时服务两处：r148 本机的主行 + 3.4.0 上的跨口径行。
    private static readonly Dictionary<HitMargin, double> LegacyXAccWeights = BuildLegacyWeights();
    private static readonly Dictionary<HitMargin, double> ModernXAccWeights = BuildModernWeights();

    /// <summary>主行口径 = 当前游戏版本的官方公式。</summary>
    private static Dictionary<HitMargin, double> XAccWeights => HasXPerfect ? ModernXAccWeights : LegacyXAccWeights;

    private static Dictionary<HitMargin, double> BuildModernWeights()
    {
        var map = new Dictionary<HitMargin, double>();

        void Add(double weight, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryResolve(name, out var value))
                    map[value] = weight;
            }
        }

        // r150：对齐 HitMarginHelper.PlayerHitMarginWeights（Auto / 中旋 / 多压无权重）
        Add(1.0, "XPerfect", "PerfectMinus", "Perfect", "PerfectPlus");
        Add(0.2, "TooEarly", "TooLate", "FailedFloor");
        Add(0.75, "EarlyPerfect", "LatePerfect");
        Add(0.4, "VeryEarly", "VeryLate");
        Add(0.0, "FailMiss", "FailOverload");
        return map;
    }

    private static Dictionary<HitMargin, double> BuildLegacyWeights()
    {
        var map = new Dictionary<HitMargin, double>();

        void Add(double weight, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryResolve(name, out var value))
                    map[value] = weight;
            }
        }

        // r148：见上。Midspin 计 1.0（3.3.1 把中旋记成 Perfect——r148 源码
        // scrPlanet 强制 Perfect，跨口径行据此还原）；FailedFloor 计 0.2 =
        // 旧分子里的 0.2×deadTiles 项（FF≈deadTiles）；Multipress 计 0.0 =
        // 旧分母按全部判定条数把它算进去（无分子权重）。缺名（r148 本机无
        // Midspin/FailedFloor）自动缺席。
        Add(1.0, "XPerfect", "PerfectMinus", "Perfect", "PerfectPlus", "Auto", "Midspin");
        Add(0.2, "FailedFloor", "TooEarly", "TooLate");
        Add(0.0, "Multipress");
        Add(0.0, "FailMiss", "FailOverload");
        Add(0.75, "EarlyPerfect", "LatePerfect");
        Add(0.4, "VeryEarly", "VeryLate");
        return map;
    }

    /// <summary>
    /// 原版 xacc 分子：Σ(该判定命中数 × 原版权重)。按名解析，逐版本对齐官方公式。
    /// counts 为 <c>scrMarginTracker.hitMarginsCount</c> 同构数组（按枚举值索引）。
    /// </summary>
    internal static double XAccNumerator(int[] counts)
    {
        if (counts == null)
            return 0.0;
        double sum = 0.0;
        foreach (var pair in XAccWeights)
        {
            int index = (int)pair.Key;
            if ((uint)index < (uint)counts.Length)
                sum += pair.Value * counts[index];
        }

        return sum;
    }

    /// <summary>
    /// 原版 xacc 分母：<c>playerHitMarginCount + GetHits(FailedFloor)</c>
    /// （scrMarginTracker.cs 的 <c>CalculatePercentAcc</c>）。
    ///
    /// <para><b>不能用 <c>hitMargins.Count</c>。</b>那是「全部判定条数」，包含
    /// 中旋 / 多压 / Auto 这些**没有 xacc 权重**的档位。用它会把这些档位算进
    /// 分母：一局 1200 个完美 + 47 个中旋会算成 1200/1247 = 96.23%，
    /// 而官方答案是 1200/1200 = 100%。「完美无瑕却显示 96%」就是这么来的。</para>
    ///
    /// <para>末尾再 + FailedFloor 是**官方式子本身**的重复计数（FailedFloor 带 0.2
    /// 权重、已进 playerHitMarginCount，官方仍再加一次）。照抄而不"化简"，
    /// 否则与游戏结算面板对不上。</para>
    /// </summary>
    internal static double XAccDenominator(int[] counts)
    {
        if (counts == null)
            return 0.0;
        double sum = 0.0;
        foreach (var pair in XAccWeights)
        {
            int index = (int)pair.Key;
            if ((uint)index < (uint)counts.Length)
                sum += counts[index];
        }

        // FailedFloor 的重复计数只属于 r150 官方式子；r148 分母 = hitMargins.Count
        // + deadTiles——deadTiles 不是判定档，无法从分布重建，旧版口径缺这一项。
        return HasXPerfect ? sum + SumSet(counts, FailedFloorFamily) : sum;
    }

    /// <summary>
    /// 原版 xacc 百分比（当前游戏版本的官方口径）。分母为 0 返回 -1（含义是
    /// "这份回放没有判定数据"，界面应显示占位符而不是 0.00%）。
    /// </summary>
    internal static double XAcc(int[] counts)
    {
        double denominator = XAccDenominator(counts);
        if (denominator <= 0.0)
            return -1.0;
        return XAccNumerator(counts) / denominator;
    }

    // ── Legacy 口径（r148 公式）单独出口：3.4.0 上的跨口径标注用 ──
    // 分母 = Σ Legacy 桶（FailedFloor 已在表内顶替 deadTiles 进分子/分母、
    // Multipress@0.0 只进分母），不再额外 + FailedFloor。

    /// <summary>
    /// Legacy（≤3.3.1）口径的 X-Accuracy：同一份分布在 r148 官方公式下的取值。
    /// 仅 3.4.0 本机的跨口径标注使用（3.3.1 本机的主行走 <see cref="XAcc"/>）。
    /// 分母为 0 返回 -1。
    /// </summary>
    internal static double LegacyXAcc(int[] counts)
    {
        if (counts == null)
            return -1.0;
        double numerator = 0.0;
        double denominator = 0.0;
        foreach (var pair in LegacyXAccWeights)
        {
            int index = (int)pair.Key;
            if ((uint)index >= (uint)counts.Length)
                continue;
            numerator += pair.Value * counts[index];
            denominator += counts[index];
        }

        return denominator <= 0.0 ? -1.0 : numerator / denominator;
    }

    /// <summary>
    /// Legacy（≤3.3.1）口径的 Accuracy：分子族 = Perfect+Auto（中旋按 Perfect
    /// 计，见 LegacyAccuracyFamily）；分母与奖励/扣减同 r148 式（FailedFloor
    /// 顶替 deadTiles 扣减）。
    /// </summary>
    internal static double LegacyAccuracy(int[] counts)
    {
        if (counts == null)
            return -1.0;
        int total = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            total += counts[i];
        }
        if (total <= 0)
            return -1.0;

        int perfect = SumSet(counts, LegacyAccuracyFamily) + SumSet(counts, SemiPerfectFamily);
        int denominator = total + SumSet(counts, FailFamily);
        double acc = perfect == denominator ? 1.0 : (double)perfect / denominator;
        acc += SumSet(counts, LegacyAccuracyFamily) * 0.0001;
        // 扣减：r148 式为 deadTiles×1e-4，跨口径用 FailedFloor 顶替（≈漏砖数）。
        acc -= SumSet(counts, FailedFloorFamily) * 0.0001;
        return acc;
    }

    /// <summary>
    /// 原版 Accuracy（与 <c>scrMarginTracker.CalculatePercentAcc</c> 同式）。
    ///
    /// <para><b>跟 xacc 不是一回事。</b>xacc 是按判定档位加权的"误差分"；
    /// Accuracy 是"完美命中占全部判定的比例"，再补一条内档完美的小数奖励。
    /// 官方式子（scrMarginTracker.cs:189-197）：</para>
    /// <code>
    /// num  = Perfect 族 + SemiPerfect 族
    /// num2 = hitMargins.Count + (FailMiss + FailOverload)
    /// acc  = num == num2 ? 1 : num / num2
    /// acc += Perfect 族内档 × 0.0001 − FailedFloor × 0.0001
    /// </code>
    ///
    /// <para><b>为什么能从判定分布算出来。</b><c>hitMargins.Count</c> 就是
    /// 全部判定的条数，而回放里写的 <c>JudgmentList</c> 正是同一个
    /// <c>hitMarginsCount</c> 数组；两者是同一份数据的两种形态。所以老回放
    /// 不用重录、格式不用改，直接由分布还原。</para>
    ///
    /// <para><b>版本差异在集合里，不在式子。</b>Perfect 族 / SemiPerfect 族
    /// 由按名解析的集合给出（旧版 Perfect+Auto，新版三档 Perfect+Auto+中旋+多压），
    /// 与 <see cref="IsCounted"/> 用的是同一套兼容层，不写死下标。</para>
    ///
    /// **分母为 0 → 返回 -1**（不是 0）：0 会被画成"0.00%"，而真实含义是
    /// "这份回放没有判定数据"，界面应显示占位符。
    /// </summary>
    internal static double Accuracy(int[] counts)
    {
        if (counts == null)
            return -1.0;
        int total = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            total += counts[i];
        }
        if (total <= 0)
            return -1.0;

        // 分子族：r150 = PerfectHitMargins（三档+Auto+中旋+多压）；r148 =
        // Perfect + Early + Late + Auto（多压不算完美，r148 反编译）。
        HitMargin[] perfectFamily = HasXPerfect ? PerfectFamily : LegacyAccuracyFamily;
        int perfect = SumSet(counts, perfectFamily) + SumSet(counts, SemiPerfectFamily);
        // **不是 total + deaths。** 官方式子 num2 = hitMargins.Count + GetDeaths()，
        // 而 hitMargins 里本来就已经记了每次 FailMiss/FailOverload（AddHitInternal
        // 无条件 Add），GetDeaths() 只是把这两桶再数一遍。照抄即可，
        // 不要"化简"成 total——那会把失败命中漏出分母，Accuracy 偏高。
        int denominator = total + SumSet(counts, FailFamily);

        double acc = perfect == denominator ? 1.0 : (double)perfect / denominator;
        // 完美奖励：**官方加到的是 PerfectHitMargins，与半完美无关**（r148：
        // GetHits(Perfect, Auto)×1e-4；r150：PerfectHitMargins×1e-4）。
        // 扣减：r150 = FailedFloor×1e-4；r148 = deadTiles×1e-4——deadTiles 不是
        // 判定档、无法从分布重建，旧版口径缺这一项（见权重表注释）。
        acc += SumSet(counts, perfectFamily) * 0.0001;
        acc -= SumSet(counts, FailedFloorFamily) * 0.0001;
        return acc;
    }

    /// <summary>FailedFloor 单元素集合（旧版本没有这个成员 → 空集，扣 0）。</summary>
    private static readonly HitMargin[] FailedFloorFamily = Set("FailedFloor");

    private static int SumSet(int[] counts, HitMargin[] set)
    {
        int sum = 0;
        for (int i = 0; i < set.Length; i++)
        {
            int index = (int)set[i];
            if ((uint)index < (uint)counts.Length)
                sum += counts[index];
        }
        return sum;
    }

    /// <summary>失败命中总数（旧 FailMiss + FailOverload 桶，按名解析）。</summary>
    internal static int FailCount(int[] counts)
    {
        if (counts == null)
            return 0;
        int total = 0;
        if (TryResolve("FailMiss", out var miss) && (uint)(int)miss < (uint)counts.Length)
            total += counts[(int)miss];
        if (TryResolve("FailOverload", out var overload) && (uint)(int)overload < (uint)counts.Length)
            total += counts[(int)overload];
        return total;
    }

    // ── 跨版本判定分布重排（SpVersion 驱动，2026-11-14 用户追问发现）──
    // JudgmentList 是录制时 hitMarginsCount 的原样快照，下标 = 录制版本的
    // 枚举顺序；两个版本的枚举只有 0–3 保证相同（旧 Perfect = 新 PerfectMinus），
    // 4 起整体错位（旧 LatePerfect=4 在 r150 上是 XPerfect）。读取端必须先
    // 重排到**当前枚举**，否则升级游戏后看旧回放：判定分布、重算精准度、
    // 跨口径行全错位——文件里存的 PercentXacc 反而是那时唯一正确的数。
    //
    // 录制版本从文件自带的 SpVersion（Patch_Lifecycle 写入 "r149" 这样的
    // build 号）读；SpVersion 是后补字段，早期文件为空——此时按桶数判别：
    // hitMarginsCount 恒为 max(枚举值)+1 全长，两版本长度天然不同（12 vs 16），
    // 与录制版本一一对应，比"猜"可靠。都判不出才保守地按当前布局解释。
    //
    // 重排按名映射到当前枚举：旧 Perfect 在新版由 PerfectMinus 顶替（三档
    // 拆分不可恢复，兼容层口径本就把三档并回一个槽，见 FilterName）；新版
    // 的三档与 Midspin 落在旧枚举上并回 Perfect 桶（2026-11-14 用户裁定：
    // 3.3.1 把中旋记成 Perfect）；FailedFloor 落在旧枚举上无处安放（旧版
    // 漏砖不是判定档）只能丢弃并计入日志——deadTiles 项随之丢失，是旧枚举
    // 表达力的硬限制，不是口径选择。
    internal const int XPerfectSinceBuild = 150;

    private static readonly string[] LegacyLayout =
    {
        "TooEarly", "VeryEarly", "EarlyPerfect", "Perfect", "LatePerfect", "VeryLate",
        "TooLate", "Multipress", "FailMiss", "FailOverload", "Auto", "OverPress",
    };

    private static readonly string[] ModernLayout =
    {
        "TooEarly", "VeryEarly", "EarlyPerfect", "PerfectMinus", "XPerfect", "PerfectPlus",
        "LatePerfect", "VeryLate", "TooLate", "Multipress", "FailMiss", "FailOverload",
        "Auto", "OverPress", "Midspin", "FailedFloor",
    };

    // 录制布局时代（SpVersion/桶数判别用）：Legacy = r141–148 布局、Modern = r150+ 布局。
    internal enum AccEra { Legacy, Modern }

    /// <summary>
    /// 文件里的判定分布按哪个布局解释。SpVersion（"r149"）优先；缺失时按
    /// 桶数判别；都判不出返回 null（调用方按当前布局解释，不重排）。
    /// </summary>
    internal static AccEra? DistributionEra(int[] counts, string spVersion)
    {
        if (!string.IsNullOrEmpty(spVersion))
        {
            string digits = spVersion.Trim();
            if (digits.StartsWith("r", StringComparison.OrdinalIgnoreCase))
                digits = digits.Substring(1);
            if (int.TryParse(digits, out int build) && build > 0)
                return build >= XPerfectSinceBuild ? AccEra.Modern : AccEra.Legacy;
        }
        if (counts != null)
        {
            if (counts.Length == LegacyLayout.Length)
                return AccEra.Legacy;
            if (counts.Length >= ModernLayout.Length)
                return AccEra.Modern;
        }
        return null;
    }

    /// <summary>
    /// 把录制版布局的判定分布重排到**当前枚举**。录制口径 == 当前口径（或判
    /// 不出版本）时原样返回；重排发生时记一条日志（铁律 1：看得见）。
    /// </summary>
    internal static int[] RemapJudgments(int[] counts, string spVersion)
    {
        if (counts == null)
            return counts;
        AccEra? era = DistributionEra(counts, spVersion);
        // 录制布局 == 当前布局（Modern ⇔ HasXPerfect）时不重排。
        if (era == null || era == (HasXPerfect ? AccEra.Modern : AccEra.Legacy))
            return counts;

        string[] layout = era == AccEra.Legacy ? LegacyLayout : ModernLayout;
        int size = 0;
        foreach (HitMargin v in Enum.GetValues(typeof(HitMargin)))
            if ((int)v + 1 > size) size = (int)v + 1;
        var remapped = new int[size];
        int dropped = 0;
        for (int i = 0; i < layout.Length && i < counts.Length; i++)
        {
            string name = layout[i];
            HitMargin target = Resolve(name, Absent);
            if (target == Absent && name == "Perfect")
                target = Resolve("PerfectMinus", Absent);   // 旧 Perfect → 新 PerfectMinus
            // 落在旧枚举上的新版档位并回旧桶（2026-11-14 用户裁定：3.3.1 把
            // 中旋记成 Perfect）：三档 → Perfect；Midspin → Perfect。
            if (target == Absent && (name == "PerfectMinus" || name == "XPerfect" || name == "PerfectPlus" || name == "Midspin"))
                target = Resolve("Perfect", Absent);
            if (target == Absent)
            {
                dropped += counts[i];
                continue;
            }
            remapped[(int)target] += counts[i];
        }

        Debug.Log("[SpectreReplay] 判定分布重排：录制 "
            + (era == AccEra.Legacy ? "≤r149（" + layout.Length + " 桶）" : "r150+（" + layout.Length + " 桶）")
            + " → 当前 r" + SpectreReplay.PatchShared.GameVersion.Release
            + (dropped > 0 ? "，" + dropped + " 个无对应档位命中丢弃" : ""));
        return remapped;
    }
}
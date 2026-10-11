using SimpleLyrics.Core.Lyrics;

namespace SimpleLyrics.Core.Editing;

/// <summary>一行歌词要落到哪个时刻。</summary>
/// <param name="Line">被改动的行。</param>
/// <param name="Start">它想去的时间码（应当已经是 <see cref="LrcTimecode.Quantize"/> 过的）。</param>
public readonly record struct LyricTimeTarget(LyricLine Line, TimeSpan Start);

/// <summary>一次时间轴改动的结果，供状态栏说明发生了什么。</summary>
/// <param name="Changed">时间真的变了的行数。</param>
/// <param name="Nudged">其中因为与别的歌词挨得太近而被错开的行数。</param>
/// <param name="LargestNudge">错开的最大幅度。</param>
public readonly record struct LyricEditReport(int Changed, int Nudged, TimeSpan LargestNudge);

/// <summary>
/// 一次改动算好之后的完整结果：要改成什么，以及这次改动有多「难」。
/// </summary>
/// <param name="Changes">
/// 逐行的前后值，可以直接交给 <see cref="TimeShiftCommand"/> 变成一步可撤销的改动。
/// 只包含<b>真的变了</b>的行。
/// </param>
/// <param name="Report">统计，供状态栏使用。</param>
public readonly record struct LyricEditPlan(IReadOnlyList<LyricTimeChange> Changes, LyricEditReport Report);

/// <summary>
/// 时间轴上的「最小间隔」：同一处不允许挤进两条歌词。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个间隔为什么必须存在。</b>显示层在相邻两条标识线的间距小于
/// <c>LyricTrackView.LabelMinGap</c>（22 像素）时不再逐条画线，改用一个色块盖住。
/// 于是两条歌词一旦靠得足够近，就<b>再也没有办法分开</b>：拉到最大放大也还是一块色斑，
/// 用户既看不见其中一条，也点不中它——那一条就此从编辑界面上消失了。间隔约束把这种
/// 「时间轴上存在、界面上不存在」的行挡在门外。
/// </para>
/// <para>
/// <b>为什么取「最大放大」那一档。</b>间距是 <c>Δt × 每秒像素数</c>，缩放越大越宽松，
/// 所以两条歌词最容易粘上的时刻是<b>放到最大时</b>——再往外就没有余地了。于是
/// 「最大放大下仍不合并」就是这个显示层能分辨的最小时间差，也就是整个工具的<b>时间分辨率下限</b>。
/// 比它更近的两条歌词，无论怎么缩放都分不开。
/// </para>
/// <para>
/// <b>落在时间码格子上。</b>LRC 只存得到 10 毫秒（<see cref="LrcTimecode"/>），
/// 于是约束也要按整格来算：需要 15.96 毫秒，那就得占满 2 格（20 毫秒）。
/// 若只按实数判断是否够 15.96 毫秒，写进文件后取整还可能把两条挤到同一格上，
/// 屏幕上「刚刚分开」的两条就又粘上了。
/// </para>
/// <para>
/// <b>只挪动刚被拖过来的那些行。</b>别的行是用户先前的工作成果，一次拖动不该顺手把它们
/// 也挪走——那会让「我明明只动了一句」变成满屏都在动。所以固定行是既定事实，
/// 只有落到它们附近的、本次被拖动的行会让位。
/// </para>
/// </remarks>
public static class LyricTimeline
{
    /// <summary>最小间隔相当于几格时间码（至少一格）。</summary>
    public static long MinimumUnits(TimeSpan minimumSeparation)
    {
        var ticksPerUnit = TimeSpan.TicksPerSecond / LrcTimecode.UnitsPerSecond;

        // 整数向上取整，不走 double：0.02 秒在二进制下是 0.0200000000000000004，
        // 乘 100 再向上取整会得到 3 格而不是 2 格。ticks 是精确整数，没有这个问题。
        var ticks = Math.Abs(minimumSeparation.Ticks);
        return Math.Max(1, (ticks + ticksPerUnit - 1) / ticksPerUnit);
    }

    /// <summary>
    /// 算出每一行最终的时刻，<b>不改动任何行</b>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 拖动过程中显示层每帧都要问一遍「这么放会不会撞上别人」，所以判定与执行必须是
    /// <b>同一份实现</b>：调用方拿这里的结果与原目标比一比，就知道会不会被错开。
    /// 显示层若自己再写一套「够不够近」的判断，屏幕上的预警与实际落点迟早会分家。
    /// </para>
    /// <para>
    /// 顺序：把 <paramref name="targets"/> 按目标时刻从早到晚放，先到的占住位置，
    /// 后到的绕开它。每次绕开都取<b>离目标最近的那个空位</b>，于是错开幅度最小；
    /// 等距时按这一行原本的先后决定往哪边让，尽量不打乱用户心里的顺序。
    /// </para>
    /// </remarks>
    /// <param name="targets">本次要改动的行与它们的目标时刻。</param>
    /// <param name="allLines">全部已打轴的行，用来知道哪些位置已经被占住。</param>
    /// <param name="minimumSeparation">两条歌词之间至少留出的时间。</param>
    /// <returns>与 <paramref name="targets"/> 一一对应的最终时刻。</returns>
    public static TimeSpan[] Resolve(
        IReadOnlyList<LyricTimeTarget> targets,
        IReadOnlyList<LyricLine> allLines,
        TimeSpan minimumSeparation)
    {
        var result = new TimeSpan[targets.Count];
        if (targets.Count == 0) return result;

        var steps = MinimumUnits(minimumSeparation);

        var moving = new HashSet<LyricLine>(targets.Count);
        foreach (var target in targets) moving.Add(target.Line);

        // 不动的那些行：它们是「别人必须绕开」的既定事实。
        var occupied = new List<long>(allLines.Count);
        foreach (var line in allLines)
        {
            if (line.Start is not { } start || moving.Contains(line)) continue;
            occupied.Add(LrcTimecode.ToUnits(start));
        }

        occupied.Sort();

        // 按目标时刻从早到晚处理。Array.Sort 不稳定，所以用下标兜住同刻的先后。
        var order = new int[targets.Count];
        for (var index = 0; index < order.Length; index++) order[index] = index;

        Array.Sort(order, (left, right) =>
        {
            var comparison = LrcTimecode.ToUnits(targets[left].Start)
                .CompareTo(LrcTimecode.ToUnits(targets[right].Start));

            return comparison != 0 ? comparison : left.CompareTo(right);
        });

        foreach (var index in order)
        {
            var target = targets[index];

            // 音频之前没有内容可言，而负时刻连 LRC 都写不出来（解析端不接受负号），
            // 所以 0 秒是硬下界：拖到更早的位置就停在 0。
            var wanted = Math.Max(LrcTimecode.ToUnits(target.Start), 0);
            var original = LrcTimecode.ToUnits(target.Line.Start ?? TimeSpan.Zero);

            var place = NearestFree(wanted, occupied, steps, original);
            result[index] = LrcTimecode.FromUnits(place);

            var position = occupied.BinarySearch(place);
            occupied.Insert(position < 0 ? ~position : position, place);
        }

        return result;
    }

    /// <summary>
    /// 算出一行行最终的时刻，并给出「从哪儿变到哪儿」的清单；<b>不改动任何行</b>。
    /// </summary>
    /// <remarks>
    /// 刻意把「算」与「落」分开：算出结果之后要先把这份前后值做成一步可撤销的改动
    /// （<see cref="TimeShiftCommand"/>），再由它一次性落下去。若这里直接改数据，
    /// 撤销就只能靠重新算一遍，而重算要依赖「别的东西都没动过」这个未必成立的前提。
    /// </remarks>
    public static LyricEditPlan Plan(
        IReadOnlyList<LyricTimeTarget> targets,
        IReadOnlyList<LyricLine> allLines,
        TimeSpan minimumSeparation)
    {
        var resolved = Resolve(targets, allLines, minimumSeparation);

        var changes = new List<LyricTimeChange>(targets.Count);
        int nudged = 0;
        var largest = TimeSpan.Zero;

        for (var index = 0; index < targets.Count; index++)
        {
            var line = targets[index].Line;
            var wanted = LrcTimecode.Quantize(targets[index].Start);
            var final = resolved[index];

            if (final != wanted)
            {
                nudged++;

                var shift = (final - wanted).Duration();
                if (shift > largest) largest = shift;
            }

            // 没挪动的行不进清单：一次毫无变化的拖动不该占用一步撤销，
            // 否则按一次 Ctrl+Z 会看起来毫无反应。
            if (line.Start == final) continue;

            changes.Add(new LyricTimeChange(line, line.Start, final));
        }

        return new LyricEditPlan(changes, new LyricEditReport(changes.Count, nudged, largest));
    }

    /// <summary>
    /// 离 <paramref name="wanted"/> 最近的、与 <paramref name="occupied"/> 里每一个都至少隔
    /// <paramref name="steps"/> 格的位置。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每个已占位置两侧各 <paramref name="steps"/> 格是禁区；若干个禁区叠在一起就成了一段段
    /// 连续的禁地。落点只可能在包含 <paramref name="wanted"/> 的那段禁地的<b>两个端点</b>上——
    /// 端点恰好离源位置 <c>steps</c> 格，正合要求，而再往里就侵入了别人的禁区。
    /// 于是「最近」只需比较这两个端点。
    /// </para>
    /// <para>
    /// <b>两端的合并必须两个方向都做。</b>这里踩过一次：最初以为「向右收只抬高右端、
    /// 向左收只压低左端」，于是只把左边的条目并进左端、右边的并进右端。但一个位于
    /// <c>wanted</c> 左边的邻人，它的禁区同样向右铺开、同样盖住 <c>wanted</c>，
    /// 右端必须被它抬高；反之亦然。漏掉这一半的表现是「错开之后仍然重叠」——
    /// 而那正是这条约束唯一要防的事，且不会崩，只会悄悄发生。
    /// </para>
    /// <para>
    /// 端点相同时（正好落在别人头上）按这一行<b>原本</b>的先后决定往哪边让：
    /// 本来在前的往前错、本来在后的往后错，顺序因此不会因为一次拖动而颠倒。
    /// </para>
    /// </remarks>
    private static long NearestFree(long wanted, List<long> occupied, long steps, long original)
    {
        if (occupied.Count == 0) return wanted;

        var index = occupied.BinarySearch(wanted);
        if (index < 0) index = ~index;

        long low = wanted, high = wanted;
        long nearest = long.MinValue;
        var nearestDistance = long.MaxValue;

        var left = index - 1;
        var right = index;

        // 先向右收一遍。
        while (right < occupied.Count && occupied[right] - steps <= high)
        {
            low = Math.Min(low, occupied[right] - steps);
            high = Math.Max(high, occupied[right] + steps);

            Track(occupied[right], wanted, ref nearest, ref nearestDistance);
            right++;
        }

        // 再向左收。向左收会抬高右端，于是右边可能又冒出几个刚刚还不相交的条目，
        // 得回头把它们一起收进来——收到不再有变化为止。
        while (left >= 0 && occupied[left] + steps >= low)
        {
            low = Math.Min(low, occupied[left] - steps);
            var raised = Math.Max(high, occupied[left] + steps);

            Track(occupied[left], wanted, ref nearest, ref nearestDistance);
            left--;

            if (raised <= high) continue;

            high = raised;

            while (right < occupied.Count && occupied[right] - steps <= high)
            {
                low = Math.Min(low, occupied[right] - steps);
                high = Math.Max(high, occupied[right] + steps);

                Track(occupied[right], wanted, ref nearest, ref nearestDistance);
                right++;
            }
        }

        // 两端都还停在原处，说明谁都不挨着。
        if (low == high) return wanted;

        // 往早挪会挪到音频之前，那就只能往晚挪。
        if (low < 0) return high;

        var earlier = wanted - low;
        var later = high - wanted;

        if (earlier < later) return low;
        if (later < earlier) return high;

        return original <= nearest ? low : high;
    }

    private static void Track(long value, long wanted, ref long nearest, ref long nearestDistance)
    {
        var distance = Math.Abs(value - wanted);
        if (distance >= nearestDistance) return;

        nearest = value;
        nearestDistance = distance;
    }
}

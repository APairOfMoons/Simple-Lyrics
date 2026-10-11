using System.Runtime.InteropServices;

namespace SimpleLyrics.Core.Audio;

/// <summary>
/// 物理内存探测，用来决定 PCM 解码缓存能占多大。
/// </summary>
/// <remarks>
/// <para>
/// 直接调 <c>GlobalMemoryStatusEx</c>，不走 <c>System.Management</c> 的 WMI 路径：
/// 后者要额外引入一个 NuGet 包并做一次 COM 初始化，而这里只需要两个整数。
/// </para>
/// <para>
/// <b>返回值是瞬时量</b>。系统可用内存随时在变，所以调用方应当在<b>每次准备继续扩大缓存之前</b>
/// 重新探测，而不是在进程启动时算一次就长期沿用——那会把上限钉在一个早已过期的快照上。
/// </para>
/// </remarks>
public static partial class SystemMemory
{
    /// <summary>缓存预算占「当前可用物理内存」的比例。</summary>
    private const double BudgetFraction = 0.25;

    /// <summary>预算下限。低于这个量缓存就没法用了，宁可多占一点。</summary>
    private const long MinimumBudgetBytes = 128L * 1024 * 1024;

    /// <summary>预算上限。常驻内存超过 1.5 GiB，对一个打轴软件来说不成体统。</summary>
    private const long MaximumBudgetBytes = 1536L * 1024 * 1024;

    /// <summary>当前可用的物理内存字节数；探测失败返回 0。</summary>
    public static long AvailablePhysicalBytes => Read().AvailablePhysical;

    /// <summary>物理内存总字节数；探测失败返回 0。</summary>
    public static long TotalPhysicalBytes => Read().TotalPhysical;

    /// <summary>
    /// 当前允许 PCM 缓存占用的字节数。
    /// </summary>
    /// <remarks>
    /// 探测失败时退回<b>下限</b>而不是 0——探不到内存不代表不该缓存，
    /// 返回 0 会让播放功能直接失去可用性，那是拿一个无关紧要的失败换了一个致命后果。
    /// </remarks>
    public static long CacheBudget()
    {
        var available = AvailablePhysicalBytes;
        if (available <= 0) return MinimumBudgetBytes;

        return Math.Clamp((long)(available * BudgetFraction), MinimumBudgetBytes, MaximumBudgetBytes);
    }

    private static (long TotalPhysical, long AvailablePhysical) Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);

        // 截到 long 能表示的范围。本机物理内存不可能触及这个上限，
        // 但如果结构体布局与系统预期不符，读出来的会是垃圾值，这时宁可给 0。
        const ulong ceiling = long.MaxValue;
        if (status.TotalPhysical > ceiling || status.AvailablePhysical > ceiling) return (0, 0);

        return ((long)status.TotalPhysical, (long)status.AvailablePhysical);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// <c>MEMORYSTATUSEX</c>。字段顺序与长度必须与 Win32 头文件完全一致，
    /// <see cref="Length"/> 也要填对，否则调用会直接失败。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

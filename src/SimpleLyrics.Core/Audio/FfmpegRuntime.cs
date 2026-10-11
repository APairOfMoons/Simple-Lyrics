using FFmpeg.AutoGen;

namespace SimpleLyrics.Core.Audio;

/// <summary>
/// FFmpeg 本机共享库的定位与一次性初始化。
/// </summary>
/// <remarks>
/// <para>
/// 本类必须在**任何** FFmpeg 调用之前执行，因为 <c>ffmpeg.RootPath</c> 只在首次
/// 触发本机加载时生效，之后修改不会重新加载。
/// </para>
/// <para>
/// 找到的目录需含 <c>avcodec-*.dll</c>。本方案只需 4 个 DLL 即可自足
/// （avutil → swresample → avcodec → avformat），不含 avfilter / avdevice / swscale。
/// </para>
/// </remarks>
public static class FfmpegRuntime
{
    private static readonly object Gate = new();

    private static string? _libraryPath;
    private static bool _initialized;

    /// <summary>已加载的库目录；未初始化时为 <c>null</c>。</summary>
    public static string? LibraryPath
    {
        get { lock (Gate) return _libraryPath; }
    }

    /// <summary>FFmpeg 是否已成功初始化。</summary>
    public static bool IsInitialized
    {
        get { lock (Gate) return _initialized; }
    }

    /// <summary>
    /// 确保 FFmpeg 已初始化；重复调用无副作用。
    /// </summary>
    /// <param name="searchRoot">
    /// 可选的查找起点，通常是打开的音频文件所在目录——
    /// 便于在任意位置运行未发布的构建。
    /// </param>
    /// <exception cref="FileNotFoundException">找不到共享库，或库无法加载。</exception>
    public static void EnsureInitialized(string? searchRoot = null)
    {
        lock (Gate)
        {
            if (_initialized) return;

            var path = Locate(searchRoot)
                ?? throw new FileNotFoundException(
                    "未找到 FFmpeg 共享库。需要一个含 avcodec-*.dll 的目录，查找顺序为：" +
                    "环境变量 SL_FFMPEG_BIN → 程序目录下的 ffmpeg\\ → " +
                    "查找起点（音频文件所在目录）下的 third_party\\ffmpeg\\bin → " +
                    "从程序目录逐级向上找到的仓库根下的 third_party\\ffmpeg\\bin。");

            // RootPath 必须在首次本机调用前赋值，因此这里先设再触发加载。
            ffmpeg.RootPath = path;

            try
            {
                // 压掉 ADTS 时长估算、时间戳跳过之类的告警噪音。
                // 产品化后应改用 av_log_set_callback 转发到自己的日志系统，而不是丢弃。
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            }
            catch (Exception ex)
            {
                throw new FileNotFoundException(
                    $"FFmpeg 共享库无法加载（目录：{path}）。常见原因：位数不匹配（需 x64）、" +
                    $"缺少依赖 DLL、或文件被安全软件拦截。原始错误：{ex.GetType().Name}: {ex.Message}",
                    ex);
            }

            _libraryPath = path;
            _initialized = true;
        }
    }

    /// <summary>运行时版本串，如 <c>N-126855-g6246f7a4cd</c>；未初始化时返回 <c>null</c>。</summary>
    public static string? RuntimeVersion
    {
        get
        {
            if (!IsInitialized) return null;
            try { return ffmpeg.av_version_info(); }
            catch { return null; }
        }
    }

    /// <summary>绑定与运行时的主版本是否一致——不一致则 ABI 无保证。</summary>
    public static bool IsAbiCompatible
    {
        get
        {
            if (!IsInitialized) return false;
            try
            {
                return (ffmpeg.avcodec_version() >> 16) == ffmpeg.LIBAVCODEC_VERSION_MAJOR
                    && (ffmpeg.avutil_version() >> 16) == ffmpeg.LIBAVUTIL_VERSION_MAJOR;
            }
            catch { return false; }
        }
    }

    private static string? Locate(string? searchRoot)
    {
        // 1) 显式覆盖，便于调试或使用系统安装的 FFmpeg。
        var fromEnvironment = Environment.GetEnvironmentVariable("SL_FFMPEG_BIN");
        if (LooksLikeLibraryDirectory(fromEnvironment)) return fromEnvironment;

        // 2) 发布形态：DLL 被拷到 exe 旁边的 ffmpeg\ 目录。
        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (LooksLikeLibraryDirectory(beside)) return beside;

        // 3) 调用方给的起点及其各级祖先。
        //    音频文件常与仓库根隔着几层（例如统一放在 samples/ 子目录），
        //    所以这里也逐级向上找，而不是只看起点本身。
        if (!string.IsNullOrWhiteSpace(searchRoot))
        {
            for (var dir = new DirectoryInfo(searchRoot); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "third_party", "ffmpeg", "bin");
                if (LooksLikeLibraryDirectory(candidate)) return candidate;
            }
        }

        // 4) 开发形态：从输出目录逐级向上回溯到仓库根。
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "third_party", "ffmpeg", "bin");
            if (LooksLikeLibraryDirectory(candidate)) return candidate;
        }

        return null;
    }

    private static bool LooksLikeLibraryDirectory(string? directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && Directory.Exists(directory)
        && Directory.EnumerateFiles(directory, "avcodec-*.dll").Any();
}

namespace SimpleLyrics.Probe;

/// <summary>
/// P0 验证素材的定位。素材可能放在仓库根，也可能放在 <c>samples/</c> 子目录，
/// 这里统一解析，免得每个探针各写一遍。
/// </summary>
internal static class TestAudio
{
    private static readonly string[] SearchDirectories = [".", "samples"];

    /// <summary>返回首个存在的候选路径；都不存在时返回仓库根下的首选路径，由调用方报告。</summary>
    public static string Resolve(string repositoryRoot, string fileName)
    {
        var fallback = Path.Combine(repositoryRoot, fileName);

        foreach (var directory in SearchDirectories)
        {
            var candidate = Path.Combine(repositoryRoot, directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return fallback;
    }
}

namespace LongShot;

/// <summary>
/// 启动诊断日志。用户报「打不开 / 卡住」时，这个文件就是唯一的现场。
///
/// 落点很关键：以前固定写 %TEMP%，用户根本不知道该去哪儿找，而且临时目录可能被
/// 重定向、被安全软件清理，出问题的痕迹说没就没。现在优先写 exe 旁边——单文件发布下
/// 那就是用户双击 exe 的那个目录，一眼可见；只有目录确实写不进去
/// （Program Files、只读介质、被只读策略保护）才退回 %TEMP%。
/// </summary>
internal static class StartLog
{
    private const long MaxBytes = 512 * 1024;

    /// <summary>日志落点。进程启动时确定一次，之后不再变。</summary>
    public static string FilePath { get; } = PickFilePath();

    private static string PickFilePath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "LongShot-startup.log");

        try
        {
            // 建得出来不代表写得进去，所以真写一次再认
            File.AppendAllText(beside, string.Empty);
            return beside;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "LongShot-startup.log");
        }
    }

    public static void Write(string message)
    {
        try
        {
            // 每次启动都追加，长期用下来别让它无限长大（超上限就重开一份）
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes) File.Delete(FilePath);

            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去绝不能影响启动
        }
    }

    /// <summary>
    /// 从进程创建到现在过了多久。包含 .NET 运行时启动，才是用户真正等待的时间；
    /// 程序内部自己掐表只能量到窗口构造那一段，会漏掉最可疑的部分。
    /// </summary>
    public static string SinceProcessStart()
    {
        try
        {
            var elapsed = DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime;
            return $"{elapsed.TotalMilliseconds:F0}ms";
        }
        catch
        {
            return "n/a";
        }
    }
}

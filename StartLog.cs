namespace LongShot;

/// <summary>
/// 启动诊断日志。写到临时目录，用户遇到「打不开 / 卡住」时可以直接把这个文件发出来定位。
/// 每次都追加、只记关键节点（一次启动十来行），不做轮转——真出问题时它自己就是证据。
/// </summary>
internal static class StartLog
{
    public static string FilePath { get; } = Path.Combine(
        Path.GetTempPath(), "LongShot-startup.log");

    public static void Write(string message)
    {
        try
        {
            // 每次启动都追加，长期用下来别让它无限长大（超上限就重开一份）
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 512 * 1024) File.Delete(FilePath);

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

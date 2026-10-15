namespace LongShot;

/// <summary>
/// 启动诊断日志。用户报「打不开 / 卡住」时，这个文件就是唯一的现场。
///
/// 落点试过几个极端，都不合适：
///   - 写 %TEMP%：用户根本不知道该去哪儿找，而且临时目录可能被重定向、被安全软件清理，
///     出问题的痕迹说没就没。
///   - 写 exe 旁边：确实一眼可见，但 exe 常被放在桌面或下载目录，等于往用户眼皮底下
///     扔杂物（实测被用户当场发现）。
/// 现在改为「本地应用数据优先，逐级兜底」：正常机器上落在
/// %LOCALAPPDATA%\LongShot\，与热键配置同一个套路，不碰任何 exe 所在目录。
/// </summary>
internal static class StartLog
{
    private const long MaxBytes = 512 * 1024;

    /// <summary>日志目录。进程启动时确定一次，之后不再变。</summary>
    public static string Folder { get; } = PickFolder();

    /// <summary>启动日志落点。</summary>
    public static string FilePath { get; } = Path.Combine(Folder, "LongShot-startup.log");

    /// <summary>
    /// 按优先级挑第一个**确实可写**的目录。
    ///
    /// 为什么要逐级兜底：极端受限的机器（企业策略锁掉 AppData、受限账户）里，
    /// 前两个候选都会失败，此时若直接退到临时目录，日志会被清理机制吃掉，
    /// 于是「打不开」重新变成查无实据。宁可最后在 exe 旁边留一个文件。
    /// 正常机器不会走到那一步——只要 %LOCALAPPDATA% 可写就停在那儿。
    /// </summary>
    private static string PickFolder()
    {
        var candidates = new List<string>(4);

        // 取不到路径时 GetFolderPath 会返回空串，Path.Combine("", "x") 会变成相对路径，
        // 那会把日志写到当前工作目录里去——必须显式挡掉。
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "LongShot"));

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming)) candidates.Add(Path.Combine(roaming, "LongShot"));

        candidates.Add(AppContext.BaseDirectory);

        foreach (var folder in candidates)
        {
            try
            {
                Directory.CreateDirectory(folder);

                // 建得出来不代表写得进去，所以真写一次再认
                var probe = Path.Combine(folder, ".writable");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);

                return folder;
            }
            catch
            {
                // 换下一个候选
            }
        }

        return Path.GetTempPath();
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

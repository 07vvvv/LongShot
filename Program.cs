using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LongShot;

internal static class Program
{
    private const int SwHide = 0;
    private const int SwRestore = 9;

    private static Mutex? _instanceMutex;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hWnd, bool invert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [STAThread]
    private static void Main(string[] args)
    {
        // 最早的兜底，必须放在第一行：后面的 Application.* 初始化自己也会失败
        // （单文件发布要先把 native 库解压到临时目录，解压不了就根本起不来），
        // 那种情况下以前连一个异常处理器都还没注册，用户只能看到「双击了但什么都没发生」。
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception, uiThread: false);

        var mode = args.FirstOrDefault()?.ToLowerInvariant();

        StartLog.Write($"=== 启动 pid={Environment.ProcessId} 模式={mode ?? "ui"} 进程创建至今 {StartLog.SinceProcessStart()}");
        StartLog.Write($"启动环境 {DescribeLaunchEnvironment()}");

        // 自检模式允许并发跑，不参与单实例检测
        if (mode is null && !ClaimSingleInstance())
        {
            StartLog.Write("单实例：已有实例，本次退出");
            return;
        }

        if (mode is not null)
        {
            // 自检模式：把输出镜像到 diag.log。
            // WinExe 不再建立 stdout，自动化验证靠读这个文件；从终端直接跑时窗口里也能看到。
            MirrorDiagnosticsToFile();
        }
        else
        {
            // 保险：万一从仍带控制台的宿主启动，别让黑窗挂着
            var console = GetConsoleWindow();
            if (console != IntPtr.Zero) ShowWindow(console, SwHide);
        }

        try
        {
            InitWinForms();
        }
        catch (Exception ex)
        {
            // 这一段以前是零日志的黑洞：崩在这里时 diag.log 是空的、启动日志也没有记录，
            // 用户和排查的人都毫无线索。现在至少留下人话提示和现场。
            StartLog.Write($"!!! 界面初始化失败，程序无法启动：{ex}");
            ReportCrash(
                new InvalidOperationException(
                    "程序启动失败：界面初始化没能完成。\n\n" +
                    $"详细信息已记录到：\n{StartLog.FilePath}\n\n" +
                    "若用的是单文件绿色版，通常是临时目录不可写或被杀毒软件拦截了解压——" +
                    "把程序换个目录（例如 D 盘）再试。",
                    ex),
                uiThread: true);
            return;
        }

        switch (mode)
        {
            case "--probe":
                ProbeRunner.Run();
                return;
            case "--selftest":
                SelftestRunner.Run();
                return;
            case "--uitest":
                UiSmokeRunner.Run();
                return;
            case "--stitchtest":
                StitchTestRunner.Run();
                return;
            case "--grabtest":
                GrabTestRunner.Run();
                return;
            case "--overlaytest":
                OverlayTestRunner.Run();
                return;
            case "--uishot":
                UiShotRunner.Run();
                return;
            case "--aligntest":
                AlignTestRunner.Run();
                return;
            case "--startuptest":
                StartupTestRunner.Run();
                return;
            case "--makeicon":
                IconMaker.Run();
                return;
            case "--pathtest":
                PathTestRunner.Run();
                return;
            case "--logprobe":
                LogPathProbe();
                return;
            default:
                Application.Run(new SettingsForm());
                return;
        }
    }

    /// <summary>
    /// 诊断用：「日志写不出来」时没有任何输出可看，只能靠它把真实路径和失败原因逼出来。
    /// 结果写到 exe 旁边的 _logprobe.txt（那是唯一已知在受限环境里也能写的位置）。
    /// </summary>
    private static void LogPathProbe()
    {
        var report = DescribeLogPaths();

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "_logprobe.txt"), report);
        }
        catch
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "_logprobe.txt"), report); }
            catch { /* 都写不了就只能放弃 */ }
        }
    }

    private static string DescribeLogPaths()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BaseDirectory    = {AppContext.BaseDirectory}");
        sb.AppendLine($"CurrentDirectory = {Environment.CurrentDirectory}");
        sb.AppendLine($"LocalAppData     = [{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}]");
        sb.AppendLine($"AppData          = [{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}]");
        sb.AppendLine($"GetTempPath      = [{Path.GetTempPath()}]");
        sb.AppendLine($"StartLog.Folder  = [{StartLog.Folder}]");
        sb.AppendLine($"StartLog.FilePath= [{StartLog.FilePath}]");
        sb.AppendLine();

        var roots = new (string Name, string Path)[]
        {
            ("LocalAppData\\LongShot", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LongShot")),
            ("AppData\\LongShot", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LongShot")),
            ("Temp", Path.GetTempPath()),
            ("exe 旁边", AppContext.BaseDirectory),
        };

        foreach (var (name, path) in roots)
        {
            try
            {
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ".probe");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                sb.AppendLine($"{name}: 完全可写  ({path})");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{name}: 失败 {ex.GetType().Name}: {ex.Message}  ({path})");
            }
        }

        sb.AppendLine();
        try
        {
            File.AppendAllText(StartLog.FilePath, string.Empty);
            sb.AppendLine("向 StartLog.FilePath 追加写入: 成功");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"向 StartLog.FilePath 追加写入: 失败 {ex.GetType().Name}: {ex.Message}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// WinForms 初始化。单独成方法只为一件事：让 <see cref="Main"/> 能把它整段包进 try，
    /// 否则这段一崩就是无日志、无提示的全黑。
    /// </summary>
    private static void InitWinForms()
    {
        // 必须最先调用，且在任何窗口创建之前。
        // 缺了它进程就是 DPI 不感知的：鼠标与窗口用逻辑坐标，而屏幕抓取用物理坐标，
        // 结果就是「框选的范围」和「实际截到的范围」对不上（125% 缩放下差 1.25 倍）。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // ⚠️ 这一行必须包起来。EnableVisualStyles 不是「装饰」那么简单：
        // 它会把一个临时清单文件写进 %TEMP% 再交给 Windows 解析（ThemingScope.
        // CreateActivationContext）。临时目录被安全软件锁住、被组策略限制、ACL 异常
        // 或者磁盘写满时，它会直接抛 UnauthorizedAccessException —— 而它以前是裸奔的，
        // 于是这类机器上程序 100% 起不来，用户只看到「双击了没反应」。
        // 外观回退成经典样式完全能用，绝不能让它决定程序能不能开。
        try
        {
            Application.EnableVisualStyles();
        }
        catch (Exception ex)
        {
            StartLog.Write($"!!! 视觉样式启用失败，界面回退为经典外观（功能不受影响）：{ex.Message}");
        }

        Application.SetCompatibleTextRenderingDefault(false);

        // 全局兜底：绝不让任何漏网异常变成「未经处理的异常」崩溃框。
        // 记录到启动日志，并给一句人话提示——程序继续跑，用户的截图不至于白截。
        // 必须在创建任何窗口之前调用，所以放在这里而不是构造函数里。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception, uiThread: true);
    }

    /// <summary>
    /// 把「这次是怎么被启动的」记全。
    ///
    /// 起因是用户报「桌面快捷方式启动打不开，进文件夹双击 exe 就正常」：
    /// 两者唯一的系统性差别就在这几个值上（工作目录不同、快捷方式可以塞参数、
    /// 快捷方式的「运行方式」会走 STARTUPINFO 覆盖窗口显示）。记下来，
    /// 下次再出现就能一眼看出是哪一种，而不必靠猜。
    /// </summary>
    private static string DescribeLaunchEnvironment()
    {
        var sb = new StringBuilder();

        sb.Append($"exe={Application.ExecutablePath}");
        sb.Append($" | 基础目录={AppContext.BaseDirectory}");
        sb.Append($" | 当前目录={Environment.CurrentDirectory}");

        var extra = Environment.GetCommandLineArgs().Skip(1).ToArray();
        sb.Append($" | 参数=[{string.Join(' ', extra)}]");

        sb.Append($" | 显示方式={DescribeShowWindow()}");

        return sb.ToString();
    }

    /// <summary>
    /// 读出 STARTUPINFO 里的显示方式。快捷方式的「运行方式：最小化」就是从这里传进来的，
    /// 而 Windows 会在进程**第一次** ShowWindow 时用它覆盖程序自己的请求——窗口于是缩进
    /// 任务栏不露面，用户只能判定为「打不开」。
    /// </summary>
    private static string DescribeShowWindow()
    {
        try
        {
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            GetStartupInfo(ref si);

            if ((si.dwFlags & StartfUseShowWindow) == 0) return "未指定（等同于正常窗口）";

            return si.wShowWindow switch
            {
                0 => "SW_HIDE（隐藏）",
                1 => "SW_SHOWNORMAL（正常）",
                2 => "SW_SHOWMINIMIZED（最小化）",
                3 => "SW_SHOWMAXIMIZED（最大化）",
                6 => "SW_MINIMIZE（最小化）",
                7 => "SW_SHOWMINNOACTIVE（最小化且不激活）",
                _ => $"未知（{si.wShowWindow}）",
            };
        }
        catch
        {
            return "n/a";
        }
    }

    private const int StartfUseShowWindow = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void GetStartupInfo(ref StartupInfo si);

    /// <summary>
    /// 兜底报告。UI 线程上的异常可以就地消化掉并继续运行；
    /// 非 UI 线程的异常拦不住进程终止，但至少把现场留在日志里。
    /// </summary>
    private static void ReportCrash(Exception? exception, bool uiThread)
    {
        StartLog.Write($"!!! 未处理异常（{(uiThread ? "UI" : "非 UI")}线程）：{exception}");

        if (!uiThread) return;

        try
        {
            MessageBox.Show(
                $"出错了：{exception?.Message}\n\n详细信息已记录到：\n{StartLog.FilePath}",
                "LongShot",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch
        {
            // 连提示都弹不出来就只能算了
        }
    }

    /// <summary>
    /// 单实例。核心原则：**宁可多开一个窗口，也绝不让用户打不开**。
    /// 之前的实现一旦找不到已有窗口就弹框退出，用户只会看到「卡一下、什么都没出现」——
    /// 而 Windows 又经常拒绝非前台进程的 SetForegroundWindow，旧窗口根本不会露头。
    /// </summary>
    private static bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(true, @"Local\LongShot.SingleInstance", out var created);
            if (created) return true;
        }
        catch
        {
            StartLog.Write("单实例：Mutex 创建失败，照常启动");
            return true;        // Mutex 本身出问题就照常启动，别把用户挡在门外
        }

        var existing = FindWindow(null, "LongShot 设置");

        // 有 Mutex 却没有窗口：多半是上一个实例没退干净。照常启动。
        if (existing == IntPtr.Zero)
        {
            StartLog.Write("单实例：Mutex 被占但找不到窗口，照常启动");
            return true;
        }

        StartLog.Write($"单实例：唤出已有窗口 0x{existing:X}");

        // 把已有窗口带到前台：Windows 会拒绝非前台进程的请求，所以多管齐下
        ShowWindow(existing, SwRestore);            // 可能被最小化了

        if (SetForegroundWindow(existing))
        {
            FlashWindow(existing, true);            // 任务栏闪烁，确保用户注意到
            return false;
        }

        // 抢不到前台就绝不硬拦：此时旧窗口可能被别的窗口严实盖住，用户看到的
        // 就是「点了没反应」——正是这个类要避免的情况。宁可多开一个窗口。
        // 但要闪一下任务栏，让用户知道「其实已经开着一个」，别以为是重复点了。
        FlashWindow(existing, true);
        StartLog.Write("单实例：已有窗口但抢不到前台，本次照常启动（宁可多开）");
        return true;
    }

    private static void MirrorDiagnosticsToFile()
    {
        try
        {
            // 带 BOM：否则读日志的一方会按系统 ANSI 解码，中文全成乱码
            var file = new StreamWriter(
                Path.Combine(StartLog.Folder, "diag.log"),
                append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = true,
            };

            Console.SetOut(new TeeWriter(Console.Out, file));
        }
        catch
        {
            // 连日志都写不了就放弃，不能因此挡住自检本身
        }
    }

    /// <summary>把输出同时写往控制台和文件。</summary>
    private sealed class TeeWriter(TextWriter console, TextWriter file) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            console.Write(value);
            file.Write(value);
        }

        public override void Write(string? value)
        {
            console.Write(value);
            file.Write(value);
        }

        public override void WriteLine(string? value)
        {
            console.WriteLine(value);
            file.WriteLine(value);
        }
    }
}

/// <summary>
/// 自检一：对一批候选组合逐个「试注册 → 立刻注销」，输出真实占用情况。
/// 用隐藏窗体当宿主，跑完即退，不改变系统状态。
/// </summary>
internal static class ProbeRunner
{
    private static readonly (string Name, HotkeySpec Spec)[] Candidates =
    [
        ("Ctrl+Alt+L", new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.L)),
        ("Ctrl+Alt+A", new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.A)),
        ("Ctrl+Alt+S", new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.S)),
        ("Ctrl+Alt+P", new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.P)),
        ("Ctrl+Shift+A", new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.A)),
        ("Ctrl+Shift+S", new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.S)),
        ("Win+Shift+S", new(HotkeyModifiers.Win | HotkeyModifiers.Shift, Keys.S)),
        ("Win+E", new(HotkeyModifiers.Win, Keys.E)),
        ("Win+R", new(HotkeyModifiers.Win, Keys.R)),
        ("Win+L", new(HotkeyModifiers.Win, Keys.L)),
        ("Win+Tab", new(HotkeyModifiers.Win, Keys.Tab)),
        ("Ctrl+Shift+Esc", new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.Escape)),
        ("Alt+Tab", new(HotkeyModifiers.Alt, Keys.Tab)),
        ("Ctrl+Alt+Del", new(HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.Delete)),
        ("PrintScreen（无修饰键）", new(HotkeyModifiers.None, Keys.PrintScreen)),
    ];

    public static void Run()
    {
        UseUtf8Console();

        var host = HiddenHost.Create();

        host.Shown += (_, _) =>
        {
            try
            {
                Dump(host.Handle);
            }
            finally
            {
                host.Close();
            }
        };

        Application.Run(host);
    }

    private static void Dump(IntPtr hwnd)
    {
        Console.WriteLine("=== 全局热键占用探测（试注册后立即注销，不改变系统状态）===");
        Console.WriteLine();

        foreach (var (name, spec) in Candidates)
        {
            var rule = HotkeyRules.Check(spec);
            string result;

            if (rule.Severity == CheckSeverity.Reject)
            {
                result = $"本地规则拒绝：{rule.Message}";
            }
            else if (HotkeyManager.TryProbe(hwnd, spec, out var err))
            {
                result = rule.Severity == CheckSeverity.Warn
                    ? "可注册（系统保留键，已警告）"
                    : "可注册（未被占用）";
            }
            else
            {
                result = $"不可用：{err}";
            }

            Console.WriteLine($"  {name,-24} {result}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 探测结束 ===");
    }

    internal static void UseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 某些宿主不允许改控制台编码，忽略
        }
    }
}

/// <summary>
/// 自检二：注册热键后分两段验证 ——
/// [1] 自投 WM_HOTKEY 验证消息处理链路（绕开系统）；
/// [2] SendInput 合成按键验证系统派发链路（这一步正是“试按确认”要覆盖的盲区）。
/// 合成输入本身受会话状态影响，因此带诊断输出与一次自动重试。
/// </summary>
internal static class SelftestRunner
{
    private const byte VkControl = 0x11;
    private const byte VkMenu = 0x12;   // Alt
    private const byte VkShift = 0x10;
    private const byte VkLWin = 0x5B;
    private const byte VkRWin = 0x5C;
    private const byte VkF9 = 0x78;

    private const uint KeyEventKeyUp = 0x0002;
    private const uint InputKeyboard = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // union 里必须放 MOUSEINPUT，否则 x64 下 INPUT 的 cbSize 会算成 32（正确是 40），SendInput 直接失败
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static void Run()
    {
        ProbeRunner.UseUtf8Console();

        var spec = new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.F9);
        var host = HiddenHost.Create();
        HotkeyManager? manager = null;
        var hits = 0;

        host.Shown += async (_, _) =>
        {
            manager = new HotkeyManager(host.Handle);
            host.Hotkeys = manager;
            manager.Pressed += _ => hits++;

            if (!manager.RegisterCurrent(spec, out var err))
            {
                Console.WriteLine($"SKIP：{spec.Display} 已被占用，无法自检（{err}）");
                host.Close();
                return;
            }

            Console.WriteLine($"=== 端到端自检：{spec.Display} ===");
            Console.WriteLine($"  INPUT 结构体大小 = {Marshal.SizeOf<INPUT>()}（x64 应为 40）");
            Console.WriteLine($"  注入前按下中的修饰键：{ModifierState()}");

            // 阶段 1：消息处理链路（自投 WM_HOTKEY，绕开系统）
            manager.SimulateCurrentPressed();
            await Task.Delay(250);
            Console.WriteLine(hits >= 1
                ? "  [1/2] PASS：消息处理链路正常（自投 WM_HOTKEY 已收到）"
                : "  [1/2] FAIL：自投 WM_HOTKEY 也没收到 —— 问题在消息处理链路");

            // 阶段 2：系统派发链路，最多试两次
            var triggered = false;
            for (var attempt = 1; attempt <= 2 && !triggered; attempt++)
            {
                ReleaseAllModifiers();

                var before = hits;
                var injected = Synthesize();
                var lastError = Marshal.GetLastWin32Error();

                await Task.Delay(1200);
                triggered = hits > before;

                Console.WriteLine($"  [2/2] 第 {attempt} 次注入 {injected}/6 个事件（错误码 {lastError}）" +
                                  $"→ {(triggered ? "触发成功" : "未触发")}，注入后修饰键：{ModifierState()}");
            }

            Console.WriteLine(triggered
                ? "  [2/2] PASS：合成按键触发了热键 —— 注册→系统派发→消息处理全链路通。"
                : "  [2/2] FAIL：按键已注入但未触发热键（试了两次）。这一环依赖会话输入状态，可能受远程会话/沙箱影响。");

            host.Close();
        };

        Application.Run(host);
        manager?.Dispose();
    }

    private static uint Synthesize()
    {
        // 对照实验：一次性注入全部 6 个事件
        var inputs = new[]
        {
            Key(VkControl, false),
            Key(VkMenu, false),
            Key(VkF9, false),
            Key(VkF9, true),
            Key(VkMenu, true),
            Key(VkControl, true),
        };

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>合成前先把可能卡住的修饰键全部抬起，避免上一次注入残留导致组合状态错乱。</summary>
    private static void ReleaseAllModifiers()
    {
        foreach (var vk in new ushort[] { VkControl, VkMenu, VkShift, VkLWin, VkRWin })
        {
            Emit(vk, true);
        }
    }

    private static uint Emit(ushort vk, bool keyUp)
    {
        var inputs = new[] { Key(vk, keyUp) };
        var injected = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        Thread.Sleep(40);
        return injected;
    }

    private static INPUT Key(ushort vk, bool keyUp) => new()
    {
        type = InputKeyboard,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KeyEventKeyUp : 0 } },
    };

    private static string ModifierState()
    {
        var pressed = new List<string>(4);
        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0) pressed.Add("Ctrl");
        if ((GetAsyncKeyState(VkMenu) & 0x8000) != 0) pressed.Add("Alt");
        if ((GetAsyncKeyState(VkShift) & 0x8000) != 0) pressed.Add("Shift");
        if ((GetAsyncKeyState(VkLWin) & 0x8000) != 0) pressed.Add("Win");
        return pressed.Count == 0 ? "(无)" : string.Join("+", pressed);
    }
}

/// <summary>
/// 自检三：构造真实的设置窗体跑一小段消息循环，验证 UI 路径（控件构造、句柄创建、
/// 热键管理器初始化、已保存热键的恢复）不抛异常。
/// </summary>
internal static class UiSmokeRunner
{
    public static void Run()
    {
        ProbeRunner.UseUtf8Console();

        var form = new SettingsForm();
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Console.WriteLine("=== UI 冒烟自检 ===");
            Console.WriteLine("  PASS：设置窗体构造、句柄创建、热键管理器初始化均无异常。");
            Console.WriteLine($"  当前热键 = {form.Hotkeys?.Current?.Display ?? "(未设置)"}");
            Console.WriteLine($"  配置文件 = {HotkeyConfig.FilePath}（存在：{File.Exists(HotkeyConfig.FilePath)}）");

            // 只构造不 Show：选区窗口是全屏置顶的，真的弹出来会盖住用户整个屏幕
            using var selector = new RegionSelectorForm();
            Console.WriteLine($"  PASS：选区窗口构造正常，覆盖范围 {selector.Bounds.Width}×{selector.Bounds.Height}（未显示）");

            CheckClearHotkey();

            form.Close();
        };

        form.Shown += (_, _) => timer.Start();
        Application.Run(form);
    }

    /// <summary>
    /// 验证「清除快捷键」。配置可能落在用户目录，也可能回退到 exe 旁边，
    /// 所以两处都先备份、测完再原样恢复。
    /// </summary>
    private static void CheckClearHotkey()
    {
        var main = HotkeyConfig.FilePath;
        var legacy = HotkeyConfig.LegacyPath;

        var mainBackup = File.Exists(main) ? File.ReadAllText(main) : null;
        var legacyBackup = File.Exists(legacy) ? File.ReadAllText(legacy) : null;

        try
        {
            var saved = new HotkeyConfig(HotkeyModifiers.Control, Keys.F9).Save();
            var where = HotkeyConfig.SavedTo;

            HotkeyConfig.Clear();
            var removed = where is null || !File.Exists(where);

            Console.WriteLine(saved && removed
                ? $"  PASS：清除快捷键可用（写入后能删除：{where}）"
                : $"  FAIL：清除快捷键异常（写入={saved}　路径={where}　删除={removed}）");
        }
        finally
        {
            HotkeyConfig.Clear();
            TryRestore(main, mainBackup);
            TryRestore(legacy, legacyBackup);
        }
    }

    private static void TryRestore(string path, string? content)
    {
        if (content is null) return;

        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(path, content);
        }
        catch
        {
            // 恢复不了也不能让自检崩掉
        }
    }
}

/// <summary>
/// 自检四：拼接算法。用「一张合成长图 + 已知偏移切出的两帧」验证配准是否算得准，
/// 覆盖正常偏移、无滚动、以及纯空白（无纹理）三种情况。纯逻辑，不依赖屏幕。
/// </summary>
internal static class StitchTestRunner
{
    public static void Run()
    {
        ProbeRunner.UseUtf8Console();
        Console.WriteLine("=== 拼接算法自检（合成图，偏移已知）===");

        var pass = 0;
        var total = 0;

        // 偏移必须小于帧高，否则两帧毫无重叠，配准在数学上就不可能。
        // 负数代表用户在往回（向上）滚。
        foreach (var expected in new[] { 37, 137, 241, -37, -137, -241 })
        {
            total++;
            var page = FakePage(200, 1400);
            var baseTop = expected < 0 ? 300 : 0;
            var prev = Slice(page, baseTop, 300);
            var next = Slice(page, baseTop + expected, 300);

            var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score)
                     && got == expected;
            var direction = expected < 0 ? "向上滚" : "向下滚";
            Console.WriteLine($"  [{direction}] 期望 dh={expected,4}　实测 dh={got,4}　score={score,6:F2}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            total++;
            var page = FakePage(200, 1400);
            var prev = Slice(page, 0, 300);
            var next = Slice(page, 0, 300);
            var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score)
                     && got == 0;
            Console.WriteLine($"  [未滚动  ] 期望 dh=   0　实测 dh={got,4}　score={score,6:F2}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            total++;
            var prev = Blank(200, 300);
            var next = Blank(200, 300);
            var trusted = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score);
            var ok = trusted && got == 0;
            Console.WriteLine($"  [纯空白  ] 应当可信且 dh=0　trusted={trusted}　dh={got}　score={score:F2}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            // 上一步滚了一大段、这一步只是小幅微调：位移和上一步完全不在一个量级，也得算准。
            // 这正是不用「上一步步长」缩小搜索范围的原因（两个方向都要覆盖）。
            total++;
            var page = FakePage(200, 1400);
            var prev = Slice(page, 300, 300);
            var next = Slice(page, 280, 300);               // next 内容比 prev 靠上 = 向上滚 20
            var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score)
                     && got == -20;
            Console.WriteLine($"  [步长突变] 上一步 200、这一步向上只滚 20　实测 dh={got,4}　score={score,6:F2}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            total++;
            var page = FakePage(200, 1400);
            var prev = Slice(page, 300, 300);
            var next = Slice(page, 320, 300);               // 向下滚 20
            var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score)
                     && got == 20;
            Console.WriteLine($"  [步长突变] 上一步 200、这一步向下只滚 20　实测 dh={got,4}　score={score,6:F2}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            // 累积模型：向下滚追加、向上滚前插、往回滚不重复，且拼出来的内容要和原页面逐行对上
            total++;
            var page = FakePage(200, 2000);
            var acc = new ScrollAccumulator();
            acc.AddFirstFrame(Slice(page, 500, 300));       // 首帧 = 内容 [500, 800)

            acc.Push(Slice(page, 600, 300), 100);           // 向下滚 100 → 底部到 900
            acc.Push(Slice(page, 650, 300), 50);            // 再向下 50 → 底部到 950
            var back = acc.Push(Slice(page, 570, 300), -80); // 往回滚 80，落在已截范围内
            var up = acc.Push(Slice(page, 470, 300), -100);  // 继续向上越过顶部 → 前插 30 行

            using var bmp = acc.BuildBitmap();
            var rowCount = bmp.Height;
            var expectedRows = 300 + 150 + 30;               // 首帧 + 向下新增 + 向上新增

            // 结果第 r 行应等于原页面第 (500 + MinCursor + r) 行
            var contentOk = true;
            for (var r = 0; r < rowCount && contentOk; r += 37)
            {
                if (bmp.GetPixel(0, r).ToArgb() != page.Pixels[(500 + acc.MinCursor + r) * 200]) contentOk = false;
            }

            var ok = rowCount == expectedRows
                     && !back && up
                     && acc.MinCursor == -30 && acc.MaxCursor == 150
                     && contentOk;

            Console.WriteLine($"  [累积模型] 行数 {rowCount}（期望 {expectedRows}）　" +
                              $"min/max={acc.MinCursor}/{acc.MaxCursor}（期望 -30/150）　" +
                              $"往回滚未重复={!back}　向上前插={up}　内容对齐={contentOk}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            // 基准重设后位置已经跟丢了，但**顺序**必须保持：用户很可能正一路往上滚，
            // 之后的内容要能正确拼到长图上方。关掉前插会让页面靠上的内容全被接到尾部，
            // 长图整体颠倒——正是用户报的故障形态。
            total++;
            var page = FakePage(200, 2000);
            var acc = new ScrollAccumulator();
            acc.AddFirstFrame(Slice(page, 500, 300));
            acc.Push(Slice(page, 600, 300), 100);
            acc.ResetBaseline();
            var tail = acc.Push(Slice(page, 700, 300), 100);
            var prepended = acc.Push(Slice(page, 400, 300), -300);

            using var bmp = acc.BuildBitmap();
            var rowCount = bmp.Height;
            var contentOk = ContentMatches(bmp, page, 500 + acc.MinCursor);
            var ok = tail && prepended && rowCount == 600 && acc.MinCursor == -100 && contentOk;

            Console.WriteLine($"  [基准重设] 行数 {rowCount}（期望 600）　接尾部={tail}　向上前插={prepended}　" +
                              $"内容对齐={contentOk}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            // 真实尺寸下必须塞进 200ms 的抓帧间隔，否则定时器会堆积
            total++;
            var page = FakePage(800, 2400);
            var prev = Slice(page, 0, 900);
            var next = Slice(page, 173, 900);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out _);
            sw.Stop();

            var good = ok && got == 173 && sw.ElapsedMilliseconds < 150;
            Console.WriteLine($"  [性能    ] 800×900 双向配准 {sw.ElapsedMilliseconds,4} ms（须 <150ms）　dh={got}　{(good ? "PASS" : "FAIL")}");
            if (good) pass++;
        }

        {
            // 最坏情况：大屏全屏选区。双向搜索的候选数翻倍，这里盯住上限
            total++;
            var page = FakePage(1920, 2600);
            var prev = Slice(page, 0, 1000);
            var next = Slice(page, 173, 1000);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out _);
            sw.Stop();

            var good = ok && got == 173 && sw.ElapsedMilliseconds < 150;
            Console.WriteLine($"  [性能 大屏] 1920×1000 双向配准 {sw.ElapsedMilliseconds,4} ms（须 <150ms）　dh={got}　{(good ? "PASS" : "FAIL")}");
            if (good) pass++;
        }

        {
            // 选区里含固定 UI（输入框 / 工具栏 / 状态栏）：这些行不随滚动移动，在任何候选偏移下
            // 都贡献同样大的差分，会把正确候选的分数抬过信任阈值，让配准全线失败、长图只剩首帧。
            total++;
            var page = FakePage(200, 1400);
            var ui = FakePage(200, 80);
            var prev = WithFixedBottom(Slice(page, 0, 300), ui, 60);
            var next = WithFixedBottom(Slice(page, 140, 300), ui, 60);
            var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score) && got == 140;
            Console.WriteLine($"  [固定 UI ] 底部 20% 不随滚动移动　期望 dh= 140　实测 dh={got,4}　score={score,6:F2}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            // 内容周期性（聊天卡片 / 列表 / 表格行）：差一个周期的候选分数完全一样，
            // 不按「与上一步位移接近」裁决就会选错，长图里就是整段内容重复覆盖。
            total++;
            var page = Tile(FakePage(200, 150), 8);      // 周期 150 行
            var prev = Slice(page, 0, 300);
            var next = Slice(page, 100, 300);            // 真值 +100，等价伪峰 -50 / +250
            var down = FrameStitcher.TryEstimateOffset(prev, next, out var gotDown, out _, 100) && gotDown == 100;
            var up = FrameStitcher.TryEstimateOffset(prev, next, out var gotUp, out _, -50) && gotUp == -50;
            Console.WriteLine($"  [周期内容] hint=+100 得 {gotDown,4}　hint= -50 得 {gotUp,4}　{(down && up ? "PASS" : "FAIL")}");
            if (down && up) pass++;
        }

        {
            // 行结构内容（每 gap 行只有 2 行文字带，带内逐像素随机、每带都不同）：粗搜 step=8 的
            // 采样行会整条落在白底上，冒出一大批假并列（分数全是 0），真值被埋在并列里、
            // 进不了精修范围。这正是「并列候选再用 step=4 复算一遍」专门修的场景，
            // 2/16 就是当初出错的那一档，所以两档都要盯住。
            foreach (var gap in new[] { 12, 16 })
            {
                total++;
                var page = SparsePage(200, 2200, gap);
                var prev = Slice(page, 400, 300);
                var next = Slice(page, 500, 300);
                var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score) && got == 100;
                Console.WriteLine($"  [行结构  ] 每 {gap,2} 行 2 行文字带　期望 dh= 100　实测 dh={got,4}　score={score,6:F2}　{(ok ? "PASS" : "FAIL")}");
                if (ok) pass++;
            }
        }

        {
            // 严格周期内容 + 没有上一步位移（首帧、或刚重设过基准）：差一个周期的候选在数学上
            // 完全等价，单帧根本分辨不了方向。所以这里要的是「给出的是一个内容真的对得上的位移，
            // 或者干脆明确说未确定」——绝不允许随便挑一个方向写进长图（那正是顺序颠倒的来源）。
            total++;
            var card = FakePage(200, 150);
            var page = Tile(card, 8);
            var prev = Slice(page, 0, 300);
            var next = Slice(page, 100, 300);

            var found = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score, 0);
            var ok = !found || score <= 1.0;

            Console.WriteLine($"  [无先验  ] 周期 150、不给上一步位移　" +
                              $"{(found ? $"实测 dh={got,4}　score={score,6:F2}" : "判为未确定（不猜方向）")}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            // 反向滚动端到端：用户从页面底部一路往上滚（dh 为负），长图必须仍是页面从上到下的
            // 顺序——页面靠下的内容接在下方。每帧都走真实配准拿 dh 再进真实累积器，最后逐行
            // 比对长图与页面：顺序一旦颠倒或重复，比对立刻失败。
            // 必须覆盖 GrabOnceCore 的 hint 路径：首帧之后还没有「上一步位移」（hint=0），
            // 以及滚动中间手一停（dh=0，hint 被清零）——正是这两处最容易被周期内容骗到。
            foreach (var (kind, step, pause, note, strict) in new (string, int, bool, string, bool)[]
            {
                ("tile48", -100, false, "反向·周期 48", true),
                ("tile48", -100, true, "反向·周期 48 + 停顿", true),
                ("sparse16", -100, true, "反向·行结构 16 + 停顿", false),
                ("fake", -100, true, "反向·无周期 + 停顿", false),
                ("tile48", -96, true, "反向·周期 48、步长 96", true),
                ("fake", 100, true, "正向·无周期 + 停顿", false),
                ("sparse16", 100, true, "正向·行结构 16 + 停顿", false),
                ("tile48", 100, true, "正向·周期 48 + 停顿", true),
            })
            {
                total++;

                var page = kind switch
                {
                    "tile48" => Tile(FakePage(200, 48), 63),
                    "sparse16" => SparsePage(200, 3000, 16),
                    _ => FakePage(200, 3000),
                };

                var steps = new List<int>();
                for (var i = 0; i < 10; i++)
                {
                    steps.Add(step);
                    if (pause) steps.Add(0);       // 手停：同一帧再来一次
                }

                // 向下滚只动尾部，向上滚才前插，所以「内容坐标最上端」只有反向滚动才会变负
                var expectedMin = step < 0 ? step * 10 : 0;
                var expectedRows = 300 + (step < 0 ? -expectedMin : step * 10);
                var startTop = step < 0 ? 1500 : 500;

                var (min, rows, contentOk) = ReverseScroll(page, startTop, 300, steps.ToArray());

                // 严格周期内容是数学边界：差一个周期的候选分数完全相同，方向不可分辨。
                // 这种内容只要求「长图仍是页面的连续切片，没有接反也没有重复」——
                // 宁可少一段，也绝不能把页面靠上的内容接到长图尾部。
                var ok = strict
                    ? contentOk && min <= 0 && min >= expectedMin
                    : min == expectedMin && rows == expectedRows && contentOk;

                Console.WriteLine($"  [反向滚动] {note,-18}　min={min,6}（期望 {expectedMin}）　" +
                                  $"行数={rows,5}（期望 {expectedRows}）　内容对齐={contentOk}　{(ok ? "PASS" : "FAIL")}");
                if (ok) pass++;
            }
        }

        {
            // 低对比度内容 + 帧间抖动（视频、大面积渐变、亚像素渲染变化）：错开一行与正对上的
            // 分数本来就差不多，峰形判据会把它误当成伪匹配。这种内容本来是能对上的，不能丢。
            foreach (var sigma in new[] { 0, 2, 8 })
            {
                total++;
                var page = LowContrastPage(200, 1400);
                var prev = AddNoise(Slice(page, 0, 300), sigma, 11);
                var next = AddNoise(Slice(page, 100, 300), sigma, 22);

                var ok = FrameStitcher.TryEstimateOffset(prev, next, out var got, out var score) && got == 100;
                Console.WriteLine($"  [低对比度] 帧间噪声 σ={sigma}　期望 dh= 100　实测 dh={got,4}　" +
                                  $"score={score,6:F2}　{(ok ? "PASS" : "FAIL")}");
                if (ok) pass++;
            }
        }

        {
            // 首帧回退：用户先往下滚一屏多，再快速往回滚。相邻两帧毫无重叠（-600 超过可检测
            // 上限 0.875h），但当前帧与首帧还有重叠——直接跟首帧对就能把绝对坐标一次性拿回来。
            total++;
            var page = FakePage(200, 4000);
            var steps = new List<int>();
            for (var i = 0; i < 5; i++) { steps.Add(100); steps.Add(0); }
            steps.Add(-600); steps.Add(0);
            for (var i = 0; i < 2; i++) { steps.Add(-100); steps.Add(0); }

            var (min, rows, contentOk) = ReverseScroll(page, 1000, 300, steps.ToArray());
            var ok = min == -300 && rows == 1100 && contentOk;

            Console.WriteLine($"  [首帧回退] min={min,6}（期望 -300）　行数={rows,5}（期望 1100）　" +
                              $"内容对齐={contentOk}　{(ok ? "PASS" : "FAIL")}");
            if (ok) pass++;
        }

        {
            // 位移超过可检测上限（单帧大于 h - h/8 ≈ 0.875h）时相邻两帧毫无重叠，配准在数学上
            // 不可能，内容只能丢——这里唯一的要求是「长图仍是页面的连续切片」：可以少内容，
            // 绝不能把内容接到错误位置（旧版会把伪匹配当成真匹配采信，正是用户报的顺序颠倒）。
            total++;
            var page = SparsePage(200, 4000, 24);
            var steps = new List<int>();
            for (var i = 0; i < 4; i++) { steps.Add(-400); steps.Add(0); }
            for (var i = 0; i < 6; i++) { steps.Add(-150); steps.Add(0); }

            var (min, rows, contentOk) = ReverseScroll(page, 2500, 300, steps.ToArray(), rebaseline: true);
            var ok = contentOk;

            Console.WriteLine($"  [反向大跳] min={min,6}　行数={rows,5}　内容对齐={contentOk}" +
                              "（可少不可错）　" + (ok ? "PASS" : "FAIL"));
            if (ok) pass++;
        }

        Console.WriteLine();
        Console.WriteLine(pass == total
            ? $"全部通过：{pass}/{total}"
            : $"有失败：{pass}/{total} 通过");
    }

    /// <summary>造一张带纵向纹理的合成长图（模拟文字行 / 表格线）。</summary>
    private static ScreenFrame FakePage(int w, int h)
    {
        var pixels = new int[w * h];
        var gray = new byte[w * h];
        var rnd = new Random(20260101);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = (byte)((x * 13 + y * 7 + rnd.Next(0, 6)) % 251);
                gray[y * w + x] = v;
                pixels[y * w + x] = unchecked((int)0xFF000000 | (v << 16) | (v << 8) | v);
            }
        }

        return new ScreenFrame { Width = w, Height = h, Gray = gray, Pixels = pixels };
    }

    private static ScreenFrame Blank(int w, int h) => new()
    {
        Width = w,
        Height = h,
        Gray = new byte[w * h],
        Pixels = new int[w * h],
    };

    private static ScreenFrame Slice(ScreenFrame page, int top, int height)
    {
        var w = page.Width;
        var gray = new byte[w * height];
        var pixels = new int[w * height];

        Array.Copy(page.Gray, top * w, gray, 0, w * height);
        Array.Copy(page.Pixels, top * w, pixels, 0, w * height);

        return new ScreenFrame { Width = w, Height = height, Gray = gray, Pixels = pixels };
    }

    /// <summary>把帧底部 rows 行换成「不随滚动移动」的固定内容，模拟选区包含输入框 / 工具栏。</summary>
    private static ScreenFrame WithFixedBottom(ScreenFrame frame, ScreenFrame fixedContent, int rows)
    {
        var w = frame.Width;
        var gray = (byte[])frame.Gray.Clone();
        var pixels = (int[])frame.Pixels.Clone();

        Array.Copy(fixedContent.Gray, 0, gray, (frame.Height - rows) * w, rows * w);
        Array.Copy(fixedContent.Pixels, 0, pixels, (frame.Height - rows) * w, rows * w);

        return new ScreenFrame { Width = w, Height = frame.Height, Gray = gray, Pixels = pixels };
    }

    /// <summary>
    /// 造一张「行结构」页面：每 gap 行只有 2 行有文字，其余是接近白底（245）的背景。
    /// 文字带里逐像素二值随机、每条带都不同，模拟真实文本行——不能是整行纯色，
    /// 否则不会出现「粗搜采样行整条落在白底上」的假并列。
    /// </summary>
    private static ScreenFrame SparsePage(int w, int h, int gap)
    {
        var pixels = new int[w * h];
        var gray = new byte[w * h];
        var rnd = new Random(11);

        for (var y = 0; y < h; y++)
        {
            var textBand = y % gap < 2;
            for (var x = 0; x < w; x++)
            {
                var v = textBand ? (byte)(rnd.Next(2) == 0 ? 30 : 210) : (byte)245;
                gray[y * w + x] = v;
                pixels[y * w + x] = unchecked((int)0xFF000000 | (v << 16) | (v << 8) | v);
            }
        }

        return new ScreenFrame { Width = w, Height = h, Gray = gray, Pixels = pixels };
    }

    /// <summary>把一张卡片纵向重复 count 次，模拟内容周期性（聊天卡片 / 列表 / 表格行）。</summary>
    private static ScreenFrame Tile(ScreenFrame card, int count)
    {
        var w = card.Width;
        var block = card.Height * w;
        var gray = new byte[block * count];
        var pixels = new int[block * count];

        for (var i = 0; i < count; i++)
        {
            Array.Copy(card.Gray, 0, gray, i * block, block);
            Array.Copy(card.Pixels, 0, pixels, i * block, block);
        }

        return new ScreenFrame { Width = w, Height = card.Height * count, Gray = gray, Pixels = pixels };
    }

    /// <summary>
    /// 反向滚动端到端：按给定步长序列在 page 上滑窗，每帧先过真实配准拿 dh、再进真实累积器
    /// （hint 的传递与清零完全复刻 RegionSelectorForm.GrabOnceCore），最后回报累积器的内容坐标。
    /// 步长为 0 表示用户这一拍手停：配准会给出 dh=0，hint 随之归零。
    /// </summary>
    private static (int MinCursor, int Rows, bool ContentOk) ReverseScroll(
        ScreenFrame page, int startTop, int frameHeight, int[] steps, bool rebaseline = false, bool trace = false)
    {
        var acc = new ScrollAccumulator();
        var top = startTop;
        var first = Slice(page, top, frameHeight);
        acc.AddFirstFrame(first);

        var prev = first;
        var lastDh = 0;                         // 首帧之后还没有「上一步位移」
        var missStreak = 0;

        if (trace) Console.WriteLine($"    top={top,5}　首帧");

        foreach (var step in steps)
        {
            top += step;
            var frame = Slice(page, top, frameHeight);

            if (!FrameStitcher.TryEstimateOffset(prev, frame, out var dh, out var sc, lastDh)
                && !TryRealign(first, frame, acc, out dh, out sc))
            {
                missStreak++;
                if (trace) Console.WriteLine($"    top={top,5}　step={step,5}　hint={lastDh,5}　配准失败　miss={missStreak}");

                // 与 RegionSelectorForm.GrabOnceCore 一致：一帧配不上先保留旧基准，
                // 连续 5 帧配不上才重设基准
                if (rebaseline && missStreak >= 5)
                {
                    prev = frame;
                    acc.ResetBaseline();
                    missStreak = 0;
                    lastDh = 0;
                    if (trace) Console.WriteLine("    ── 基准重设 ──");
                }

                continue;
            }

            missStreak = 0;
            var pushed = acc.Push(frame, dh);
            if (dh != 0) lastDh = dh;           // 停手时 dh=0，hint 不清零

            if (trace)
            {
                Console.WriteLine($"    top={top,5}　step={step,5}　hint={lastDh,5}　dh={dh,5}　" +
                                  $"score={sc,6:F2}　push={pushed}　cursor={acc.Cursor}　min/max={acc.MinCursor}/{acc.MaxCursor}　rows={acc.Rows}");
            }

            prev = frame;
        }

        using var bmp = acc.BuildBitmap();
        return (acc.MinCursor, acc.Rows, ContentMatches(bmp, page, startTop + acc.MinCursor));
    }

    /// <summary>与 RegionSelectorForm.TryRealignToFirst 一致：跟首帧直接对，换回绝对坐标。</summary>
    private static bool TryRealign(
        ScreenFrame first, ScreenFrame frame, ScrollAccumulator acc, out int dh, out double score)
    {
        dh = 0;
        score = 0;

        if (!FrameStitcher.TryEstimateOffset(first, frame, out var absolute, out score)) return false;

        dh = absolute - acc.Cursor;
        return dh != 0;
    }

    /// <summary>低对比度页面：行底色逐行独立起伏 ±3（视频、大面积渐变、浅色表格、浅灰骨架屏）。</summary>
    private static ScreenFrame LowContrastPage(int w, int h)
    {
        var pixels = new int[w * h];
        var gray = new byte[w * h];
        var rnd = new Random(3);

        for (var y = 0; y < h; y++)
        {
            var row = 150 + rnd.Next(-3, 4);

            for (var x = 0; x < w; x++)
            {
                var v = (byte)Math.Clamp(row + rnd.Next(0, 2), 0, 255);
                gray[y * w + x] = v;
                pixels[y * w + x] = unchecked((int)0xFF000000 | (v << 16) | (v << 8) | v);
            }
        }

        return new ScreenFrame { Width = w, Height = h, Gray = gray, Pixels = pixels };
    }

    /// <summary>叠加独立的帧间抖动，模拟渲染差异 / 亚像素变化 / 视频噪声。</summary>
    private static ScreenFrame AddNoise(ScreenFrame frame, int sigma, int seed)
    {
        if (sigma <= 0) return frame;

        var size = frame.Gray.Length;
        var gray = new byte[size];
        var pixels = new int[size];
        var rnd = new Random(seed);

        for (var i = 0; i < size; i++)
        {
            // 两个均匀分布相加近似正态，省一次浮点开方
            var delta = (rnd.Next(-sigma, sigma + 1) + rnd.Next(-sigma, sigma + 1)) / 2;
            var v = (byte)Math.Clamp(frame.Gray[i] + delta, 0, 255);
            gray[i] = v;
            pixels[i] = unchecked((int)0xFF000000 | (v << 16) | (v << 8) | v);
        }

        return new ScreenFrame { Width = frame.Width, Height = frame.Height, Gray = gray, Pixels = pixels };
    }

    /// <summary>长图每一行是否都等于页面从 pageTop 起的对应行（顺序颠倒或重复都会露馅）。</summary>
    private static bool ContentMatches(Bitmap bmp, ScreenFrame page, int pageTop)
    {
        var w = page.Width;
        if (pageTop < 0 || pageTop + bmp.Height > page.Height) return false;

        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < w; x += 7)
            {
                if (bmp.GetPixel(x, y).ToArgb() != page.Pixels[(pageTop + y) * w + x]) return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 自检五：真实抓屏。从屏幕中间抓一小块，确认 CopyFromScreen 在当前环境返回真实画面
/// 而不是一片纯黑（锁屏 / 无桌面会话时会是纯黑）。
/// </summary>
internal static class GrabTestRunner
{
    public static void Run()
    {
        ProbeRunner.UseUtf8Console();
        Console.WriteLine("=== 抓屏自检 ===");

        var screen = SystemInformation.VirtualScreen;
        var rect = new Rectangle(screen.Left + screen.Width / 4, screen.Top + screen.Height / 4, 200, 200);

        try
        {
            var frame = ScreenGrabber.Grab(rect);

            long sum = 0;
            foreach (var g in frame.Gray) sum += g;
            var average = (double)sum / frame.Gray.Length;
            var distinct = frame.Gray.Distinct().Count();

            Console.WriteLine($"  屏幕范围 {screen.Width}×{screen.Height}，抓取区域 {rect}");
            Console.WriteLine($"  结果 {frame.Width}×{frame.Height}　平均灰度 {average:F1}　不同灰度 {distinct} 种");
            Console.WriteLine(average > 1 && distinct > 5
                ? "  PASS：抓到的是真实画面内容。"
                : "  注意：几乎纯色，桌面可能处于锁屏或空屏状态。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL：抓屏抛异常 {ex.Message}");
        }
    }
}

/// <summary>
/// 自检六：选区窗口的鼠标命中。会短暂显示遮罩并把光标挪动一次，随后恢复原位。
///
/// 用「合成鼠标移动 → 看谁收到 MouseMove」判断命中，而不是 WindowFromPoint：
/// 后者会跳过 layered 窗口（Opacity &lt; 1 的窗口就是 layered），测不出真实命中。
/// </summary>
internal static class OverlayTestRunner
{
    private const int WmMouseWheel = 0x020A;

    public static void Run()
    {
        ProbeRunner.UseUtf8Console();
        Console.WriteLine("=== 选区窗口自检（屏幕会短暂变暗，光标会被挪动后归位）===");

        var screen = SystemInformation.VirtualScreen;
        var local = new Rectangle(screen.Width / 2 - 200, screen.Height / 2 - 150, 400, 300);
        var onScreen = new Rectangle(local.X + screen.Left, local.Y + screen.Top, local.Width, local.Height);
        var probe = new Point(onScreen.Left + 120, onScreen.Top + 120);

        if (!CaptureNative.GetCursorPos(out var original))
            original = new Point(screen.Left + 10, screen.Top + 10);

        var baseline = ScreenGrabber.Grab(onScreen);
        var form = new RegionSelectorForm();

        form.Shown += async (_, _) =>
        {
            using (var legacy = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = screen,
                BackColor = Color.Fuchsia,
                TransparencyKey = Color.Fuchsia,
                TopMost = true,
                ShowInTaskbar = false,
            })
            {
                var legacySawMove = false;
                legacy.MouseMove += (_, _) => legacySawMove = true;
                legacy.Show();
                await Task.Delay(200);

                var hit = await HoverAndCheckAsync(probe, original, () => legacySawMove);
                Console.WriteLine(hit switch
                {
                    true => "  [对照  ] TransparencyKey 窗口接到了鼠标 —— 根因判断需要重新看",
                    false => "  [对照  ] TransparencyKey 的透明区域鼠标穿透 —— 拿它当遮罩，框选时收不到 MouseDown",
                    null => "  [对照  ] SKIP：本环境合成光标无效",
                });
                legacy.Close();
            }

            await Task.Delay(200);

            // 结构性校验：不依赖任何合成输入，直接看穿透位有没有被设上
            var ex1 = CaptureNative.GetExStyle(form.Handle);
            Console.WriteLine((ex1 & CaptureNative.WsExTransparent) == 0
                ? "  [阶段1 样式] PASS：未设 WS_EX_TRANSPARENT，遮罩会接住鼠标"
                : "  [阶段1 样式] FAIL：遮罩被设成鼠标穿透，框选必然失效");

            form.ResetMouseFlag();
            var stage1 = await HoverAndCheckAsync(probe, original, () => form.SawMouseMove);
            Console.WriteLine(stage1 switch
            {
                true => "  [阶段1 悬停] PASS：鼠标落到了遮罩上，可以框选",
                false => "  [阶段1 悬停] FAIL：鼠标没落到遮罩上",
                null => "  [阶段1 悬停] SKIP：本环境合成光标无效，无法端到端验证（看上面的样式校验）",
            });

            form.SimulateSelection(local);
            await Task.Delay(300);

            var ex2 = CaptureNative.GetExStyle(form.Handle);
            Console.WriteLine((ex2 & CaptureNative.WsExTransparent) != 0
                ? "  [阶段2 样式] PASS：已设 WS_EX_TRANSPARENT，鼠标与滚轮穿透到下层"
                : "  [阶段2 样式] FAIL：穿透位没设上，会挡住下层应用");

            form.ResetMouseFlag();
            var stage2 = await HoverAndCheckAsync(probe, original, () => form.SawMouseMove);
            Console.WriteLine(stage2 switch
            {
                true => "  [阶段2 悬停] FAIL：抓取时仍拦鼠标",
                false => "  [阶段2 悬停] PASS：鼠标穿透到下层应用",
                null => "  [阶段2 悬停] SKIP：本环境合成光标无效",
            });

            // 自校准三帧法：屏幕内容本身会变（时钟、鼠标 hover），
            // 所以拿「同一配置重复抓帧」的漂移当基线，再看窗口有没有额外贡献像素。
            var withWindow1 = ScreenGrabber.Grab(onScreen);
            form.Hide();
            await Task.Delay(140);
            var withoutWindow = ScreenGrabber.Grab(onScreen);
            form.Show();
            await Task.Delay(140);
            var withWindow2 = ScreenGrabber.Grab(onScreen);

            var drift = AverageDelta(withWindow1.Gray, withWindow2.Gray);
            var effect = AverageDelta(withWindow1.Gray, withoutWindow.Gray);
            Console.WriteLine(effect <= drift + 3
                ? $"  [阶段2 抓帧] PASS：窗口没往画面里塞像素（有无窗口差 {effect:F1}，同配置漂移 {drift:F1}）"
                : $"  [阶段2 抓帧] FAIL：抓帧被窗口影响（有无窗口差 {effect:F1}，同配置漂移 {drift:F1}）");

            Console.WriteLine(form.HookInstalled
                ? "  [左键 钩子] PASS：鼠标钩子已安装"
                : "  [左键 钩子] FAIL：鼠标钩子没装上，左键结束会失效");

            // 滚轮必须放行（否则下层应用滚不动）
            var wheelSwallowed = form.InvokeHookForTest(WmMouseWheel);
            Console.WriteLine(!wheelSwallowed
                ? "  [滚轮 放行] PASS：滚轮不被钩子拦截，下层应用照常滚动"
                : "  [滚轮 放行] FAIL：滚轮被吞掉了，内容根本滚不动");

            // 极端情况：选区几乎占满屏幕，浮层没地方放。
            // 以前这里直接放弃显示，用户就完全看不到 «左键结束» 这条提示了。
            form.SimulateSelection(new Rectangle(0, 0, screen.Width, screen.Height));
            await Task.Delay(400);
            Console.WriteLine(form.HudVisible
                ? "  [浮层 极端] PASS：选区占满屏幕时浮层仍然显示（贴到角落）"
                : "  [浮层 极端] FAIL：浮层不见了，用户看不到任何提示");

            // 左键必须被吞掉并触发结束。喂消息给回调，不产生真实点击，绝不会误触你的应用。
            // 结束会关掉主窗体、消息循环随即退出，所以断言必须放在 FormClosed 里。
            var leftSwallowed = false;
            form.FormClosed += (_, _) =>
                Console.WriteLine(leftSwallowed
                    ? "  [左键 结束] PASS：左键被吞掉，截图随之结束"
                    : "  [左键 结束] FAIL：左键没被吞掉");

            form.SuppressOutputForTest = true;
            leftSwallowed = form.InvokeHookForTest(CaptureNative.WmLeftButtonDown);

            // 万一左键逻辑失效，别把测试挂死
            var guard = new System.Windows.Forms.Timer { Interval = 3000 };
            guard.Tick += (_, _) =>
            {
                guard.Stop();
                form.Close();
            };
            guard.Start();
        };

        Application.Run(form);
    }

    /// <summary>
    /// 把光标挪到目标点，在归位之前读取命中结果（归位本身也会产生移动事件）。
    /// 返回 null 表示合成光标移动在本环境无效，这一次探测作废——不能把环境限制读成结论。
    /// </summary>
    private static async Task<bool?> HoverAndCheckAsync(Point probe, Point restore, Func<bool> sawMove)
    {
        CaptureNative.SetCursorPos(probe.X, probe.Y);
        await Task.Delay(180);

        CaptureNative.GetCursorPos(out var now);
        var moved = Math.Abs(now.X - probe.X) <= 2 && Math.Abs(now.Y - probe.Y) <= 2;
        var hit = moved && sawMove();

        CaptureNative.SetCursorPos(restore.X, restore.Y);
        await Task.Delay(120);

        return moved ? hit : null;
    }

    private static double AverageDelta(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;

        long sum = 0;
        for (var i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / n;
    }
}

/// <summary>
/// 自检：长图输出目录。逐个候选目录实地试写，把「为什么存不下来」摆出来。
/// 图片目录被权限策略、OneDrive 重定向或安全软件挡住时，这里一眼就能看出来。
/// </summary>
internal static class PathTestRunner
{
    public static void Run()
    {
        ProbeRunner.UseUtf8Console();
        Console.WriteLine("=== 长图输出目录自检 ===");

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        Console.WriteLine($"  图片目录   : {pictures}");
        Console.WriteLine($"    └ 可写？ : {Probe(string.IsNullOrEmpty(pictures) ? string.Empty : Path.Combine(pictures, "LongShot"))}");
        Console.WriteLine($"  exe 目录   : {AppContext.BaseDirectory}");
        Console.WriteLine($"    └ 可写？ : {Probe(Path.Combine(AppContext.BaseDirectory, "captures"))}");
        Console.WriteLine($"  临时目录   : {Path.GetTempPath()}");
        Console.WriteLine($"    └ 可写？ : {Probe(Path.Combine(Path.GetTempPath(), "LongShot"))}");

        var chosen = ScreenGrabber.OutputFolder();
        Console.WriteLine($"  实际选用   : {chosen}");

        try
        {
            using var bmp = new Bitmap(40, 40);
            using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Magenta);

            var path = Path.Combine(chosen, "pathtest.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            var bytes = new FileInfo(path).Length;
            File.Delete(path);

            Console.WriteLine($"  PASS：真实保存成功（{bytes} 字节）——长图会落到上面这个目录");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL：真实保存失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    private static string Probe(string path)
    {
        if (string.IsNullOrEmpty(path)) return "跳过（路径为空）";

        try
        {
            Directory.CreateDirectory(path);

            var probe = Path.Combine(path, ".probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return "是";
        }
        catch (Exception ex)
        {
            return $"否（{ex.GetType().Name}）";
        }
    }
}

/// <summary>
/// 自检七：把设置界面抓成 PNG，用来核对外观。会短暂弹出设置窗口。
/// </summary>
internal static class UiShotRunner
{
    public static void Run()
    {
        ProbeRunner.UseUtf8Console();

        var form = new SettingsForm { TopMost = true };

        form.Shown += (_, _) =>
        {
            var timer = new System.Windows.Forms.Timer { Interval = 700 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    // 必须用 PrintWindow：CopyFromScreen 抓到的是盖在上面的别的窗口
                    using (var bmp = CaptureNative.CaptureWindow(form))
                    {
                        var path = Path.Combine(AppContext.BaseDirectory, "ui-preview.png");
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine($"设置界面截图：{path}（{bmp.Width}×{bmp.Height}）");
                    }

                    // 抓取阶段的浮层卡片：挪到屏幕外显示，抓完即关，不干扰你
                    using var hud = new CaptureHud(() => { });
                    hud.SetBounds(-4000, -4000, 352, 64);
                    hud.Show();
                    Application.DoEvents();
                    hud.UpdateStatus(12, 1834, 2, false);
                    Application.DoEvents();

                    using (var shot = CaptureNative.CaptureWindow(hud))
                    {
                        var hudPath = Path.Combine(AppContext.BaseDirectory, "hud-preview.png");
                        shot.Save(hudPath, System.Drawing.Imaging.ImageFormat.Png);
                        Console.WriteLine($"浮层卡片截图：{hudPath}（{shot.Width}×{shot.Height}）");
                    }

                    hud.Close();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"截图失败：{ex.Message}");
                }

                form.Close();
            };
            timer.Start();
        };

        Application.Run(form);
    }
}

/// <summary>
/// 自检：框选坐标与抓帧坐标是否对齐。
/// 屏幕上放一个纯色标记窗口，再用「屏幕坐标」去抓那一块——颜色对得上，
/// 就说明「鼠标框选的位置」和「真正截到的位置」是同一条坐标链路。
/// 会短暂在屏幕中央显示一个品红方块。
/// </summary>
internal static class AlignTestRunner
{
    public static void Run()
    {
        ProbeRunner.UseUtf8Console();
        Console.WriteLine("=== 框选坐标对齐自检 ===");

        var screen = SystemInformation.VirtualScreen;
        Console.WriteLine($"  虚拟屏 = {screen}");
        Console.WriteLine($"  主屏   = {Screen.PrimaryScreen?.Bounds}");

        // 选区窗口必须与虚拟屏等大，否则框选范围从根上就不对
        using (var overlay = new RegionSelectorForm())
        {
            Console.WriteLine(overlay.Bounds == screen
                ? $"  [窗口覆盖] PASS：选区窗口 {overlay.Bounds.Size} 与虚拟屏一致"
                : $"  [窗口覆盖] FAIL：选区窗口 {overlay.Bounds.Size} ≠ 虚拟屏 {screen.Size}");
        }

        // 尺寸要大于 Windows 的最小窗口约束（实测 120 会被抬到 170），否则测的是别的东西
        var marker = new Rectangle(
            screen.Left + screen.Width / 2 - 150,
            screen.Top + screen.Height / 2 - 150,
            300,
            300);

        using var flag = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = marker,
            BackColor = Color.FromArgb(255, 0, 255),
            TopMost = true,
            ShowInTaskbar = false,
        };

        flag.Shown += async (_, _) =>
        {
            // 桌面上可能压着别的置顶窗口，用 SetWindowPos 硬推一次再抓
            CaptureNative.ForceTopmost(flag.Handle);
            flag.Refresh();
            await Task.Delay(700);

            Console.WriteLine($"  标记窗口 可见={flag.Visible} 置顶={flag.TopMost} 期望 {marker} 实际 {flag.Bounds}");

            try
            {
                // 先全屏扫一遍，看品红到底画在哪儿（或压根没画出来）
                var full = ScreenGrabber.Grab(screen);
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, hits = 0;

                for (var y = 0; y < full.Height; y++)
                {
                    for (var x = 0; x < full.Width; x++)
                    {
                        var c = full.Pixels[y * full.Width + x];
                        var r0 = (c >> 16) & 0xFF;
                        var g0 = (c >> 8) & 0xFF;
                        var b0 = c & 0xFF;

                        if (r0 > 200 && g0 < 60 && b0 > 200)
                        {
                            hits++;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                Console.WriteLine(hits > 0
                    ? $"  全屏品红像素 {hits} 个，包围盒 X[{minX},{maxX}] Y[{minY},{maxY}]"
                    : "  全屏找不到品红像素 —— 标记窗口没画到屏幕上（被遮挡或未渲染）");

                // 把抓到的画面存下来，肉眼核对抓帧内容本身对不对
                try
                {
                    using var shot = ScreenGrabber.BuildBitmap(new List<int>(full.Pixels), full.Width);
                    var shotPath = Path.Combine(AppContext.BaseDirectory, "align-fullscreen.png");
                    shot.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"  全屏抓帧已存：{shotPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  全屏抓帧保存失败：{ex.Message}");
                }

                var frame = ScreenGrabber.Grab(marker);
                var magenta = 0;
                long rSum = 0, gSum = 0, bSum = 0;

                foreach (var c in frame.Pixels)
                {
                    var r = (c >> 16) & 0xFF;
                    var g = (c >> 8) & 0xFF;
                    var b = c & 0xFF;
                    rSum += r;
                    gSum += g;
                    bSum += b;
                    if (r > 200 && g < 60 && b > 200) magenta++;
                }

                var count = frame.Pixels.Length;
                var ratio = (double)magenta / count;
                Console.WriteLine($"  抓到的平均颜色 R={rSum / (double)count:F0} G={gSum / (double)count:F0} B={bSum / (double)count:F0}");

                Console.WriteLine(ratio > 0.9
                    ? $"  [坐标对齐] PASS：按屏幕坐标 {marker} 抓到的就是标记窗口（品红 {ratio:P0}）"
                    : $"  [坐标对齐] FAIL：抓到的不是标记窗口（品红只有 {ratio:P0}）—— 框选范围与实际截图不一致");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [坐标对齐] FAIL：抓屏抛异常 {ex.Message}");
            }

            flag.Close();
        };

        Application.Run(flag);
    }
}

/// <summary>
/// 自检：设置窗口从构造到显示完成要多久，用来盯住启动流畅度。
/// </summary>
internal static class StartupTestRunner
{
    public static void Run()
    {
        ProbeRunner.UseUtf8Console();
        Console.WriteLine("=== 启动耗时自检 ===");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var form = new SettingsForm();
        var constructed = clock.ElapsedMilliseconds;

        form.Shown += (_, _) =>
        {
            clock.Stop();
            Console.WriteLine($"  构造窗口     {constructed,5} ms");
            Console.WriteLine($"  构造 → 显示  {clock.ElapsedMilliseconds,5} ms");
            Console.WriteLine(clock.ElapsedMilliseconds < 800
                ? "  PASS：启动在 800ms 以内"
                : "  FAIL：启动超过 800ms，用户会感到卡顿");
            form.Close();
        };

        Application.Run(form);
    }
}

internal static class HiddenHost
{
    public static HotkeyHostForm Create() => new()
    {
        Opacity = 0,
        ShowInTaskbar = false,
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        Location = new Point(-4000, -4000),
        Size = new Size(1, 1),
    };
}
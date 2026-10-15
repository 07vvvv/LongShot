using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LongShot;

// ---------------------------------------------------------------- Win32

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

// ---------------------------------------------------------------- 模型

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,

    /// <summary>MOD_NOREPEAT：长按不重复触发。</summary>
    NoRepeat = 0x4000,
}

public sealed record HotkeySpec(HotkeyModifiers Modifiers, Keys Key)
{
    public HotkeyModifiers EffectiveModifiers => Modifiers & ~HotkeyModifiers.NoRepeat;

    public bool HasWin => EffectiveModifiers.HasFlag(HotkeyModifiers.Win);

    public string Display
    {
        get
        {
            var parts = new List<string>(5);
            if (EffectiveModifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (EffectiveModifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (EffectiveModifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (EffectiveModifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(HotkeyRules.KeyName(Key));
            return string.Join("+", parts);
        }
    }

    public override string ToString() => Display;
}

public enum CheckSeverity
{
    /// <summary>没问题，直接走试注册。</summary>
    Ok,

    /// <summary>属于系统保留键，可以尝试，但多半会被系统占用。</summary>
    Warn,

    /// <summary>本地规则就不允许，不必调 API。</summary>
    Reject,
}

public sealed record CheckResult(CheckSeverity Severity, string Message)
{
    public bool CanProceed => Severity != CheckSeverity.Reject;
}

// ---------------------------------------------------------------- 规则

public static class HotkeyRules
{
    /// <summary>
    /// 系统保留键表。这里只用来给出更友好的文案，不是硬拒绝——
    /// 真正的判据永远是 TryProbe 的实际注册结果。
    /// </summary>
    private static readonly (HotkeyModifiers Mods, Keys Key, string Why)[] SystemReserved =
    [
        (HotkeyModifiers.Win, Keys.E, "Win+E 由资源管理器占用"),
        (HotkeyModifiers.Win, Keys.R, "Win+R 由“运行”对话框占用"),
        (HotkeyModifiers.Win, Keys.D, "Win+D 由“显示桌面”占用"),
        (HotkeyModifiers.Win, Keys.I, "Win+I 由系统设置占用"),
        (HotkeyModifiers.Win, Keys.A, "Win+A 由操作中心占用"),
        (HotkeyModifiers.Win, Keys.X, "Win+X 由快速链接菜单占用"),
        (HotkeyModifiers.Win, Keys.S, "Win+S 由系统搜索占用"),
        (HotkeyModifiers.Win, Keys.L, "Win+L 由系统锁定屏幕占用"),
        (HotkeyModifiers.Win, Keys.Tab, "Win+Tab 由任务视图占用"),
        (HotkeyModifiers.Win, Keys.Space, "Win+Space 由输入法切换占用"),
        (HotkeyModifiers.Alt, Keys.Tab, "Alt+Tab 由任务切换器占用"),
        (HotkeyModifiers.Alt, Keys.Escape, "Alt+Esc 由系统窗口切换占用"),
        (HotkeyModifiers.Alt, Keys.F4, "Alt+F4 由系统关闭窗口占用"),
        (HotkeyModifiers.Alt, Keys.Space, "Alt+Space 由窗口系统菜单占用"),
        (HotkeyModifiers.Control | HotkeyModifiers.Shift, Keys.Escape, "Ctrl+Shift+Esc 由任务管理器占用"),
        (HotkeyModifiers.Control, Keys.Space, "Ctrl+Space 通常由输入法切换占用"),
        (HotkeyModifiers.None, Keys.PrintScreen, "PrintScreen 通常被系统截图设置占用"),
    ];

    /// <summary>绝对无法注册的组合，直接拒绝。</summary>
    private static readonly (HotkeyModifiers Mods, Keys Key, string Why)[] Impossible =
    [
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.Delete,
            "Ctrl+Alt+Del 是系统安全注意序列（SAS），任何程序都无法注册"),
    ];

    public static bool IsModifierKey(Keys key) => key
        is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.Menu or Keys.LMenu or Keys.RMenu
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
        or Keys.LWin or Keys.RWin;

    public static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + ((int)key - (int)Keys.NumPad0),
        Keys.Oemtilde => "`",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemPipe => "\\",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.None => "(未设置)",
        _ => key.ToString(),
    };

    public static CheckResult Check(HotkeySpec spec)
    {
        var mods = spec.EffectiveModifiers;

        if (spec.Key == Keys.None)
            return new CheckResult(CheckSeverity.Reject, "请先按下一个主键。");

        if (IsModifierKey(spec.Key))
            return new CheckResult(CheckSeverity.Reject, "主键不能是修饰键本身。");

        if (mods == HotkeyModifiers.None)
            return new CheckResult(CheckSeverity.Reject,
                "必须至少包含一个修饰键（Ctrl / Alt / Shift / Win），否则会吞掉正常按键输入。");

        foreach (var (m, k, why) in Impossible)
            if (m == mods && k == spec.Key)
                return new CheckResult(CheckSeverity.Reject, $"{why}，该组合键无法设置。");

        foreach (var (m, k, why) in SystemReserved)
            if (m == mods && k == spec.Key)
                return new CheckResult(CheckSeverity.Warn, $"{why}，多半无法设置。");

        return new CheckResult(CheckSeverity.Ok, string.Empty);
    }
}

// ---------------------------------------------------------------- 管理器

/// <summary>
/// 全局热键。槽位固定三个 id：current（当前生效）/ pending（待用户试按确认）/ probe（试注册后立刻注销）。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int IdCurrent = 0x4C50;
    private const int IdPending = 0x4C51;
    private const int IdProbe = 0x4C52;

    private readonly IntPtr _hwnd;
    private HotkeySpec? _current;
    private HotkeySpec? _pending;
    private bool _disposed;

    public HotkeyManager(IntPtr hwnd) => _hwnd = hwnd;

    public HotkeySpec? Current => _current;
    public HotkeySpec? Pending => _pending;

    /// <summary>当前生效的热键被按下。</summary>
    public event Action<HotkeySpec>? Pressed;

    /// <summary>待确认的热键被按下（用于“试按确认”）。</summary>
    public event Action<HotkeySpec>? PendingPressed;

    private static uint ToNative(HotkeySpec spec) =>
        (uint)(spec.EffectiveModifiers | HotkeyModifiers.NoRepeat);

    /// <summary>
    /// 试注册：注册成功即注销。这是 Windows 上唯一能判定“该组合键是否已被占用”的办法——
    /// 系统没有提供枚举全局热键的 API。
    /// </summary>
    public static bool TryProbe(IntPtr hwnd, HotkeySpec spec, out string? error)
    {
        if (NativeMethods.RegisterHotKey(hwnd, IdProbe, ToNative(spec), (uint)spec.Key))
        {
            NativeMethods.UnregisterHotKey(hwnd, IdProbe);
            error = null;
            return true;
        }

        error = DescribeError(Marshal.GetLastWin32Error());
        return false;
    }

    public static string DescribeError(int code) => code switch
    {
        1409 => "该组合键已被其他程序占用（ERROR_HOTKEY_ALREADY_REGISTERED）",
        1400 => "窗口句柄无效（内部错误）",
        87 => "组合键参数非法",
        5 => "权限不足",
        _ => $"注册失败（Win32 错误码 {code}）",
    };

    /// <summary>启动时注册已保存的热键。注册失败会尽量恢复原有热键，不会让用户丢掉正在用的键。</summary>
    public bool RegisterCurrent(HotkeySpec spec, out string? error)
    {
        var backup = _current;

        if (backup is not null)
        {
            NativeMethods.UnregisterHotKey(_hwnd, IdCurrent);
            _current = null;
        }

        if (NativeMethods.RegisterHotKey(_hwnd, IdCurrent, ToNative(spec), (uint)spec.Key))
        {
            _current = spec;
            error = null;
            return true;
        }

        error = DescribeError(Marshal.GetLastWin32Error());

        if (backup is not null &&
            NativeMethods.RegisterHotKey(_hwnd, IdCurrent, ToNative(backup), (uint)backup.Key))
        {
            _current = backup;
        }

        return false;
    }

    /// <summary>把新热键注册到 pending 槽，等待用户试按确认；此时旧热键仍然生效。</summary>
    public bool RegisterPending(HotkeySpec spec, out string? error)
    {
        RollbackPending();
        if (NativeMethods.RegisterHotKey(_hwnd, IdPending, ToNative(spec), (uint)spec.Key))
        {
            _pending = spec;
            error = null;
            return true;
        }

        error = DescribeError(Marshal.GetLastWin32Error());
        return false;
    }

    /// <summary>确认：pending 提升为 current，旧热键此时才被释放。失败会自动恢复旧热键。</summary>
    public bool CommitPending(out string? error)
    {
        error = null;
        if (_pending is null)
        {
            error = "没有待确认的热键";
            return false;
        }

        var next = _pending;
        var backup = _current;

        NativeMethods.UnregisterHotKey(_hwnd, IdPending);
        _pending = null;

        if (backup is not null)
        {
            NativeMethods.UnregisterHotKey(_hwnd, IdCurrent);
            _current = null;
        }

        if (NativeMethods.RegisterHotKey(_hwnd, IdCurrent, ToNative(next), (uint)next.Key))
        {
            _current = next;
            return true;
        }

        error = DescribeError(Marshal.GetLastWin32Error());

        if (backup is not null &&
            NativeMethods.RegisterHotKey(_hwnd, IdCurrent, ToNative(backup), (uint)backup.Key))
        {
            _current = backup;
        }

        return false;
    }

    /// <summary>仅供自检：把“当前热键被按下”直接投进消息处理链路，绕开系统热键派发。</summary>
    internal void SimulateCurrentPressed() =>
        NativeMethods.PostMessage(_hwnd, WmHotkey, IdCurrent, IntPtr.Zero);

    public void RollbackPending()
    {
        NativeMethods.UnregisterHotKey(_hwnd, IdPending);
        _pending = null;
    }

    public void UnregisterCurrent()
    {
        NativeMethods.UnregisterHotKey(_hwnd, IdCurrent);
        _current = null;
    }

    /// <summary>宿主窗口的 WndProc 调用它；返回 true 表示消息已消费。</summary>
    public bool HandleMessage(ref Message m)
    {
        if (m.Msg != WmHotkey) return false;

        int id = m.WParam.ToInt32();
        if (id == IdCurrent && _current is { } c)
        {
            Pressed?.Invoke(c);
            return true;
        }

        if (id == IdPending && _pending is { } p)
        {
            PendingPressed?.Invoke(p);
            return true;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RollbackPending();
        UnregisterCurrent();
    }
}

// ---------------------------------------------------------------- 宿主窗口

/// <summary>
/// 带热键消息处理的宿主窗口。全局热键必须挂在一个真实存在的窗口上，
/// 且该窗口的 WndProc 必须把 WM_HOTKEY 交给 <see cref="Hotkeys"/>——漏掉这一步，
/// 热键会「注册成功但毫无反应」。
/// </summary>
public class HotkeyHostForm : Form
{
    // 明确声明不参与设计器序列化，否则 .NET 10 的 WinForms 分析器（WFO1000）会直接报错
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HotkeyManager? Hotkeys { get; set; }

    protected override void WndProc(ref Message m)
    {
        if (Hotkeys is not null && Hotkeys.HandleMessage(ref m)) return;
        base.WndProc(ref m);
    }
}

// ---------------------------------------------------------------- 持久化

public sealed record HotkeyConfig(HotkeyModifiers Modifiers, Keys Key)
{
    /// <summary>
    /// 配置放用户漫游目录。放在 exe 旁边会出事：exe 一旦位于 Program Files
    /// 这类只读目录，保存会静默失败，用户重启后发现热键没了却毫无提示。
    /// </summary>
    public static string FilePath => Path.Combine(Folder, "hotkey.json");

    /// <summary>实际写成功的位置。两处都写不进去时为 null，界面据此提示用户。</summary>
    public static string? SavedTo { get; private set; }

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LongShot");

    /// <summary>回退位置：exe 旁边（旧版本用的就是这里）。</summary>
    internal static string LegacyPath => Path.Combine(AppContext.BaseDirectory, "hotkey.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static HotkeyConfig? Load()
    {
        foreach (var path in new[] { FilePath, LegacyPath })
        {
            try
            {
                if (!File.Exists(path)) continue;

                var config = JsonSerializer.Deserialize<HotkeyConfig>(File.ReadAllText(path), Options);
                if (config is null) continue;

                if (path == LegacyPath) config.Save();      // 迁到新位置
                return config;
            }
            catch
            {
                // 这个位置坏了就看下一个
            }
        }

        return null;
    }

    /// <summary>
    /// 优先写用户目录；写不进去（只读环境、策略限制）就退回 exe 旁边。
    /// 返回 false 表示两处都失败——必须让界面说出来，否则用户会遇到
    /// 「设置好了、重启就没了」而且完全不知道为什么。
    /// </summary>
    public bool Save()
    {
        var json = JsonSerializer.Serialize(this, Options);

        foreach (var path in new[] { FilePath, LegacyPath })
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                File.WriteAllText(path, json);
                SavedTo = path;
                return true;
            }
            catch
            {
                // 换下一个位置
            }
        }

        SavedTo = null;
        return false;
    }

    public static void Clear()
    {
        foreach (var path in new[] { FilePath, LegacyPath })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删不掉也不该崩，下次启动会重新注册
            }
        }
    }
}

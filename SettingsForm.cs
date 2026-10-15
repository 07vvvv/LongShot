using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LongShot;

/// <summary>
/// 设置界面。暖白画布 + 1px 细边卡片 + 无阴影，快捷键用等宽字体呈现。
/// 修饰键用「标签式开关」而不是原生复选框：系统复选框的 3D 边缘和这套风格割裂。
/// </summary>
public sealed class SettingsForm : HotkeyHostForm
{
    private enum StatusKind { Ok, Warn, Error }

    private static readonly Color Canvas = Color.FromArgb(0xFB, 0xFB, 0xFA);
    private static readonly Color Surface = Color.White;
    private static readonly Color Line = Color.FromArgb(0xEA, 0xEA, 0xEA);
    private static readonly Color Ink = Color.FromArgb(0x11, 0x11, 0x11);
    private static readonly Color Muted = Color.FromArgb(0x78, 0x77, 0x74);
    private static readonly Color Hover = Color.FromArgb(0xF2, 0xF1, 0xEE);
    private static readonly Color OkInk = Color.FromArgb(0x34, 0x65, 0x38);
    private static readonly Color WarnInk = Color.FromArgb(0x95, 0x64, 0x00);
    private static readonly Color ErrorInk = Color.FromArgb(0x9F, 0x2F, 0x2D);

    private readonly Font _titleFont = new("Microsoft YaHei UI", 15F, FontStyle.Bold);
    private readonly Font _bodyFont = new("Microsoft YaHei UI", 9F);
    private readonly Font _metaFont = new("Microsoft YaHei UI", 8.25F);
    private readonly Font _monoFont = new("Consolas", 11F);

    private readonly ModifierToggle _tglCtrl = new(HotkeyModifiers.Control, "Ctrl");
    private readonly ModifierToggle _tglAlt = new(HotkeyModifiers.Alt, "Alt");
    private readonly ModifierToggle _tglShift = new(HotkeyModifiers.Shift, "Shift");
    private readonly ModifierToggle _tglWin = new(HotkeyModifiers.Win, "Win");

    private readonly TextBox _txtKey = new();
    private readonly Label _lblCurrent = new();
    private readonly Label _lblStatus = new();
    private readonly Button _btnClear = new() { Text = "清除" };
    private readonly Button _btnApply = new() { Text = "保存并应用" };

    private readonly System.Windows.Forms.Timer _confirmTimer = new() { Interval = 5000 };
    private TaskCompletionSource<bool>? _confirm;
    private RegionSelectorForm? _capture;
    private Keys _key = Keys.None;

    public SettingsForm()
    {
        Text = "LongShot 设置";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowIcon = true;        // 有图标了，任务栏/Alt+Tab 里能认出来
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 430);
        BackColor = Canvas;
        Font = _bodyFont;

        BuildLayout();

        _txtKey.KeyDown += OnKeyDownCapture;
        _btnApply.Click += OnApplyClick;
        _btnClear.Click += OnClearClick;
        _confirmTimer.Tick += OnConfirmTimeout;
    }

    // ------------------------------------------------------------ 外观

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "LongShot",
            Font = _titleFont,
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(30, 24),
        };

        var subtitle = new Label
        {
            Text = "Windows 滚动截图 · 全局快捷键",
            Font = _bodyFont,
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(32, 56),
        };

        var rule = new Panel { Location = new Point(30, 88), Size = new Size(400, 1), BackColor = Line };

        var captionKeys = Section("快捷键", 106);
        var card = Card(new Point(30, 130), new Size(400, 116));
        LayoutModifiers(card);
        LayoutKeyBox(card);

        var captionCurrent = Section("当前生效", 262);

        _lblCurrent.Font = _monoFont;
        _lblCurrent.ForeColor = Muted;
        _lblCurrent.AutoSize = true;
        _lblCurrent.Text = "—";
        _lblCurrent.Location = new Point(30, 284);

        StyleSecondary(_btnClear);
        _btnClear.Size = new Size(78, 30);
        _btnClear.Location = new Point(352, 278);

        _lblStatus.Font = _metaFont;
        _lblStatus.ForeColor = Muted;
        _lblStatus.AutoSize = false;
        _lblStatus.BackColor = Surface;
        _lblStatus.Padding = new Padding(12, 10, 12, 10);
        _lblStatus.Location = new Point(30, 322);
        _lblStatus.Size = new Size(400, 58);
        _lblStatus.Paint += (_, e) =>
        {
            using var pen = new Pen(Line, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, _lblStatus.Width - 1, _lblStatus.Height - 1);
        };

        StylePrimary(_btnApply);
        _btnApply.Size = new Size(130, 34);
        _btnApply.Location = new Point(300, 390);

        Controls.AddRange([
            title, subtitle, rule,
            captionKeys, card,
            captionCurrent, _lblCurrent, _btnClear,
            _lblStatus, _btnApply,
        ]);
    }

    private Label Section(string text, int y) => new()
    {
        Text = text,
        Font = _metaFont,
        ForeColor = Muted,
        AutoSize = true,
        Location = new Point(30, y),
    };

    private static Panel Card(Point location, Size size)
    {
        var panel = new Panel { Location = location, Size = size, BackColor = Surface };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Line, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    private void LayoutModifiers(Panel card)
    {
        var toggles = new[] { _tglCtrl, _tglAlt, _tglShift, _tglWin };
        var x = 22;

        foreach (var toggle in toggles)
        {
            toggle.Location = new Point(x, 18);
            card.Controls.Add(toggle);
            x += toggle.Width + 12;
        }
    }

    private void LayoutKeyBox(Panel card)
    {
        var caption = new Label
        {
            Text = "主键",
            Font = _bodyFont,
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(22, 68),
        };

        var holder = new Panel
        {
            Location = new Point(74, 60),
            Size = new Size(150, 32),
            BackColor = Surface,
        };
        holder.Paint += (_, e) =>
        {
            using var pen = new Pen(Line, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, holder.Width - 1, holder.Height - 1);
        };

        _txtKey.BorderStyle = BorderStyle.None;
        _txtKey.Font = _monoFont;
        _txtKey.ForeColor = Ink;
        _txtKey.BackColor = Surface;
        _txtKey.ReadOnly = true;
        _txtKey.TabStop = false;        // 不自动抢焦点，否则显示时会被整段蓝色选中
        _txtKey.Text = string.Empty;
        _txtKey.Location = new Point(10, 7);
        _txtKey.Width = 128;

        var hint = new Label
        {
            Text = "点这里，然后按任意键",
            Font = _metaFont,
            ForeColor = Muted,
            AutoSize = true,
            Location = new Point(236, 68),
        };

        holder.Controls.Add(_txtKey);
        card.Controls.Add(caption);
        card.Controls.Add(holder);
        card.Controls.Add(hint);
    }

    private static void StylePrimary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x33, 0x33, 0x33);
        button.FlatAppearance.MouseDownBackColor = Color.Black;
        button.BackColor = Ink;
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
    }

    private static void StyleSecondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Line;
        button.FlatAppearance.MouseOverBackColor = Hover;
        button.BackColor = Surface;
        button.ForeColor = Ink;
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
    }

    /// <summary>标签式修饰键开关：选中是实心黑，未选中是白底细边。</summary>
    private sealed class ModifierToggle : Button
    {
        public ModifierToggle(HotkeyModifiers modifier, string text)
        {
            Modifier = modifier;
            Text = text;
            Size = new Size(74, 32);
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            TabStop = false;
            Apply(false);

            Click += (_, _) => Apply(!IsOn);
        }

        public HotkeyModifiers Modifier { get; }

        public bool IsOn { get; private set; }

        public void Apply(bool on)
        {
            IsOn = on;
            BackColor = on ? Ink : Surface;
            ForeColor = on ? Color.White : Ink;
            FlatAppearance.BorderSize = 1;
            FlatAppearance.BorderColor = on ? Ink : Line;
            FlatAppearance.MouseOverBackColor = on ? Color.FromArgb(0x33, 0x33, 0x33) : Hover;
            Invalidate();
        }
    }

    // ------------------------------------------------------------ 生命周期

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // ponytail: 热键绑在窗口句柄上。若 WinForms 重建句柄（改 FormBorderStyle / ShowInTaskbar 等会触发），
        // 这里会因 Hotkeys 非空而跳过重注册，热键随即失效。当前这些属性都在构造函数里一次设好，不会重建；
        // 将来若要动态改窗口样式，需在这里比对句柄并重新注册。
        if (Hotkeys is not null) return;

        Hotkeys = new HotkeyManager(Handle);
        Hotkeys.Pressed += OnHotkeyPressed;
        Hotkeys.PendingPressed += _ => _confirm?.TrySetResult(true);

        LoadSavedHotkey();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        EnsureOnScreen();

        ActiveControl = null;       // 谁也别抢焦点，窗口一出来就是干净的
        StartLog.Write($"窗口已显示，进程创建至今 {StartLog.SinceProcessStart()}");
    }

    /// <summary>
    /// 把窗口从「快捷方式式启动」里拽出来。
    ///
    /// 桌面快捷方式可以设「运行方式：最小化」，Windows 会经 STARTUPINFO.wShowWindow 传
    /// SW_SHOWMINNOACTIVE；而 Windows 又在进程**第一次** ShowWindow 时拿它覆盖程序自己的请求。
    /// 结果就是从快捷方式启动时窗口缩在任务栏不肯露面，用户只能认定「打不开」，
    /// 而进文件夹双击 exe（wShowWindow 正常）一切如常。所以显示之后再校正一次。
    /// </summary>
    private void EnsureOnScreen()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
            StartLog.Write("窗口启动时处于最小化（快捷方式的「运行方式」所致），已恢复正常大小");
        }

        if (!Visible) Show();

        Activate();

        // 抢前台：Windows 只把前台权限交给当前前台进程，被拒是常态，闪任务栏兜个底
        if (!SetForegroundWindow(Handle)) FlashWindow(Handle, true);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hWnd, bool invert);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Hotkeys?.Dispose();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------ 事件

    private void OnKeyDownCapture(object? sender, KeyEventArgs e)
    {
        if (HotkeyRules.IsModifierKey(e.KeyCode))
        {
            e.SuppressKeyPress = true;
            return;
        }

        _key = e.KeyCode;
        _txtKey.Text = HotkeyRules.KeyName(_key);
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    private void OnConfirmTimeout(object? sender, EventArgs e)
    {
        _confirmTimer.Stop();
        _confirm?.TrySetResult(false);
    }

    private void OnClearClick(object? sender, EventArgs e)
    {
        Hotkeys?.UnregisterCurrent();
        HotkeyConfig.Clear();

        _tglCtrl.Apply(false);
        _tglAlt.Apply(false);
        _tglShift.Apply(false);
        _tglWin.Apply(false);
        _key = Keys.None;
        _txtKey.Text = string.Empty;

        _lblCurrent.Text = "—";
        _lblCurrent.ForeColor = Muted;
        SetStatus("已清除快捷键，配置文件也已删除。现在按任何组合键都不会触发截图。", StatusKind.Warn);
    }

    /// <summary>
    /// 热键是全局的、不依赖焦点，所以「再按一次结束」这条路在选区窗口失去焦点后依然有效
    /// ——这是截图流程唯一的结束入口保证。
    /// </summary>
    private void OnHotkeyPressed(HotkeySpec spec)
    {
        if (_capture is { IsDisposed: false })
        {
            _capture.FinishCapture();
            return;
        }

        var capture = new RegionSelectorForm();
        capture.FormClosed += (_, _) =>
        {
            _capture = null;
            if (IsDisposed) return;     // 主窗口先关时别再碰已经释放的控件
            SetStatus($"截图流程结束。长图已保存到 {ScreenGrabber.OutputFolder()}", StatusKind.Ok);
        };

        _capture = capture;
        capture.Show();
        SetStatus($"已进入截图：拖拽框选区域，然后滚动鼠标滚轮；再按一次 {spec.Display} 结束并生成长图。",
            StatusKind.Ok);
    }

    private async void OnApplyClick(object? sender, EventArgs e)
    {
        if (Hotkeys is null) return;

        var spec = BuildSpec();

        // ① 本地规则校验
        var check = HotkeyRules.Check(spec);
        if (!check.CanProceed)
        {
            SetStatus(check.Message, StatusKind.Error);
            return;
        }

        _btnApply.Enabled = false;
        try
        {
            // ② 试注册：Windows 上唯一可靠的冲突判定
            if (!HotkeyManager.TryProbe(Handle, spec, out var err))
            {
                SetStatus($"无法设置 {spec.Display}：{err}。请更换其他组合键。", StatusKind.Error);
                return;
            }

            if (!Hotkeys.RegisterPending(spec, out err))
            {
                SetStatus($"无法设置 {spec.Display}：{err}。请更换其他组合键。", StatusKind.Error);
                return;
            }

            // 非 Win 组合直接落地；含 Win 的组合存在“注册成功但收不到”的盲区，需要试按确认
            if (!spec.HasWin)
            {
                Commit(spec);
                return;
            }

            var suffix = string.IsNullOrEmpty(check.Message) ? string.Empty : $"（注意：{check.Message}）";
            _confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SetStatus($"请在 5 秒内按一次 {spec.Display} 完成确认……{suffix}", StatusKind.Warn);
            _confirmTimer.Start();

            var confirmed = await _confirm.Task;
            _confirmTimer.Stop();

            if (!confirmed)
            {
                Hotkeys.RollbackPending();
                SetStatus($"{spec.Display} 没有收到按键——该系统组合很可能被 Windows 优先占用。" +
                          "该组合键无法设置，请更换。", StatusKind.Error);
                return;
            }

            Commit(spec);
        }
        catch (Exception ex)
        {
            // async void 里漏出去的异常会直接终止进程，这里必须兜住
            SetStatus($"操作失败：{ex.Message}", StatusKind.Error);
        }
        finally
        {
            _confirm = null;
            _btnApply.Enabled = true;
        }
    }

    // ------------------------------------------------------------ 内部

    private HotkeySpec BuildSpec()
    {
        var mods = HotkeyModifiers.None;
        if (_tglCtrl.IsOn) mods |= HotkeyModifiers.Control;
        if (_tglAlt.IsOn) mods |= HotkeyModifiers.Alt;
        if (_tglShift.IsOn) mods |= HotkeyModifiers.Shift;
        if (_tglWin.IsOn) mods |= HotkeyModifiers.Win;
        return new HotkeySpec(mods, _key);
    }

    /// <summary>把一份热键写回控件，保证界面显示的就是当前真正生效的组合。</summary>
    private void ShowSpec(HotkeySpec spec)
    {
        _tglCtrl.Apply(spec.EffectiveModifiers.HasFlag(HotkeyModifiers.Control));
        _tglAlt.Apply(spec.EffectiveModifiers.HasFlag(HotkeyModifiers.Alt));
        _tglShift.Apply(spec.EffectiveModifiers.HasFlag(HotkeyModifiers.Shift));
        _tglWin.Apply(spec.EffectiveModifiers.HasFlag(HotkeyModifiers.Win));

        _key = spec.Key;
        _txtKey.Text = HotkeyRules.KeyName(spec.Key);

        _lblCurrent.Text = spec.Display;
        _lblCurrent.ForeColor = Ink;
    }

    private void Commit(HotkeySpec spec)
    {
        if (!Hotkeys!.CommitPending(out var err))
        {
            SetStatus($"应用失败：{err}", StatusKind.Error);
            return;
        }

        ShowSpec(spec);

        var saved = new HotkeyConfig(spec.EffectiveModifiers, spec.Key).Save();
        SetStatus(saved
                ? $"已应用 {spec.Display}。全局生效；配置已保存到 {HotkeyConfig.SavedTo}"
                : $"已应用 {spec.Display}（本次有效），但配置没能写入磁盘，重启后要重新设置。",
            saved ? StatusKind.Ok : StatusKind.Warn);
    }

    private void LoadSavedHotkey()
    {
        var cfg = HotkeyConfig.Load();
        if (cfg is null)
        {
            OfferDefaultHotkey();
            return;
        }

        var spec = new HotkeySpec(cfg.Modifiers, cfg.Key);
        if (Hotkeys!.RegisterCurrent(spec, out var err))
        {
            ShowSpec(spec);
            SetStatus($"已启用上次保存的快捷键 {spec.Display}。", StatusKind.Ok);
        }
        else
        {
            ShowSpec(spec);
            _lblCurrent.ForeColor = Muted;
            SetStatus($"上次保存的 {spec.Display} 这次没能启用：{err}。请重新设置。", StatusKind.Error);
        }
    }

    /// <summary>
    /// 首次运行直接启用一个默认热键：否则用户装完打开程序，看到的只是一句
    /// 「请先设置快捷键」，还得自己猜该设什么。占用失败就退回引导文案。
    /// </summary>
    private void OfferDefaultHotkey()
    {
        var fallback = new HotkeySpec(HotkeyModifiers.Control | HotkeyModifiers.Alt, Keys.L);
        ShowSpec(fallback);

        if (Hotkeys!.RegisterCurrent(fallback, out var err))
        {
            var saved = new HotkeyConfig(fallback.EffectiveModifiers, fallback.Key).Save();
            var note = saved ? string.Empty : "（配置没能写入磁盘，重启后需重新设置）";
            SetStatus($"首次使用，已自动启用 {fallback.Display}。想换键位就重新勾选再点「保存并应用」。{note}",
                saved ? StatusKind.Ok : StatusKind.Warn);
            return;
        }

        _lblCurrent.ForeColor = Muted;
        SetStatus($"首次使用。默认的 {fallback.Display} 已被别的程序占用（{err}），" +
                  "请自己选一个组合，再点「保存并应用」。", StatusKind.Warn);
    }

    private void SetStatus(string text, StatusKind kind)
    {
        // 所有关键节点都从状态栏流过：配置加载、热键注册结果、截图流程。
        // 记在这里，一次改动就覆盖了整条启动链路。
        StartLog.Write($"[{kind}] {text}");

        _lblStatus.Text = text;
        _lblStatus.ForeColor = kind switch
        {
            StatusKind.Ok => OkInk,
            StatusKind.Warn => WarnInk,
            _ => ErrorInk,
        };
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LongShot;

/// <summary>
/// 选区窗口，两个阶段：
///   1）选择：整个屏幕压一层 35% 黑遮罩，鼠标可以正常拖拽框选。
///   2）抓取：主窗口彻底透明（Opacity = 0）并对鼠标完全穿透，用户像平时一样点击和滚动下层应用；
///            进度与「完成」入口交给独立的浮层小卡片 CaptureHud。
///
/// 两个踩过的坑，别改回去：
///   · 用 TransparencyKey 做遮罩 → 色键透明的区域连鼠标一起穿透，框选收不到 MouseDown。
///   · 抓取阶段用「半透明 + SetWindowRgn 挖洞」→ 实测抓帧仍会被自己的遮罩压暗（平均灰度差 41.9）。
/// </summary>
public sealed class RegionSelectorForm : Form
{
    private const int CaptureIntervalMs = 200;

    /// <summary>连续这么多帧都配不上，就重设基准（约 1 秒）。</summary>
    private const int RebaselineAfterMisses = 5;
    private const double MaskOpacity = 0.35;
    private const int HudWidth = 352;
    private const int HudHeight = 64;

    private static readonly Color Line = Color.FromArgb(0xEA, 0xEA, 0xEA);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = CaptureIntervalMs };

    // 低级鼠标钩子必须在字段里保住引用，否则会被 GC 回收导致钩子失效
    private readonly CaptureNative.LowLevelMouseProc _mouseProc;
    private IntPtr _mouseHook;

    private Point _dragStart;
    private bool _dragging;
    private Rectangle _selection;
    private bool _capturing;
    private CaptureHud? _hud;
    private bool _hudOverlapsSelection;

    private ScreenFrame? _previous;
    private ScreenFrame? _first;
    private readonly ScrollAccumulator _accumulator = new();
    private int _skipped;
    private int _missStreak;

    /// <summary>上一步的位移，作为配准打平时的裁决依据（内容有周期性时只有它能分辨真假峰）。</summary>
    private int _lastDh;

    public RegionSelectorForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
        Opacity = MaskOpacity;      // 遮罩：看得见屏幕内容，同时鼠标仍由本窗口接收
        Text = "LongShot 选区";

        _mouseProc = OnMouseHook;
        _timer.Tick += (_, _) => GrabOnce();
    }

    internal bool IsCapturing => _capturing;

    /// <summary>仅供自检：鼠标钩子是否装上了。</summary>
    internal bool HookInstalled => _mouseHook != IntPtr.Zero;

    /// <summary>仅供自检：抓取浮层当前是否可见。</summary>
    internal bool HudVisible => _hud is { Visible: true };

    /// <summary>仅供自检：避免测试时真的保存图片并弹出查看器。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool SuppressOutputForTest { get; set; }

    /// <summary>仅供自检：把一条鼠标消息直接喂给钩子回调，返回 true 表示被吞掉。</summary>
    internal bool InvokeHookForTest(int message) =>
        OnMouseHook(0, new IntPtr(message), IntPtr.Zero) != IntPtr.Zero;

    /// <summary>仅供自检：本窗口是否收到过鼠标移动。</summary>
    internal bool SawMouseMove { get; private set; }

    internal void ResetMouseFlag() => SawMouseMove = false;

    /// <summary>仅供自检：跳过鼠标拖拽，直接设定选区并进入抓取阶段。</summary>
    internal void SimulateSelection(Rectangle clientRect)
    {
        _selection = clientRect;
        BeginCapture();
    }

    // ------------------------------------------------------------ 绘制（只在选择阶段可见）

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_selection.Width > 0 && _selection.Height > 0)
        {
            // 边框整条画在选区外侧：任何背景下都看得见，且不侵入抓帧区域
            using var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 1);
            e.Graphics.DrawRectangle(shadow, _selection.X - 7, _selection.Y - 7,
                _selection.Width + 12, _selection.Height + 12);

            using var border = new Pen(Color.White, 2);
            e.Graphics.DrawRectangle(border, _selection.X - 5, _selection.Y - 5,
                _selection.Width + 8, _selection.Height + 8);
        }
        else
        {
            var text = "拖拽框选要截取的区域　·　Esc 取消";
            using var font = new Font("Microsoft YaHei UI", 10.5F);
            var size = e.Graphics.MeasureString(text, font);
            var card = new RectangleF((Width - size.Width) / 2f - 18, 28f, size.Width + 36, size.Height + 22);

            e.Graphics.FillRectangle(Brushes.White, card);
            using var pen = new Pen(Line, 1);
            e.Graphics.DrawRectangle(pen, card.X, card.Y, card.Width - 1, card.Height - 1);
            e.Graphics.DrawString(text, font, Brushes.Black, card.X + 18, card.Y + 11);
        }
    }

    // ------------------------------------------------------------ 选区

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_capturing) return;
        _dragging = true;
        _dragStart = e.Location;
        _selection = Rectangle.Empty;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        SawMouseMove = true;
        if (!_dragging) return;
        _selection = Normalize(_dragStart, e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;

        _selection = Normalize(_dragStart, e.Location);
        if (_selection.Width < 16 || _selection.Height < 32)
        {
            _selection = Rectangle.Empty;
            Invalidate();
            return;
        }

        BeginCapture();
    }

    private static Rectangle Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    // ------------------------------------------------------------ 抓帧

    private void BeginCapture()
    {
        _capturing = true;
        Cursor = Cursors.Default;

        // 彻底透明：抓帧拍到的必然是下层真实画面。比「半透明 + 挖洞」确定得多。
        Opacity = 0;

        // 鼠标整体穿透，滚轮才能正常滚动下层内容
        CaptureNative.MakeClickThrough(Handle);

        ShowHud();
        InstallMouseHook();

        GrabOnce();
        _timer.Start();
    }

    private void InstallMouseHook()
    {
        _mouseHook = CaptureNative.SetWindowsHookEx(
            CaptureNative.WhMouseLl, _mouseProc, IntPtr.Zero, 0);

        if (_mouseHook == IntPtr.Zero)
            _hud?.SetTitle("左键结束不可用，请再按一次热键结束");
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        CaptureNative.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    /// <summary>
    /// 低级鼠标钩子：滚轮放行（下层应用照常滚动），左键吞掉并结束截图，
    /// 右键也吞掉（否则会在下层应用弹出右键菜单）。
    /// 钩子回调里绝不能做重活——超时会被系统摘钩，所以结束动作异步派回 UI 线程。
    /// </summary>
    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 已经不在抓取状态就一律放行：收尾瞬间到达的点击不能被吞掉，
        // 否则用户会发现点哪儿都没反应。
        if (nCode >= 0 && _capturing)
        {
            var message = wParam.ToInt32();

            if (message == CaptureNative.WmLeftButtonDown)
            {
                try
                {
                    BeginInvoke((Action)FinishCapture);
                }
                catch (InvalidOperationException)
                {
                    // 窗口正在销毁。钩子回调里抛异常会被系统把钩子摘掉，这里必须吞掉。
                }

                return new IntPtr(1);
            }

            if (message == CaptureNative.WmRightButtonDown) return new IntPtr(1);
        }

        return CaptureNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void ShowHud()
    {
        _hud?.Close();      // 重复进入抓取时别把上一个浮层丢在屏幕上

        var (x, y) = PlaceHud();

        _hud = new CaptureHud(FinishCapture);
        _hud.SetBounds(x, y, HudWidth, HudHeight);
        _hud.Show();

        // 浮层若压住了选区，抓帧时必须临时藏起来，否则会被拍进长图
        var inClient = new Rectangle(x - Bounds.Left, y - Bounds.Top, HudWidth, HudHeight);
        _hudOverlapsSelection = _selection.IntersectsWith(inClient);
    }

    /// <summary>
    /// 浮层位置：优先选区上方，其次下方，都放不下（选区几乎占满屏幕）就贴右下角。
    /// 以前这里直接放弃显示，结果是用户完全看不到提示、不知道左键能结束。
    /// </summary>
    private (int X, int Y) PlaceHud()
    {
        var x = _selection.X + (_selection.Width - HudWidth) / 2 + Bounds.Left;
        x = Math.Max(Bounds.Left + 8, Math.Min(x, Bounds.Right - HudWidth - 8));

        var above = _selection.Y - HudHeight - 12 + Bounds.Top;
        if (above >= Bounds.Top + 8) return (x, above);

        var below = _selection.Bottom + 12 + Bounds.Top;
        if (below + HudHeight <= Bounds.Bottom - 8) return (x, below);

        return (Bounds.Right - HudWidth - 16, Bounds.Bottom - HudHeight - 16);
    }

    private void GrabOnce()
    {
        try
        {
            GrabOnceCore();
        }
        catch (Exception ex)
        {
            // 抓帧或配准出任何意外，都不该让异常从定时器抛进消息循环——那会直接把程序崩掉。
            // 但也不能一声不吭：留下日志，否则用户看到的是「卡住」，而现场什么都没有。
            _skipped++;
            StartLog.Write($"抓帧失败，已跳过一帧：{ex.GetType().Name} {ex.Message}");
            UpdateHud();
        }
    }

    private void GrabOnceCore()
    {
        var screenRect = new Rectangle(
            _selection.X + Bounds.Left,
            _selection.Y + Bounds.Top,
            _selection.Width,
            _selection.Height);

        // 浮层压在选区上时先藏起来，抓完立刻恢复；多等 30ms 让隐藏真正合成到屏幕上
        var hideHud = _hudOverlapsSelection && _hud is { Visible: true };
        if (hideHud)
        {
            _hud!.Visible = false;
            Thread.Sleep(30);
        }

        ScreenFrame frame;
        try
        {
            frame = ScreenGrabber.Grab(screenRect);
        }
        finally
        {
            if (hideHud) _hud!.Visible = true;
        }

        if (_previous is null)
        {
            _accumulator.AddFirstFrame(frame);
            _previous = frame;
            _first = frame;
            UpdateHud();
            return;
        }

        if (!FrameStitcher.TryEstimateOffset(_previous, frame, out var dh, out _, _lastDh)
            && !TryRealignToFirst(frame, out dh))
        {
            // 基准失效时不能顺手把当前帧当成新基准：累积器的游标还停在旧位置，
            // 后续以新基准为原点算出的位移会让内容整体错位（少一段却被当作已截到）。
            // 一帧对不上就先保留旧基准继续追；连续多帧都对不上才重设基准，
            // 此时容忍结果里出现一次可见的跳跃——总好过永远配不上、彻底卡死。
            _skipped++;
            _missStreak++;

            if (_missStreak >= RebaselineAfterMisses)
            {
                _previous = frame;
                _accumulator.ResetBaseline();
                _missStreak = 0;
                _lastDh = 0;        // 基准换了，上一步位移不再有参考意义
            }

            UpdateHud();
            return;
        }

        _missStreak = 0;
        _accumulator.Push(frame, dh);
        if (dh != 0) _lastDh = dh;      // 停手（dh=0）时不清零：下一帧还要靠它分辨周期内容的真假峰

        _previous = frame;
        UpdateHud();
    }

    /// <summary>
    /// 与上一帧对不上时的退路：直接跟首帧对。用户一次滚得比一屏还多时相邻两帧毫无重叠，
    /// 配准在数学上不可能，但当前帧往往还与首帧有重叠——对上了就能把「当前帧在内容坐标里的
    /// 绝对位置」一次性拿回来（dh 换算成相对上一帧的位移），比让累积器的游标停在旧位置继续
    /// 往前滚要准得多。
    /// </summary>
    private bool TryRealignToFirst(ScreenFrame frame, out int dh)
    {
        dh = 0;

        if (_first is null) return false;
        if (!FrameStitcher.TryEstimateOffset(_first, frame, out var absolute, out _)) return false;

        dh = absolute - _accumulator.Cursor;
        return dh != 0;
    }

    private void UpdateHud() =>
        _hud?.UpdateStatus(_accumulator.Segments, _accumulator.Rows, _skipped, _accumulator.LimitReached);

    // ------------------------------------------------------------ 结束

    public void FinishCapture()
    {
        UninstallMouseHook();

        if (!_capturing)
        {
            Close();
            return;
        }

        _timer.Stop();
        _capturing = false;

        if (SuppressOutputForTest || !_accumulator.HasContent)
        {
            Close();
            return;
        }

        string? path = null;
        try
        {
            using var bmp = _accumulator.BuildBitmap();
            path = SaveAndCopy(bmp);
        }
        catch (Exception ex)
        {
            // 生成失败也不能把用户卡在窗口里，但必须留下现场——否则用户只看到「什么都没发生」
            StartLog.Write($"长图生成失败：{ex.GetType().Name} {ex.Message}");
        }

        Close();

        if (path is null)
        {
            StartLog.Write("长图保存失败：没有可用输出目录或写入被拒");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 没有关联程序就静默失败，文件已经在磁盘上
        }
    }

    private static string? SaveAndCopy(Bitmap bmp)
    {
        try
        {
            var path = Path.Combine(
                ScreenGrabber.OutputFolder(),
                $"longshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);

            try
            {
                Clipboard.SetImage(bmp);
            }
            catch
            {
                // 剪贴板被别的程序占着，忽略
            }

            return path;
        }
        catch (Exception ex)
        {
            StartLog.Write($"保存长图失败：{ex.GetType().Name} {ex.Message}　目录={ScreenGrabber.OutputFolder()}");
            return null;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                _timer.Stop();
                Close();
                return;
            case Keys.Enter:
                FinishCapture();
                return;
            default:
                base.OnKeyDown(e);
                return;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UninstallMouseHook();
        _timer.Stop();
        _timer.Dispose();
        _hud?.Close();
        base.OnFormClosed(e);
    }
}

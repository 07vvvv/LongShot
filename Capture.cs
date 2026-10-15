using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LongShot;

// ---------------------------------------------------------------- Win32

internal static class CaptureNative
{
    private const int GwlExstyle = -20;
    public const int WsExTransparent = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private const uint PwRenderFullContent = 0x00000002;

    // ---- 低级鼠标钩子：抓取阶段要「滚轮放行、左键吞掉」只能靠它 ----
    public const int WhMouseLl = 14;
    public const int WmLeftButtonDown = 0x0201;
    public const int WmRightButtonDown = 0x0204;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>把窗口硬推到最顶层（比 TopMost 属性更硬），自检时用来排除遮挡干扰。</summary>
    public static void ForceTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);

    /// <summary>
    /// 让窗口把自己重绘到内存 DC，不依赖它在屏幕上有没有被别的窗口盖住。
    /// 自检抓界面截图必须走这条，CopyFromScreen 抓到的是别人的窗口。
    /// </summary>
    public static Bitmap CaptureWindow(Control control)
    {
        var bmp = new Bitmap(control.Width, control.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try
        {
            PrintWindow(control.Handle, hdc, PwRenderFullContent);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }

        return bmp;
    }

    /// <summary>让窗口对鼠标完全穿透（含滚轮），用于选区确定后不再拦截用户操作。</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        if (IntPtr.Size == 8)
        {
            var ex = GetWindowLongPtr64(hwnd, GwlExstyle).ToInt64();
            SetWindowLongPtr64(hwnd, GwlExstyle, new IntPtr(ex | WsExTransparent));
        }
        else
        {
            var ex = GetWindowLong32(hwnd, GwlExstyle);
            SetWindowLong32(hwnd, GwlExstyle, ex | WsExTransparent);
        }
    }

    /// <summary>读取窗口扩展样式，用于自检确认穿透位有没有真的设上。</summary>
    public static long GetExStyle(IntPtr hwnd) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hwnd, GwlExstyle).ToInt64()
        : GetWindowLong32(hwnd, GwlExstyle);
}

// ---------------------------------------------------------------- 帧

/// <summary>一帧屏幕内容：灰度用于配准，彩色用于最终输出。</summary>
public sealed class ScreenFrame
{
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Width × Height 的灰度值，用于估算滚动偏移。</summary>
    public byte[] Gray { get; init; } = [];

    /// <summary>Width × Height 的 32bpp ARGB 像素，用于生成最终长图。</summary>
    public int[] Pixels { get; init; } = [];
}

public static class ScreenGrabber
{
    /// <summary>
    /// 长图输出目录。按优先级挑第一个**确实可写**的：图片目录 → exe 旁边的 captures → 临时目录。
    ///
    /// 这个方法刻意不抛异常：它以前只有一句 Directory.CreateDirectory，
    /// 在「图片」目录不可写（权限策略、OneDrive 重定向、安全软件保护）时会直接抛
    /// UnauthorizedAccessException，把整个程序崩成一个「未经处理的异常」对话框。
    /// </summary>
    public static string OutputFolder()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        var candidates = new[]
        {
            string.IsNullOrEmpty(pictures) ? string.Empty : Path.Combine(pictures, "LongShot"),
            Path.Combine(AppContext.BaseDirectory, "captures"),
            Path.Combine(Path.GetTempPath(), "LongShot"),
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;

            try
            {
                Directory.CreateDirectory(candidate);

                // 建得出来不代表写得进去：探一下再认，免得等到真正保存时才失败
                var probe = Path.Combine(candidate, ".writable");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);

                return candidate;
            }
            catch
            {
                // 换下一个候选
            }
        }

        return Path.GetTempPath();      // 最后的兜底：临时目录一定存在
    }

    public static ScreenFrame Grab(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException("选区为空", nameof(rect));

        var w = rect.Width;
        var h = rect.Height;
        var pixels = new int[w * h];
        var gray = new byte[w * h];

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new int[w];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, line, 0, w);
                var offset = y * w;
                for (var x = 0; x < w; x++)
                {
                    var c = line[x];
                    pixels[offset + x] = c;
                    var b = c & 0xFF;
                    var gch = (c >> 8) & 0xFF;
                    var r = (c >> 16) & 0xFF;
                    gray[offset + x] = (byte)((r * 299 + gch * 587 + b * 114) / 1000);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return new ScreenFrame { Width = w, Height = h, Gray = gray, Pixels = pixels };
    }

    /// <summary>把累积的像素行写成位图。rows 是「每行 width 个像素」首尾相接的扁平数组。</summary>
    public static Bitmap BuildBitmap(List<int> rows, int width)
    {
        var rowCount = rows.Count / width;
        if (rowCount <= 0) throw new ArgumentException("没有任何像素行", nameof(rows));

        var bmp = new Bitmap(width, rowCount, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, rowCount), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new int[width];
            for (var y = 0; y < rowCount; y++)
            {
                rows.CopyTo(y * width, line, 0, width);
                Marshal.Copy(line, 0, data.Scan0 + y * data.Stride, width);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }
}

// ---------------------------------------------------------------- 配准

public static class FrameStitcher
{
    /// <summary>平均灰度差超过它就不信任这次对齐（0–255）。</summary>
    public const double TrustThreshold = 10.0;

    /// <summary>低于它就认为画面根本没动（用户没滚）。</summary>
    private const double StillThreshold = 1.0;

    private const int MinOverlap = 8;

    /// <summary>粗搜后取前 K 个候选分别精修。</summary>
    private const int CoarseTopK = 5;

    /// <summary>
    /// 重叠区至少要有多高才算有效候选。太小的话样本太少，无纹理内容也能「碰巧匹配」，
    /// 会把一次大幅滚动误判成小重叠下的完美匹配，中间内容整段丢失。
    /// </summary>
    private static int MinOverlapFor(int h) => Math.Max(24, h / 8);

    /// <summary>
    /// 估算 next 相对 prev 的纵向滚动量 dh：dh &gt; 0 表示向下滚了 dh 行（next[y] ≈ prev[y+dh]），
    /// dh &lt; 0 表示向上滚（next[y+|dh|] ≈ prev[y]）。
    /// 返回 false 表示这次对齐不可信，应丢弃该帧。
    ///
    /// 这里刻意不用「上一步长」缩小搜索范围来提速：那样省下的是几十毫秒，
    /// 代价却是用户反向滚动或步长突变时，范围外的真实位移被漏掉、范围里的
    /// 周期性伪峰冒充答案，往长图里塞进错位内容。实测全范围搜索 800×900 只要 ~30ms，
    /// 换来的是这类错误整类消失。
    /// </summary>
    public static bool TryEstimateOffset(ScreenFrame prev, ScreenFrame next, out int dh, out double score)
    {
        dh = 0;
        score = double.MaxValue;

        var w = Math.Min(prev.Width, next.Width);
        var h = Math.Min(prev.Height, next.Height);
        if (w <= 0 || h < MinOverlap * 3) return false;

        // 画面几乎没动 = 用户没在滚。必须先判这一条：候选范围排除了 0，
        // 否则「完全没滚动」会被匹配成「滚了几像素」，每次都往长图里塞重复内容。
        var still = NormalizedSad(prev.Gray, next.Gray, w, h, 0, 4);
        if (still <= StillThreshold)
        {
            dh = 0;
            score = still;
            return true;
        }

        var limit = h - MinOverlapFor(h) - 1;
        if (limit < 4) return false;

        var (found, best, bestScore) = Search(prev.Gray, next.Gray, w, h, -limit, limit);
        if (!found || bestScore > TrustThreshold) return false;

        dh = best;
        score = bestScore;
        return true;
    }

    /// <summary>
    /// 找最佳位移：降采样遍历**每一个**候选（步长 1，SAD 采样 step 8），
    /// 再对 top-K 用全分辨率确认。不能按固定步长跳着扫候选：
    /// 内容有周期（表格线、代码行、条纹），真值落在扫描网格之间时，
    /// 网格上的周期性伪峰会赢，而精修范围又够不到真值。
    /// </summary>
    private static (bool Found, int Dh, double Score) Search(
        byte[] prev, byte[] next, int w, int h, int min, int max)
    {
        var coarse = new List<(int Dh, double Score)>(max - min + 1);

        for (var candidate = min; candidate <= max; candidate++)
        {
            if (Math.Abs(candidate) < 4) continue;      // 太小的位移不可信，等同静止
            coarse.Add((candidate, NormalizedSad(prev, next, w, h, candidate, 8)));
        }

        if (coarse.Count == 0) return (false, 0, double.MaxValue);

        coarse.Sort((a, b) => a.Score.CompareTo(b.Score));

        var best = 0;
        var bestScore = double.MaxValue;

        foreach (var (seed, _) in coarse.Take(CoarseTopK))
        {
            var lo = Math.Max(min, seed - 2);
            var hi = Math.Min(max, seed + 2);
            for (var candidate = lo; candidate <= hi; candidate++)
            {
                if (Math.Abs(candidate) < 4) continue;
                var s = NormalizedSad(prev, next, w, h, candidate, 1);
                if (s < bestScore)
                {
                    bestScore = s;
                    best = candidate;
                }
            }
        }

        return (true, best, bestScore);
    }

    /// <summary>
    /// 归一化 SAD：不同候选 dh 的重叠区长度不同，不归一化的话「重叠越少分越低」，
    /// 结果会系统性地偏向大偏移。这是这个算法最容易写错的地方。
    /// </summary>
    private static double NormalizedSad(byte[] prev, byte[] next, int w, int h, int dh, int step)
    {
        var shift = Math.Abs(dh);
        var overlap = h - shift;
        if (overlap < MinOverlapFor(h)) return double.MaxValue;

        // dh > 0：next[y] 对应 prev[y + dh]
        // dh < 0：next[y + |dh|] 对应 prev[y]
        var prevShift = dh >= 0 ? dh : 0;
        var nextShift = dh >= 0 ? 0 : shift;

        long sum = 0;
        long count = 0;

        for (var y = 0; y < overlap; y += step)
        {
            var p = (y + prevShift) * w;
            var n = (y + nextShift) * w;
            for (var x = 0; x < w; x += step)
            {
                sum += Math.Abs(prev[p + x] - next[n + x]);
                count++;
            }
        }

        return count == 0 ? double.MaxValue : (double)sum / count;
    }
}

// ---------------------------------------------------------------- 累积

/// <summary>
/// 按「内容坐标」累积长图：向下滚追加、向上滚前插、在已经截过的范围内来回滚不重复。
/// cursor = 当前帧顶部相对首帧顶部的偏移；已截内容是 [MinCursor, MaxCursor + 帧高)。
/// </summary>
public sealed class ScrollAccumulator
{
    private readonly List<int[]> _headBlocks = [];      // 首帧上方，越靠后越靠上
    private readonly List<int[]> _tailBlocks = [];      // 首帧下方，越靠后越靠下
    private int[] _first = [];
    private bool _allowPrepend = true;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Cursor { get; private set; }
    public int MinCursor { get; private set; }
    public int MaxCursor { get; private set; }
    public int Segments { get; private set; }
    public int Rows { get; private set; }
    public bool HasContent => _first.Length > 0;

    /// <summary>长度到顶了：再往下累积内存吃不消，而且 GDI+ 位图也有尺寸上限。</summary>
    public bool LimitReached { get; private set; }

    /// <summary>
    /// 长图总像素上限。累积缓冲和最终生成的那张位图都要占内存，
    /// 3000 万像素约 120 MB。固定行数上限是不对的：1920 宽选区的 3 万行
    /// 是 800 宽选区的 2.4 倍内存，一样会爆。
    /// </summary>
    private const long MaxPixels = 30_000_000;

    /// <summary>GDI+ 位图高度上限 65535，留点余量。</summary>
    private const int MaxRowsHardLimit = 60_000;

    private long RowLimit => Math.Min(MaxRowsHardLimit, Math.Max(1000, MaxPixels / Math.Max(1, Width)));

    public void AddFirstFrame(ScreenFrame frame)
    {
        _first = frame.Pixels;
        Width = frame.Width;
        Height = frame.Height;
        Cursor = MinCursor = MaxCursor = 0;
        Segments = 1;
        Rows = Height;
    }

    /// <summary>推入一帧及其相对上一帧的滚动量；返回本次是否带来了新内容。</summary>
    public bool Push(ScreenFrame frame, int dh)
    {
        if (!HasContent)
        {
            AddFirstFrame(frame);
            return true;
        }

        if (dh == 0) return false;

        var next = Cursor + dh;
        var added = false;
        var w = frame.Width;

        if (next > MaxCursor)
        {
            // 向下滚出新内容：新帧末尾那几行
            var count = Math.Min(next - MaxCursor, frame.Height);

            if (Rows + count > RowLimit)
            {
                LimitReached = true;
                return false;
            }

            _tailBlocks.Add(frame.Pixels.AsSpan((frame.Height - count) * w, count * w).ToArray());
            MaxCursor += count;     // 记实际取到的行数，别假设它必然等于 next
            Rows += count;
            Segments++;
            added = true;
        }
        else if (_allowPrepend && next < MinCursor)
        {
            // 向上滚出新内容：新帧开头那几行
            var count = Math.Min(MinCursor - next, frame.Height);

            if (Rows + count > RowLimit)
            {
                LimitReached = true;
                return false;
            }

            _headBlocks.Add(frame.Pixels.AsSpan(0, count * w).ToArray());
            MinCursor -= count;
            Rows += count;
            Segments++;
            added = true;
        }
        // 落在已截范围内 = 用户往回滚，内容已经在图里，不要重复追加

        Cursor = next;
        return added;
    }

    /// <summary>
    /// 基准失效（连续多帧都配不上）时重设：把当前位置当成新的底部，
    /// 之后的内容一律接在尾部，不再接受向上前插——允许结果里出现一次可见的跳跃，
    /// 总好过因为永远配不上而彻底卡死。
    /// </summary>
    public void ResetBaseline()
    {
        Cursor = MaxCursor;
        _allowPrepend = false;
    }

    /// <summary>
    /// 直接拼成位图。刻意不经过 List&lt;int&gt; 中转：
    /// 那样会在生成长图的瞬间把内存占用翻倍，长网页正是最容易踩到的时候。
    /// </summary>
    public Bitmap BuildBitmap()
    {
        if (!HasContent || Rows <= 0) throw new InvalidOperationException("还没有任何内容");

        var bmp = new Bitmap(Width, Rows, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new Rectangle(0, 0, Width, Rows),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var y = 0;

            for (var i = _headBlocks.Count - 1; i >= 0; i--) WriteBlock(data, _headBlocks[i], ref y);
            WriteBlock(data, _first, ref y);
            foreach (var block in _tailBlocks) WriteBlock(data, block, ref y);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    private void WriteBlock(BitmapData data, int[] block, ref int y)
    {
        var line = new int[Width];
        var rows = block.Length / Width;

        for (var r = 0; r < rows && y < Rows; r++, y++)
        {
            Array.Copy(block, r * Width, line, 0, Width);
            Marshal.Copy(line, 0, data.Scan0 + y * data.Stride, Width);
        }
    }
}

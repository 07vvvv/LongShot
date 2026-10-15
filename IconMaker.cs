using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LongShot;

/// <summary>
/// 生成应用图标 app.ico（多尺寸 PNG-in-ICO）。
/// 图形语义：深色圆角底 + 一张「长图」+ 向下箭头 = 向下滚动截长图。
/// 需要重做图标时跑一次 --makeicon，产物由 csproj 的 ApplicationIcon 引用。
/// </summary>
internal static class IconMaker
{
    private static readonly int[] Sizes = [16, 24, 32, 48, 64, 128, 256];

    public static void Run()
    {
        ProbeRunner.UseUtf8Console();

        var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
        var images = new List<byte[]>(Sizes.Length);

        foreach (var size in Sizes)
        {
            using var bmp = Draw(size);
            images.Add(ToPng(bmp));
        }

        WriteIco(images, Sizes, path);
        Console.WriteLine($"图标已生成：{path}（{Sizes.Length} 个尺寸，{new FileInfo(path).Length / 1024.0:F1} KB）");

        // 顺便存一份最大尺寸的 PNG，方便肉眼核对（.NET 的 Icon 类不认 PNG 压缩的 ICO 条目）
        using var preview = Draw(Sizes[^1]);
        var previewPath = Path.Combine(AppContext.BaseDirectory, "app-icon-preview.png");
        preview.Save(previewPath, ImageFormat.Png);
        Console.WriteLine($"预览图：{previewPath}");
    }

    private static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        var inset = size * 0.02f;
        var face = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);

        using (var background = RoundedRect(face, size * 0.22f))
        using (var brush = new SolidBrush(Color.FromArgb(0x11, 0x11, 0x11)))
        {
            g.FillPath(brush, background);
        }

        using var white = new SolidBrush(Color.White);

        // 「长图」：竖直的圆角长条
        var sheetWidth = size * 0.34f;
        var sheetHeight = size * 0.42f;
        var sheet = new RectangleF(
            (size - sheetWidth) / 2f,
            size * 0.17f,
            sheetWidth,
            sheetHeight);

        using (var sheetPath = RoundedRect(sheet, size * 0.05f))
        {
            g.FillPath(white, sheetPath);
        }

        // 「向下滚」：长条下方的三角
        var arrow = new[]
        {
            new PointF(size * 0.50f, size * 0.83f),
            new PointF(size * 0.29f, size * 0.65f),
            new PointF(size * 0.71f, size * 0.65f),
        };

        g.FillPolygon(white, arrow);

        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>写 ICO 容器：6 字节目录头 + 每图 16 字节目录项 + 各图 PNG 数据。</summary>
    private static void WriteIco(IReadOnlyList<byte[]> images, IReadOnlyList<int> sizes, string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);                // 保留
        writer.Write((ushort)1);                // 类型：图标
        writer.Write((ushort)images.Count);

        var offset = 6 + 16 * images.Count;

        for (var i = 0; i < images.Count; i++)
        {
            var side = sizes[i];
            writer.Write((byte)(side >= 256 ? 0 : side));   // 0 表示 256
            writer.Write((byte)(side >= 256 ? 0 : side));
            writer.Write((byte)0);                          // 调色板数
            writer.Write((byte)0);                          // 保留
            writer.Write((ushort)1);                        // 色彩平面
            writer.Write((ushort)32);                       // 位深
            writer.Write(images[i].Length);
            writer.Write(offset);

            offset += images[i].Length;
        }

        foreach (var image in images) writer.Write(image);
    }
}

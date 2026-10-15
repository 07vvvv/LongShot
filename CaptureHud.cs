using System.Drawing;
using System.Windows.Forms;

namespace LongShot;

/// <summary>
/// 抓取阶段的浮层：一个不透明的小卡片，显示进度并提供可点击的「完成」。
/// 抓取时主遮罩窗口已经彻底透明（拍不到自己、也不拦鼠标），
/// 用户的所有视觉反馈都落在这里。卡片必须放在选区之外。
/// </summary>
public sealed class CaptureHud : Form
{
    private static readonly Color Canvas = Color.FromArgb(0xFB, 0xFB, 0xFA);
    private static readonly Color Ink = Color.FromArgb(0x11, 0x11, 0x11);
    private static readonly Color Muted = Color.FromArgb(0x78, 0x77, 0x74);
    private static readonly Color Line = Color.FromArgb(0xEA, 0xEA, 0xEA);

    private readonly Label _title = new();
    private readonly Label _status = new();

    protected override bool ShowWithoutActivation => true;

    public CaptureHud(Action onDone)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Canvas;
        KeyPreview = true;

        _title.Text = "滚轮滚动截取　·　单击左键结束";
        _title.Font = new Font("Microsoft YaHei UI", 9F);
        _title.ForeColor = Ink;
        _title.AutoSize = true;
        _title.Location = new Point(16, 14);

        _status.Text = "已拼接 1 段";
        _status.Font = new Font("Microsoft YaHei UI", 8.25F);
        _status.ForeColor = Muted;
        _status.AutoSize = true;
        _status.Location = new Point(16, 38);

        var done = new Button
        {
            Text = "完成",
            Location = new Point(248, 17),
            Size = new Size(88, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Ink,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        done.FlatAppearance.BorderSize = 0;
        done.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x33, 0x33, 0x33);
        done.Click += (_, _) => onDone();

        Controls.Add(_title);
        Controls.Add(_status);
        Controls.Add(done);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Escape) onDone();
        };
    }

    public void SetTitle(string text) => _title.Text = text;

    public void UpdateStatus(int segments, int rows, int skipped, bool limitReached)
    {
        if (limitReached)
        {
            _status.Text = $"已达长度上限 {rows} 行　·　左键结束后再截下一段";
            return;
        }

        var skip = skipped > 0 ? $"　跳过 {skipped} 帧" : string.Empty;
        var hint = skipped >= 5 ? "　（滚慢一点更准）" : string.Empty;
        _status.Text = $"已拼接 {segments} 段　{rows} 行{skip}{hint}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Line, 1);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

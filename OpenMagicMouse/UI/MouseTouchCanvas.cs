using System.Drawing.Drawing2D;
using System.Drawing.Text;
using OpenMagicMouse.Models;

namespace OpenMagicMouse.UI;

/// <summary>
/// Live double-buffered capacitive touch surface visualizer for Apple Magic Mouse 2.
/// </summary>
public sealed class MouseTouchCanvas : Control
{
    private TouchFinger[] _fingers = [];
    private byte _clicks;
    private string _gestureBanner = "";
    private long _gestureBannerUntilMs;
    private bool _coastingActive;

    private static readonly Color SurfaceTop = Color.FromArgb(38, 43, 56);
    private static readonly Color SurfaceBottom = Color.FromArgb(24, 28, 37);
    private static readonly Color RimColor = Color.FromArgb(72, 82, 104);
    private static readonly Color TouchZoneDivider = Color.FromArgb(52, 60, 76);
    private static readonly Color AppleBlue = Color.FromArgb(10, 132, 255);
    private static readonly Color AppleGreen = Color.FromArgb(48, 209, 88);
    private static readonly Color ApplePurple = Color.FromArgb(191, 90, 242);
    private static readonly Color AppleOrange = Color.FromArgb(255, 159, 10);

    public MouseTouchCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(28, 32, 41);
    }

    public void UpdateFrame(TouchFrame frame)
    {
        _fingers = frame.Fingers;
        _clicks = frame.Clicks;
        Invalidate();
    }

    public void SetCoastingState(bool coasting)
    {
        if (_coastingActive != coasting)
        {
            _coastingActive = coasting;
            Invalidate();
        }
    }

    public void ShowGestureBanner(string text)
    {
        _gestureBanner = text;
        _gestureBannerUntilMs = Environment.TickCount64 + 1800;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int padX = 34;
        int padTop = 18;
        int padBottom = 56;
        int bodyW = Math.Max(140, Width - padX * 2);
        int bodyH = Math.Max(220, Height - padTop - padBottom);
        var bodyRect = new RectangleF(padX, padTop, bodyW, bodyH);
        float radius = bodyW * 0.44f;

        using var mousePath = CreateRoundedRect(bodyRect, radius);

        // Outer subtle shadow
        using (var shadowPath = CreateRoundedRect(new RectangleF(padX + 2, padTop + 5, bodyW, bodyH), radius))
        using (var shadowBrush = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
        {
            g.FillPath(shadowBrush, shadowPath);
        }

        // Mouse shell gradient
        using (var shellBrush = new LinearGradientBrush(bodyRect, SurfaceTop, SurfaceBottom, LinearGradientMode.Vertical))
        {
            g.FillPath(shellBrush, mousePath);
        }

        // Subtle click highlight when physical switch is pressed
        if (_clicks != 0)
        {
            using var clickBrush = new SolidBrush(Color.FromArgb(26, 10, 132, 255));
            g.FillPath(clickBrush, mousePath);
        }

        // Outer metallic rim
        using (var rimPen = new Pen(_clicks != 0 ? AppleBlue : RimColor, _clicks != 0 ? 2.2f : 1.6f))
        {
            g.DrawPath(rimPen, mousePath);
        }

        // Subtle seam dividing upper touch deck & rear palm area
        float seamY = bodyRect.Top + bodyRect.Height * 0.52f;
        using (var seamPen = new Pen(Color.FromArgb(70, TouchZoneDivider), 1f) { DashStyle = DashStyle.Dot })
        {
            g.DrawLine(seamPen, bodyRect.Left + 14, seamY, bodyRect.Right - 14, seamY);
        }

        // Minimal centered indicator near palm
        using (var labelFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
        using (var mutedBrush = new SolidBrush(Color.FromArgb(115, 130, 152)))
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString("CẢM ỨNG ĐA ĐIỂM", labelFont, mutedBrush, bodyRect.Left + bodyRect.Width / 2f, bodyRect.Top + 16f, sf);
            g.DrawString("MAGIC MOUSE 2", labelFont, mutedBrush, bodyRect.Left + bodyRect.Width / 2f, bodyRect.Bottom - 28f, sf);
        }

        // Clip finger rendering smoothly inside the mouse deck
        var prevClip = g.Clip;
        g.SetClip(mousePath);

        int downCount = 0;
        string coordText = "Chạm ngón tay lên mặt chuột để xem trực tiếp";
        Color[] palette = [AppleBlue, AppleGreen, ApplePurple, AppleOrange];

        for (int i = 0; i < _fingers.Length; i++)
        {
            var f = _fingers[i];
            if (!f.IsDown) continue;
            downCount++;

            // X in [-1100..+1258], Y in [-2047..+1589] (+Y = TOP/front, -Y = BOTTOM/rear)
            float nx = Math.Clamp((f.X + 1100f) / 2358f, 0.06f, 0.94f);
            float ny = Math.Clamp((1589f - f.Y) / 3636f, 0.06f, 0.94f);

            float px = bodyRect.Left + nx * bodyRect.Width;
            float py = bodyRect.Top + ny * bodyRect.Height;
            float baseR = Math.Clamp(15f + f.Size * 0.25f, 16f, 28f);
            Color c = palette[(f.Id >= 0 ? f.Id : i) % palette.Length];

            using (var glowBrush = new SolidBrush(Color.FromArgb(55, c)))
            {
                g.FillEllipse(glowBrush, px - baseR * 1.45f, py - baseR * 1.45f, baseR * 2.9f, baseR * 2.9f);
            }
            using (var dotBrush = new SolidBrush(Color.FromArgb(220, c)))
            {
                g.FillEllipse(dotBrush, px - baseR, py - baseR, baseR * 2, baseR * 2);
            }
            using (var ringPen = new Pen(Color.White, 1.5f))
            {
                g.DrawEllipse(ringPen, px - baseR, py - baseR, baseR * 2, baseR * 2);
            }

            using var idFont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            using var whiteBrush = new SolidBrush(Color.White);
            var sfCenter = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(downCount.ToString(), idFont, whiteBrush, new RectangleF(px - baseR, py - baseR, baseR * 2, baseR * 2), sfCenter);

            if (downCount == 1)
            {
                coordText = $"Ngón: {downCount} | Tọa độ: ({f.X}, {f.Y})";
            }
        }

        if (downCount > 1)
        {
            coordText = $"Đang chạm {downCount} ngón đồng thời";
        }

        g.Clip = prevClip;

        // Bottom live coordinate / gesture banner pill
        var bannerRect = new RectangleF(12, Height - 44, Width - 24, 34);
        bool bannerActive = !string.IsNullOrEmpty(_gestureBanner) && Environment.TickCount64 <= _gestureBannerUntilMs;
        string footerText = bannerActive
            ? $"⚡ {_gestureBanner}"
            : (_coastingActive && downCount == 0 ? "🌊 Đang trôi quán tính Mac (chạm ngón để phanh)" : coordText);

        using var pillPath = CreateRoundedRect(bannerRect, 10f);
        using (var pillBrush = new SolidBrush(bannerActive ? Color.FromArgb(40, 10, 132, 255) : Color.FromArgb(22, 25, 33)))
        {
            g.FillPath(pillBrush, pillPath);
        }
        using (var pillPen = new Pen(bannerActive ? AppleBlue : Color.FromArgb(52, 60, 76), 1f))
        {
            g.DrawPath(pillPen, pillPath);
        }
        using (var bannerFont = new Font("Segoe UI", 9f, bannerActive ? FontStyle.Bold : FontStyle.Regular))
        using (var textBrush = new SolidBrush(bannerActive ? Color.White : Color.FromArgb(182, 192, 210)))
        {
            var sfText = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(footerText, bannerFont, textBrush, bannerRect, sfText);
        }
    }

    private static GraphicsPath CreateRoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

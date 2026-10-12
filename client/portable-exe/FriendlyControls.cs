using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class AppIdentity
{
    // Each window/image list owns a copy; disposing one must not invalidate another.
    internal static Icon CreateIcon()
    {
        using (var stream = typeof(AppIdentity).Assembly.GetManifestResourceStream("Remsup.App.ico"))
        {
            if (stream == null) throw new InvalidOperationException("Remsup app icon is missing from this build.");
            using (var icon = new Icon(stream, 32, 32)) return (Icon)icon.Clone();
        }
    }
    internal static Bitmap CreateBitmap()
    {
        using (var icon = CreateIcon()) return icon.ToBitmap();
    }
}

// Painting the card edge keeps normal native child controls, focus and scrolling.
internal sealed class CardPanel : Panel
{
    internal CardPanel()
    {
        DoubleBuffered = true;
        Padding = new Padding(18);
        Margin = new Padding(8);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = SystemInformation.HighContrast ? 0 : 12;
        var bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        using (var path = new GraphicsPath())
        {
            if (radius == 0) path.AddRectangle(bounds);
            else
            {
                float d = radius * 2;
                path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
                path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
                path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
                path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
            }
            using (var fill = new SolidBrush(BackColor)) e.Graphics.FillPath(fill, path);
            using (var border = new Pen(Appearance.Border)) e.Graphics.DrawPath(border, path);
        }
    }
}

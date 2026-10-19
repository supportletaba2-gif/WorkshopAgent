using System.Drawing.Drawing2D;

namespace WorkshopAgent;

internal static class WorkshopIcon
{
    public static Icon Create()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var associated = Icon.ExtractAssociatedIcon(path);
                if (associated is not null)
                    return new Icon(associated, 32, 32);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.ComponentModel.Win32Exception)
        {
        }

        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(21, 122, 69));
            graphics.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("W", font, Brushes.White, 7, 6);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}

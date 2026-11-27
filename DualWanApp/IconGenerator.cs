using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DualWanBalancer
{
    public static class IconGenerator
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon CreateTrayIcon(WanMode mode, bool hasWarning, bool gameModeActive)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                Color accent;
                if (gameModeActive || mode == WanMode.GameSplit)
                {
                    accent = Color.FromArgb(244, 63, 94); // Rose/Coral Game & Split Mode
                }
                else if (hasWarning)
                {
                    accent = Color.FromArgb(245, 158, 11); // Amber warning
                }
                else if (mode == WanMode.Combined)
                {
                    accent = Color.FromArgb(16, 185, 129); // Emerald
                }
                else if (mode == WanMode.OnlyEthernet)
                {
                    accent = Color.FromArgb(59, 130, 246); // Blue
                }
                else if (mode == WanMode.OnlyWifi)
                {
                    accent = Color.FromArgb(168, 85, 247); // Purple
                }
                else
                {
                    accent = Color.FromArgb(100, 116, 139); // Slate
                }

                using (SolidBrush bg = new SolidBrush(Color.FromArgb(15, 23, 42)))
                using (Pen border = new Pen(accent, 2.5f))
                {
                    g.FillEllipse(bg, 2, 2, 28, 28);
                    g.DrawEllipse(border, 2, 2, 28, 28);
                }

                using (Pen downPen = new Pen(Color.FromArgb(52, 211, 153), 2.5f))
                using (Pen upPen = new Pen(Color.FromArgb(56, 189, 248), 2.5f))
                {
                    downPen.StartCap = LineCap.Round;
                    downPen.EndCap = LineCap.Round;
                    upPen.StartCap = LineCap.Round;
                    upPen.EndCap = LineCap.Round;

                    g.DrawLine(downPen, 11, 8, 11, 22);
                    g.DrawLine(downPen, 7, 18, 11, 22);
                    g.DrawLine(downPen, 15, 18, 11, 22);

                    g.DrawLine(upPen, 21, 23, 21, 9);
                    g.DrawLine(upPen, 17, 13, 21, 9);
                    g.DrawLine(upPen, 25, 13, 21, 9);
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }
    }
}

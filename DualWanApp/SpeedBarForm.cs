using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DualWanBalancer
{
    public class SpeedBarForm : Form
    {
        private bool _dragging;
        private bool _movedDuringDrag;
        private Point _dragStartCursor;
        private Point _dragStartForm;

        private string _modeBadge = "BOTH";
        private Color _modeColor = Color.FromArgb(16, 185, 129);
        private string _totalDown = "0.0 Mbps";
        private string _totalUp = "0.0 Mbps";
        private string _pingBadge = "-- ms";
        private Color _pingColor = Color.FromArgb(148, 163, 184);
        private string _ethText = "ETH: --";
        private string _wifiText = "WIFI: --";
        private string _gameBanner = "";
        private bool _ethUp;
        private bool _wifiUp;

        public event EventHandler OpenPanelRequested;

        public SpeedBarForm(ContextMenuStrip contextMenu, AppConfig cfg)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(15, 23, 42);
            Size = new Size(364, 56);
            ContextMenuStrip = contextMenu;

            if (cfg != null && cfg.SpeedBarX >= 0 && cfg.SpeedBarY >= 0 && IsVisibleOnScreen(cfg.SpeedBarX, cfg.SpeedBarY))
            {
                Location = new Point(cfg.SpeedBarX, cfg.SpeedBarY);
            }
            else
            {
                DockBottomRight();
            }

            MouseDown += OnBarMouseDown;
            MouseMove += OnBarMouseMove;
            MouseUp += OnBarMouseUp;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW (hide from Alt+Tab)
                return cp;
            }
        }

        private static bool IsVisibleOnScreen(int x, int y)
        {
            Rectangle test = new Rectangle(x, y, 80, 30);
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                if (Screen.AllScreens[i].WorkingArea.IntersectsWith(test)) return true;
            }
            return false;
        }

        public void DockBottomRight()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 10, wa.Bottom - Height - 8);
        }

        public void UpdateMetrics(
            DualWanSnapshot snap,
            WanMode effectiveMode,
            bool useMbps,
            LinkLatencyStats ethLatency,
            LinkLatencyStats wifiLatency,
            DetectedGameInfo activeGame)
        {
            if (activeGame != null)
            {
                _modeBadge = "GAME";
                _modeColor = Color.FromArgb(244, 63, 94);
                _gameBanner = activeGame.DisplayName;
            }
            else if (effectiveMode == WanMode.GameSplit)
            {
                _modeBadge = "SPLIT";
                _modeColor = Color.FromArgb(244, 63, 94);
                _gameBanner = "Game->ETH | DL->WIFI";
            }
            else if (effectiveMode == WanMode.Combined)
            {
                _modeBadge = "BOTH";
                _modeColor = Color.FromArgb(16, 185, 129);
                _gameBanner = "";
            }
            else if (effectiveMode == WanMode.OnlyEthernet)
            {
                _modeBadge = "ETH";
                _modeColor = Color.FromArgb(59, 130, 246);
                _gameBanner = "";
            }
            else if (effectiveMode == WanMode.OnlyWifi)
            {
                _modeBadge = "WIFI";
                _modeColor = Color.FromArgb(168, 85, 247);
                _gameBanner = "";
            }
            else
            {
                _modeBadge = "DIRECT";
                _modeColor = Color.FromArgb(100, 116, 139);
                _gameBanner = "";
            }

            double ethDown = (snap != null && snap.Ethernet != null) ? snap.Ethernet.DownloadBytesPerSec : 0;
            double ethUp = (snap != null && snap.Ethernet != null) ? snap.Ethernet.UploadBytesPerSec : 0;
            double wifiDown = (snap != null && snap.Wifi != null) ? snap.Wifi.DownloadBytesPerSec : 0;
            double wifiUp = (snap != null && snap.Wifi != null) ? snap.Wifi.UploadBytesPerSec : 0;

            _ethUp = snap != null && snap.Ethernet != null && snap.Ethernet.IsUp;
            _wifiUp = snap != null && snap.Wifi != null && snap.Wifi.IsUp;

            _totalDown = NetworkDiscovery.FormatRate(ethDown + wifiDown, useMbps);
            _totalUp = NetworkDiscovery.FormatRate(ethUp + wifiUp, useMbps);

            // Pick best visible ping for top badge
            double displayPing = -1;
            if (_ethUp && ethLatency != null && ethLatency.HasMeasurement && ethLatency.CurrentPingMs < 999)
            {
                displayPing = ethLatency.CurrentPingMs;
            }
            if (_wifiUp && wifiLatency != null && wifiLatency.HasMeasurement && wifiLatency.CurrentPingMs < 999)
            {
                if (displayPing < 0 || (effectiveMode == WanMode.OnlyWifi) || (wifiLatency.CurrentPingMs < displayPing && effectiveMode == WanMode.Combined))
                {
                    displayPing = wifiLatency.CurrentPingMs;
                }
            }

            if (displayPing >= 0)
            {
                _pingBadge = string.Format("{0:0} ms", displayPing);
                if (displayPing <= 45) _pingColor = Color.FromArgb(52, 211, 153);
                else if (displayPing <= 95) _pingColor = Color.FromArgb(251, 191, 36);
                else _pingColor = Color.FromArgb(248, 113, 113);
            }
            else
            {
                _pingBadge = "-- ms";
                _pingColor = Color.FromArgb(148, 163, 184);
            }

            string ethPing = (_ethUp && ethLatency != null && ethLatency.HasMeasurement) ? string.Format("{0:0}ms ", ethLatency.CurrentPingMs) : "";
            string wifiPing = (_wifiUp && wifiLatency != null && wifiLatency.HasMeasurement) ? string.Format("{0:0}ms ", wifiLatency.CurrentPingMs) : "";

            _ethText = _ethUp
                ? string.Format("ETH {0}{1}/{2}", ethPing, NetworkDiscovery.FormatRate(ethDown, useMbps), NetworkDiscovery.FormatRate(ethUp, useMbps))
                : "ETH Off";

            _wifiText = _wifiUp
                ? string.Format("WIFI {0}{1}/{2}", wifiPing, NetworkDiscovery.FormatRate(wifiDown, useMbps), NetworkDiscovery.FormatRate(wifiUp, useMbps))
                : "WIFI Off";

            Invalidate();
        }

        private void OnBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _movedDuringDrag = false;
                _dragStartCursor = Cursor.Position;
                _dragStartForm = Location;
            }
        }

        private void OnBarMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point cur = Cursor.Position;
            int dx = cur.X - _dragStartCursor.X;
            int dy = cur.Y - _dragStartCursor.Y;
            if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
            {
                _movedDuringDrag = true;
            }
            if (_movedDuringDrag)
            {
                Location = new Point(_dragStartForm.X + dx, _dragStartForm.Y + dy);
            }
        }

        private void OnBarMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                bool wasClick = !_movedDuringDrag;
                _dragging = false;
                if (wasClick && OpenPanelRequested != null)
                {
                    OpenPanelRequested(this, EventArgs.Empty);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(15, 23, 42)))
            using (Pen border = new Pen(Color.FromArgb(51, 65, 85), 1.2f))
            {
                g.FillRectangle(bg, bounds);
                g.DrawRectangle(border, bounds);
            }

            Rectangle badgeRect = new Rectangle(8, 8, 48, 20);
            using (SolidBrush pillBrush = new SolidBrush(_modeColor))
            using (Font badgeFont = new Font("Segoe UI", 7.2f, FontStyle.Bold))
            using (SolidBrush whiteBrush = new SolidBrush(Color.White))
            {
                g.FillRectangle(pillBrush, badgeRect);
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(_modeBadge, badgeFont, whiteBrush, badgeRect, sf);
            }

            using (Font mainFont = new Font("Segoe UI", 8.8f, FontStyle.Bold))
            using (SolidBrush dlBrush = new SolidBrush(Color.FromArgb(52, 211, 153)))
            using (SolidBrush ulBrush = new SolidBrush(Color.FromArgb(56, 189, 248)))
            using (SolidBrush pingBrush = new SolidBrush(_pingColor))
            {
                g.DrawString("↓ " + _totalDown, mainFont, dlBrush, 62, 9);
                g.DrawString("↑ " + _totalUp, mainFont, ulBrush, 166, 9);
                g.DrawString(_pingBadge, mainFont, pingBrush, 276, 9);
            }

            using (SolidBrush ethDot = new SolidBrush(_ethUp ? Color.FromArgb(34, 197, 94) : Color.FromArgb(239, 68, 68)))
            using (SolidBrush wifiDot = new SolidBrush(_wifiUp ? Color.FromArgb(34, 197, 94) : Color.FromArgb(239, 68, 68)))
            using (Font subFont = new Font("Segoe UI", 6.8f, FontStyle.Regular))
            using (SolidBrush muted = new SolidBrush(Color.FromArgb(148, 163, 184)))
            using (SolidBrush gameBrush = new SolidBrush(Color.FromArgb(251, 113, 133)))
            {
                g.FillEllipse(ethDot, 10, 38, 6, 6);
                g.DrawString(_ethText, subFont, muted, 19, 34);

                if (!string.IsNullOrEmpty(_gameBanner))
                {
                    g.DrawString("Active: " + _gameBanner, subFont, gameBrush, 182, 34);
                }
                else
                {
                    g.FillEllipse(wifiDot, 174, 38, 6, 6);
                    g.DrawString(_wifiText, subFont, muted, 183, 34);
                }
            }
        }
    }
}

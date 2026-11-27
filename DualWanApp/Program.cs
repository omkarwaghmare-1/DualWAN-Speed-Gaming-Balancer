using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DualWanBalancer
{
    internal static class Program
    {
        private const string MutexName = @"Local\DualWanBalancer_SingleInstance_Mutex";
        private const string ShowEventName = @"Local\DualWanBalancer_ShowUI_Event";

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            bool startInBackground = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--background", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "--minimized", StringComparison.OrdinalIgnoreCase))
                {
                    startInBackground = true;
                }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (EventWaitHandle showEvt = EventWaitHandle.OpenExisting(ShowEventName))
                        {
                            showEvt.Set();
                        }
                    }
                    catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (DualWanAppContext ctx = new DualWanAppContext(startInBackground))
                {
                    Application.Run(ctx);
                }
            }
        }
    }

    internal class DualWanAppContext : ApplicationContext
    {
        private readonly AppConfig _config;
        private readonly DualWanProxyEngine _engine;
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _menu;
        private readonly SpeedBarForm _speedBar;
        private readonly MainForm _mainForm;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly EventWaitHandle _showEvent;
        private readonly RegisteredWaitHandle _registeredWait;

        private ToolStripMenuItem _miCombined;
        private ToolStripMenuItem _miGameSplit;
        private ToolStripMenuItem _miOnlyEth;
        private ToolStripMenuItem _miOnlyWifi;
        private ToolStripMenuItem _miDirect;
        private ToolStripMenuItem _miAutoGame;
        private ToolStripMenuItem _miShowBar;
        private ToolStripMenuItem _miStartup;

        private DualWanSnapshot _lastSnapshot;
        private LinkLatencyStats _ethLatency;
        private LinkLatencyStats _wifiLatency;
        private DetectedGameInfo _activeGame;
        private int _boostedGamePid = -1;
        private string _lastUplinkSignature = "";
        private string _lastEngineError = "";
        private Icon _currentTrayIcon;

        private int _tickCount;
        private bool _probeInFlight;

        public DualWanAppContext(bool startInBackground)
        {
            _config = AppConfig.Load();
            _engine = new DualWanProxyEngine();

            if (_config.ProxyDirty)
            {
                NetworkDiscovery.RestoreSystemProxy(_config);
            }

            _menu = BuildContextMenu();

            _trayIcon = new NotifyIcon();
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.Text = "DualWAN Speed & Gaming Balancer";
            _trayIcon.Visible = true;
            _trayIcon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ShowMainPanel();
                }
            };

            _speedBar = new SpeedBarForm(_menu, _config);
            _speedBar.OpenPanelRequested += delegate { ShowMainPanel(); };
            _speedBar.LocationChanged += delegate
            {
                if (_speedBar.Visible)
                {
                    _config.SpeedBarX = _speedBar.Left;
                    _config.SpeedBarY = _speedBar.Top;
                }
            };

            _mainForm = new MainForm(_config);
            _mainForm.ModeSelected += ApplyMode;
            _mainForm.SettingsChanged += OnSettingsChanged;
            _mainForm.ApplyOsMetricsRequested += OnApplyOsMetrics;
            _mainForm.ApplyTcpTweaksRequested += OnApplyTcpTweaks;
            _mainForm.RevertTcpTweaksRequested += OnRevertTcpTweaks;
            _mainForm.ExitAppRequested += ExitGracefully;

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DualWanBalancer_ShowUI_Event");
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                OnExternalShowSignal,
                null,
                -1,
                false);

            SystemEvents.SessionEnding += OnSessionEnding;

            RefreshAll(true);
            TriggerLatencyProbeAsync();

            if (_config.ShowSpeedBar)
            {
                _speedBar.Show();
            }

            if (!startInBackground)
            {
                _mainForm.ShowAndFocus();
                PromptStartupIfFirstRun();
            }

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 1000;
            _pollTimer.Tick += OnPollTimerTick;
            _pollTimer.Start();
        }

        private ContextMenuStrip BuildContextMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            _miCombined = new ToolStripMenuItem("Combined (Ethernet + Wi-Fi)", null, delegate { ApplyMode(WanMode.Combined); });
            _miGameSplit = new ToolStripMenuItem("Game Split (Games->ETH | DL->WIFI)", null, delegate { ApplyMode(WanMode.GameSplit); });
            _miOnlyEth = new ToolStripMenuItem("Only Ethernet", null, delegate { ApplyMode(WanMode.OnlyEthernet); });
            _miOnlyWifi = new ToolStripMenuItem("Only Wi-Fi", null, delegate { ApplyMode(WanMode.OnlyWifi); });
            _miDirect = new ToolStripMenuItem("Direct (Pause / No Proxy)", null, delegate { ApplyMode(WanMode.Direct); });

            _miAutoGame = new ToolStripMenuItem("Auto Game Boost Mode", null, delegate
            {
                _config.AutoGameMode = !_config.AutoGameMode;
                _config.Save();
                RefreshAll(true);
            });

            _miShowBar = new ToolStripMenuItem("Show Bottom Speed & Ping Bar", null, delegate
            {
                _config.ShowSpeedBar = !_config.ShowSpeedBar;
                _config.Save();
                OnSettingsChanged();
            });

            ToolStripMenuItem miDockBar = new ToolStripMenuItem("Reset Speed Bar to Bottom-Right", null, delegate
            {
                _speedBar.DockBottomRight();
                _config.SpeedBarX = _speedBar.Left;
                _config.SpeedBarY = _speedBar.Top;
                _config.Save();
            });

            _miStartup = new ToolStripMenuItem("Start at PC Startup", null, delegate
            {
                bool target = !NetworkDiscovery.IsStartupEnabled();
                NetworkDiscovery.SetStartupEnabled(target, Application.ExecutablePath);
                _mainForm.SyncStartupCheckbox();
                SyncMenuChecks();
            });

            ToolStripMenuItem miOpen = new ToolStripMenuItem("Open Control Panel", null, delegate { ShowMainPanel(); });
            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit (Restore Proxy)", null, delegate { ExitGracefully(); });

            menu.Items.Add(_miCombined);
            menu.Items.Add(_miGameSplit);
            menu.Items.Add(_miOnlyEth);
            menu.Items.Add(_miOnlyWifi);
            menu.Items.Add(_miDirect);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_miAutoGame);
            menu.Items.Add(_miShowBar);
            menu.Items.Add(miDockBar);
            menu.Items.Add(_miStartup);
            menu.Items.Add(miOpen);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);

            return menu;
        }

        private void PromptStartupIfFirstRun()
        {
            if (_config.StartupPrompted) return;
            _config.StartupPrompted = true;

            if (!NetworkDiscovery.IsStartupEnabled())
            {
                DialogResult res = MessageBox.Show(
                    _mainForm,
                    "Do you want DualWAN Speed & Gaming Balancer to run in the background (with the live bottom speed & ping bar) and start automatically when your PC starts?",
                    "Run in Background & PC Startup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (res == DialogResult.Yes)
                {
                    NetworkDiscovery.SetStartupEnabled(true, Application.ExecutablePath);
                    _mainForm.SyncStartupCheckbox();
                    SyncMenuChecks();
                }
            }
            _config.Save();
        }

        private void OnExternalShowSignal(object state, bool timedOut)
        {
            if (_mainForm.IsHandleCreated)
            {
                _mainForm.BeginInvoke((MethodInvoker)delegate { ShowMainPanel(); });
            }
            else
            {
                _speedBar.BeginInvoke((MethodInvoker)delegate { ShowMainPanel(); });
            }
        }

        private void ShowMainPanel()
        {
            _mainForm.ShowAndFocus();
        }

        private void ApplyMode(WanMode mode)
        {
            _config.SelectedMode = mode;
            _config.Save();
            RefreshAll(true);
        }

        private void OnSettingsChanged()
        {
            if (_config.ShowSpeedBar)
            {
                if (!_speedBar.Visible) _speedBar.Show();
            }
            else
            {
                if (_speedBar.Visible) _speedBar.Hide();
            }
            RefreshAll(true);
        }

        private void OnApplyOsMetrics()
        {
            string msg;
            WanMode effMode = ResolveEffectiveMode();
            bool ok = NetworkDiscovery.ApplyOsInterfaceMetrics(_lastSnapshot, effMode, out msg);
            MessageBox.Show(
                _mainForm,
                msg,
                "Windows Interface Metrics",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void OnApplyTcpTweaks()
        {
            string msg;
            bool ok = NetworkOptimizer.ApplyLowLatencyTweaks(_lastSnapshot, out msg);
            MessageBox.Show(
                _mainForm,
                msg,
                "Windows Low-Latency Tweaks",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void OnRevertTcpTweaks()
        {
            string msg;
            bool ok = NetworkOptimizer.RevertLowLatencyTweaks(_lastSnapshot, out msg);
            MessageBox.Show(
                _mainForm,
                msg,
                "Revert Windows Tweaks",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void OnPollTimerTick(object sender, EventArgs e)
        {
            _tickCount++;
            RefreshAll(false);

            if (_tickCount % 3 == 0)
            {
                TriggerLatencyProbeAsync();
            }
        }

        private void TriggerLatencyProbeAsync()
        {
            if (_probeInFlight || _lastSnapshot == null) return;
            _probeInFlight = true;

            string ethIp = (_lastSnapshot.Ethernet != null && _lastSnapshot.Ethernet.IsUp) ? _lastSnapshot.Ethernet.IPv4 : null;
            string wifiIp = (_lastSnapshot.Wifi != null && _lastSnapshot.Wifi.IsUp) ? _lastSnapshot.Wifi.IPv4 : null;
            string target = _config.PingProbeTarget;
            int maxPing = _config.MaxHealthyPingMs;

            Task.Run(async delegate
            {
                try
                {
                    Task<LinkLatencyStats> tEth = LatencyMonitor.ProbeLinkAsync("Ethernet", ethIp, target, maxPing);
                    Task<LinkLatencyStats> tWifi = LatencyMonitor.ProbeLinkAsync("Wi-Fi", wifiIp, target, maxPing);
                    await Task.WhenAll(tEth, tWifi).ConfigureAwait(false);
                    _ethLatency = tEth.Result;
                    _wifiLatency = tWifi.Result;

                    if (_ethLatency != null && _ethLatency.HasMeasurement)
                    {
                        _engine.UpdateUplinkHealth("Ethernet", _ethLatency.CurrentPingMs, _ethLatency.JitterMs, _ethLatency.LossPercent, _ethLatency.IsUnhealthy);
                    }
                    if (_wifiLatency != null && _wifiLatency.HasMeasurement)
                    {
                        _engine.UpdateUplinkHealth("Wi-Fi", _wifiLatency.CurrentPingMs, _wifiLatency.JitterMs, _wifiLatency.LossPercent, _wifiLatency.IsUnhealthy);
                    }
                }
                catch { }
                finally
                {
                    _probeInFlight = false;
                }
            });
        }

        private WanMode ResolveEffectiveMode()
        {
            if (!_config.AutoGameMode || _activeGame == null)
            {
                return _config.SelectedMode;
            }

            bool ethUp = _lastSnapshot != null && _lastSnapshot.Ethernet != null && _lastSnapshot.Ethernet.IsUp && !string.IsNullOrEmpty(_lastSnapshot.Ethernet.IPv4);
            bool wifiUp = _lastSnapshot != null && _lastSnapshot.Wifi != null && _lastSnapshot.Wifi.IsUp && !string.IsNullOrEmpty(_lastSnapshot.Wifi.IPv4);

            if (_config.GameModePolicy == GamePolicy.SplitGameEthDownloadWifi)
            {
                // Isolate background browser/download proxy onto Wi-Fi while direct game traffic uses Ethernet
                if (wifiUp && ethUp) return WanMode.GameSplit;
                return _config.SelectedMode;
            }
            if (_config.GameModePolicy == GamePolicy.ForceLowestPingLink)
            {
                return PickLowestLatencyMode(ethUp, wifiUp);
            }
            if (_config.GameModePolicy == GamePolicy.PauseProxyWhileGaming)
            {
                return WanMode.Direct;
            }
            return _config.SelectedMode;
        }

        private WanMode PickLowestLatencyMode(bool ethUp, bool wifiUp)
        {
            if (ethUp && !wifiUp) return WanMode.OnlyEthernet;
            if (wifiUp && !ethUp) return WanMode.OnlyWifi;
            if (!ethUp && !wifiUp) return WanMode.Direct;

            double ethScore = (_ethLatency != null && _ethLatency.HasMeasurement)
                ? (_ethLatency.AvgPingMs + _ethLatency.JitterMs * 2 + _ethLatency.LossPercent * 10)
                : 15.0; // Wired Ethernet default preference before first probe
            double wifiScore = (_wifiLatency != null && _wifiLatency.HasMeasurement)
                ? (_wifiLatency.AvgPingMs + _wifiLatency.JitterMs * 2 + _wifiLatency.LossPercent * 10)
                : 35.0;

            return ethScore <= wifiScore ? WanMode.OnlyEthernet : WanMode.OnlyWifi;
        }

        private string DescribeActiveGameMode(WanMode effectiveMode)
        {
            if (_activeGame == null) return "";
            if (!_config.AutoGameMode)
            {
                return string.Format("Game Detected: {0} (Auto Game Mode is off)", _activeGame.DisplayName);
            }
            if (_config.GameModePolicy == GamePolicy.SplitGameEthDownloadWifi)
            {
                return string.Format("GAME MODE [{0}]: Direct game traffic stays on low-ping Ethernet; browser/downloads isolated to Wi-Fi.", _activeGame.DisplayName);
            }
            if (_config.GameModePolicy == GamePolicy.ForceLowestPingLink)
            {
                return string.Format("GAME MODE [{0}]: Switched proxy to lowest-ping link ({1}).", _activeGame.DisplayName, effectiveMode);
            }
            if (_config.GameModePolicy == GamePolicy.PauseProxyWhileGaming)
            {
                return string.Format("GAME MODE [{0}]: Proxy paused for direct OS routing.", _activeGame.DisplayName);
            }
            return string.Format("GAME MODE [{0}]: Combined mode with Sticky Host Affinity active.", _activeGame.DisplayName);
        }

        private void RefreshAll(bool forceReconfigure)
        {
            _lastSnapshot = NetworkDiscovery.CaptureSnapshot();

            // Check running games every 2 seconds (or on forced refresh)
            if (forceReconfigure || _tickCount % 2 == 0)
            {
                DetectedGameInfo prevGame = _activeGame;
                _activeGame = GameDetector.DetectActiveGame(_config.CustomGames, _config.AllowHeuristicGameDetect);

                if (_config.AutoGameMode && _config.BoostGamePriority)
                {
                    if (_activeGame != null && _activeGame.ProcessId != _boostedGamePid)
                    {
                        if (_boostedGamePid > 4) GameDetector.TryBoostProcessPriority(_boostedGamePid, false);
                        _boostedGamePid = _activeGame.ProcessId;
                        GameDetector.TryBoostProcessPriority(_boostedGamePid, true);
                    }
                    else if (_activeGame == null && _boostedGamePid > 4)
                    {
                        GameDetector.TryBoostProcessPriority(_boostedGamePid, false);
                        _boostedGamePid = -1;
                    }
                }
                else if (_boostedGamePid > 4)
                {
                    GameDetector.TryBoostProcessPriority(_boostedGamePid, false);
                    _boostedGamePid = -1;
                }

                if ((prevGame == null) != (_activeGame == null))
                {
                    forceReconfigure = true;
                }
            }

            WanMode effectiveMode = ResolveEffectiveMode();
            try
            {
                ReconcileEngineAndProxy(_lastSnapshot, effectiveMode, forceReconfigure);
                _lastEngineError = "";
            }
            catch (Exception ex)
            {
                _lastEngineError = "Proxy error: " + ex.Message;
            }

            List<UplinkEndpoint> proxyStats = _engine.IsRunning ? _engine.GetUplinkStats() : null;
            _speedBar.UpdateMetrics(_lastSnapshot, effectiveMode, _config.UseMbps, _ethLatency, _wifiLatency, _config.AutoGameMode ? _activeGame : null);
            _mainForm.UpdateLiveView(
                _lastSnapshot,
                proxyStats,
                effectiveMode,
                _config.UseMbps,
                _engine.IsRunning ? _engine.ActiveHttpPort : 0,
                _engine.IsRunning ? _engine.ActiveSocksPort : 0,
                _lastEngineError,
                _ethLatency,
                _wifiLatency,
                _activeGame,
                DescribeActiveGameMode(effectiveMode));

            UpdateTrayStatus(_lastSnapshot, effectiveMode);
            SyncMenuChecks();
        }

        private void ReconcileEngineAndProxy(DualWanSnapshot snap, WanMode effectiveMode, bool forceReconfigure)
        {
            List<UplinkEndpoint> uplinks = new List<UplinkEndpoint>();
            bool ethReady = snap != null && snap.Ethernet != null && snap.Ethernet.IsUp && !string.IsNullOrEmpty(snap.Ethernet.IPv4);
            bool wifiReady = snap != null && snap.Wifi != null && snap.Wifi.IsUp && !string.IsNullOrEmpty(snap.Wifi.IPv4);

            if (effectiveMode == WanMode.Combined)
            {
                if (ethReady)
                {
                    uplinks.Add(new UplinkEndpoint
                    {
                        Label = "Ethernet",
                        LocalAddress = IPAddress.Parse(snap.Ethernet.IPv4),
                        Weight = _config.EthernetWeight
                    });
                }
                if (wifiReady)
                {
                    uplinks.Add(new UplinkEndpoint
                    {
                        Label = "Wi-Fi",
                        LocalAddress = IPAddress.Parse(snap.Wifi.IPv4),
                        Weight = _config.WifiWeight
                    });
                }
            }
            else if (effectiveMode == WanMode.GameSplit)
            {
                // Proxy routes browser/background downloads over Wi-Fi; direct OS games stay on Ethernet (Metric 10)
                if (wifiReady)
                {
                    uplinks.Add(new UplinkEndpoint
                    {
                        Label = "Wi-Fi",
                        LocalAddress = IPAddress.Parse(snap.Wifi.IPv4),
                        Weight = 1
                    });
                }
                else if (ethReady)
                {
                    uplinks.Add(new UplinkEndpoint
                    {
                        Label = "Ethernet",
                        LocalAddress = IPAddress.Parse(snap.Ethernet.IPv4),
                        Weight = 1
                    });
                }
            }
            else if (effectiveMode == WanMode.OnlyEthernet && ethReady)
            {
                uplinks.Add(new UplinkEndpoint
                {
                    Label = "Ethernet",
                    LocalAddress = IPAddress.Parse(snap.Ethernet.IPv4),
                    Weight = 1
                });
            }
            else if (effectiveMode == WanMode.OnlyWifi && wifiReady)
            {
                uplinks.Add(new UplinkEndpoint
                {
                    Label = "Wi-Fi",
                    LocalAddress = IPAddress.Parse(snap.Wifi.IPv4),
                    Weight = 1
                });
            }

            string sig = string.Format("{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                effectiveMode,
                _config.Strategy,
                _config.StickyHostAffinity,
                _config.LatencyFailover,
                _config.AutoSetSystemProxy,
                ethReady ? snap.Ethernet.IPv4 + ":" + _config.EthernetWeight : "none",
                wifiReady ? snap.Wifi.IPv4 + ":" + _config.WifiWeight : "none");

            if (!forceReconfigure && sig == _lastUplinkSignature)
            {
                return;
            }
            _lastUplinkSignature = sig;

            if (effectiveMode == WanMode.Direct || uplinks.Count == 0)
            {
                if (_engine.IsRunning) _engine.Stop();
                if (_config.ProxyDirty) NetworkDiscovery.RestoreSystemProxy(_config);
                return;
            }

            _engine.Configure(uplinks, _config.Strategy, _config.HttpPort, _config.SocksPort, _config.StickyHostAffinity, _config.LatencyFailover);

            if (_config.AutoSetSystemProxy)
            {
                NetworkDiscovery.EnableLocalSystemProxy(_config, _engine.ActiveHttpPort);
            }
            else if (_config.ProxyDirty)
            {
                NetworkDiscovery.RestoreSystemProxy(_config);
            }
        }

        private void UpdateTrayStatus(DualWanSnapshot snap, WanMode effectiveMode)
        {
            double dl = ((snap != null && snap.Ethernet != null) ? snap.Ethernet.DownloadBytesPerSec : 0) +
                        ((snap != null && snap.Wifi != null) ? snap.Wifi.DownloadBytesPerSec : 0);
            double ul = ((snap != null && snap.Ethernet != null) ? snap.Ethernet.UploadBytesPerSec : 0) +
                        ((snap != null && snap.Wifi != null) ? snap.Wifi.UploadBytesPerSec : 0);

            bool gameActive = _config.AutoGameMode && _activeGame != null;
            string modeLabel = gameActive ? ("GAME:" + _activeGame.DisplayName) : effectiveMode.ToString();
            string tip = string.Format("DualWAN [{0}] | DL {1} | UL {2}",
                modeLabel,
                NetworkDiscovery.FormatRate(dl, _config.UseMbps),
                NetworkDiscovery.FormatRate(ul, _config.UseMbps));
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            _trayIcon.Text = tip;

            bool warn = snap == null ||
                        (effectiveMode == WanMode.Combined && (snap.Ethernet == null || !snap.Ethernet.IsUp || snap.Wifi == null || !snap.Wifi.IsUp)) ||
                        (effectiveMode == WanMode.OnlyEthernet && (snap.Ethernet == null || !snap.Ethernet.IsUp)) ||
                        (effectiveMode == WanMode.OnlyWifi && (snap.Wifi == null || !snap.Wifi.IsUp));

            Icon nextIcon = IconGenerator.CreateTrayIcon(effectiveMode, warn, gameActive);
            Icon prev = _currentTrayIcon;
            _currentTrayIcon = nextIcon;
            _trayIcon.Icon = nextIcon;
            _mainForm.Icon = nextIcon;
            if (prev != null) prev.Dispose();
        }

        private void SyncMenuChecks()
        {
            _miCombined.Checked = _config.SelectedMode == WanMode.Combined;
            _miGameSplit.Checked = _config.SelectedMode == WanMode.GameSplit;
            _miOnlyEth.Checked = _config.SelectedMode == WanMode.OnlyEthernet;
            _miOnlyWifi.Checked = _config.SelectedMode == WanMode.OnlyWifi;
            _miDirect.Checked = _config.SelectedMode == WanMode.Direct;
            _miAutoGame.Checked = _config.AutoGameMode;
            _miShowBar.Checked = _config.ShowSpeedBar;
            _miStartup.Checked = NetworkDiscovery.IsStartupEnabled();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            CleanupBeforeExit();
        }

        private void CleanupBeforeExit()
        {
            try { if (_pollTimer != null) _pollTimer.Stop(); } catch { }
            if (_boostedGamePid > 4)
            {
                GameDetector.TryBoostProcessPriority(_boostedGamePid, false);
                _boostedGamePid = -1;
            }
            try { _config.Save(); } catch { }
            try { _engine.Stop(); } catch { }
            NetworkDiscovery.RestoreSystemProxy(_config);
            try { _trayIcon.Visible = false; } catch { }
        }

        private void ExitGracefully()
        {
            CleanupBeforeExit();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.SessionEnding -= OnSessionEnding;
                try { if (_registeredWait != null) _registeredWait.Unregister(null); } catch { }
                try { if (_showEvent != null) _showEvent.Dispose(); } catch { }
                try { if (_pollTimer != null) _pollTimer.Dispose(); } catch { }
                try { if (_speedBar != null) _speedBar.Dispose(); } catch { }
                try { if (_mainForm != null) _mainForm.Dispose(); } catch { }
                try { if (_trayIcon != null) _trayIcon.Dispose(); } catch { }
                try { if (_currentTrayIcon != null) _currentTrayIcon.Dispose(); } catch { }
                try { if (_engine != null) _engine.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}

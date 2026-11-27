using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DualWanBalancer
{
    public class MainForm : Form
    {
        private readonly AppConfig _config;

        private Label _lblStatusBadge;
        private Button _btnCombined;
        private Button _btnGameSplit;
        private Button _btnOnlyEth;
        private Button _btnOnlyWifi;
        private Button _btnDirect;

        private Label _lblOsRoute;
        private Label _lblWarning;
        private Label _lblGameBanner;
        private Label _lblEthMeta;
        private Label _lblEthSpeed;
        private Label _lblEthLatency;
        private Label _lblEthProxyStats;
        private NumericUpDown _numEthWeight;

        private Label _lblWifiMeta;
        private Label _lblWifiSpeed;
        private Label _lblWifiLatency;
        private Label _lblWifiProxyStats;
        private NumericUpDown _numWifiWeight;

        private CheckBox _chkSystemProxy;
        private CheckBox _chkSpeedBar;
        private CheckBox _chkStartup;
        private CheckBox _chkMbps;
        private ComboBox _cmbStrategy;
        private Label _lblFooterHint;

        // Game Boost & Ping tab controls
        private CheckBox _chkAutoGame;
        private ComboBox _cmbGamePolicy;
        private Label _lblDetectedGame;
        private CheckBox _chkHeuristicGame;
        private CheckBox _chkBoostPriority;
        private CheckBox _chkStickyAffinity;
        private CheckBox _chkLatencyFailover;
        private ComboBox _cmbPingTarget;
        private NumericUpDown _numMaxPing;
        private ListBox _lstCustomGames;
        private ComboBox _cmbRunningApps;
        private TextBox _txtCustomExe;

        private bool _suppressEvents;

        public event Action<WanMode> ModeSelected;
        public event Action SettingsChanged;
        public event Action ApplyOsMetricsRequested;
        public event Action ApplyTcpTweaksRequested;
        public event Action RevertTcpTweaksRequested;
        public event Action ExitAppRequested;

        public MainForm(AppConfig config)
        {
            _config = config;
            InitializeLayout();
            LoadConfigToControls();
        }

        private void InitializeLayout()
        {
            Text = "DualNetwork Speed in Single Router with Gaming Balancer";
            Size = new Size(786, 650);
            MinimumSize = new Size(760, 620);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Header bar
            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 62;
            header.BackColor = Color.FromArgb(30, 41, 59);
            Controls.Add(header);

            Label title = new Label();
            title.Text = "DualNetwork Speed in Single Router with Gaming Balancer";
            title.UseMnemonic = false;
            title.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            title.ForeColor = Color.White;
            title.AutoSize = true;
            title.Location = new Point(20, 11);
            header.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "AI-Built Application  |  Ethernet + Wi-Fi Aggregation & Game Split  |  by Omkar Waghmare";
            sub.UseMnemonic = false;
            sub.Font = new Font("Segoe UI", 8.5f);
            sub.ForeColor = Color.FromArgb(148, 163, 184);
            sub.AutoSize = true;
            sub.Location = new Point(22, 37);
            header.Controls.Add(sub);

            _lblStatusBadge = new Label();
            _lblStatusBadge.Text = "COMBINED";
            _lblStatusBadge.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _lblStatusBadge.ForeColor = Color.White;
            _lblStatusBadge.BackColor = Color.FromArgb(16, 185, 129);
            _lblStatusBadge.TextAlign = ContentAlignment.MiddleCenter;
            _lblStatusBadge.Size = new Size(165, 28);
            _lblStatusBadge.Location = new Point(590, 17);
            header.Controls.Add(_lblStatusBadge);

            TabControl tabs = new TabControl();
            tabs.Location = new Point(12, 70);
            tabs.Size = new Size(746, 488);
            Controls.Add(tabs);

            TabPage tabDash = new TabPage("Dashboard (Speed & Links)");
            tabDash.BackColor = Color.FromArgb(15, 23, 42);
            tabDash.ForeColor = Color.White;
            BuildDashboardTab(tabDash);
            tabs.TabPages.Add(tabDash);

            TabPage tabGame = new TabPage("Game Boost, Ping & Stability");
            tabGame.BackColor = Color.FromArgb(15, 23, 42);
            tabGame.ForeColor = Color.White;
            BuildGameTab(tabGame);
            tabs.TabPages.Add(tabGame);

            // Footer action buttons
            Button btnMetrics = new Button();
            btnMetrics.Text = "Apply OS Metrics (Admin)";
            btnMetrics.FlatStyle = FlatStyle.Flat;
            btnMetrics.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            btnMetrics.BackColor = Color.FromArgb(51, 65, 85);
            btnMetrics.ForeColor = Color.White;
            btnMetrics.Size = new Size(176, 34);
            btnMetrics.Location = new Point(14, 568);
            btnMetrics.Click += delegate { if (ApplyOsMetricsRequested != null) ApplyOsMetricsRequested(); };
            Controls.Add(btnMetrics);

            _lblFooterHint = new Label();
            _lblFooterHint.Text = "HTTP/HTTPS: 127.0.0.1:8080  |  SOCKS5: 127.0.0.1:1080";
            _lblFooterHint.Font = new Font("Segoe UI", 8f);
            _lblFooterHint.ForeColor = Color.FromArgb(148, 163, 184);
            _lblFooterHint.AutoSize = true;
            _lblFooterHint.Location = new Point(198, 577);
            Controls.Add(_lblFooterHint);

            Button btnHide = new Button();
            btnHide.Text = "Run in Background";
            btnHide.FlatStyle = FlatStyle.Flat;
            btnHide.FlatAppearance.BorderColor = Color.FromArgb(59, 130, 246);
            btnHide.BackColor = Color.FromArgb(30, 58, 138);
            btnHide.ForeColor = Color.White;
            btnHide.Size = new Size(148, 34);
            btnHide.Location = new Point(442, 568);
            btnHide.Click += delegate { HideToBackground(); };
            Controls.Add(btnHide);

            Button btnExit = new Button();
            btnExit.Text = "Exit & Restore Proxy";
            btnExit.UseMnemonic = false;
            btnExit.FlatStyle = FlatStyle.Flat;
            btnExit.FlatAppearance.BorderColor = Color.FromArgb(220, 38, 38);
            btnExit.BackColor = Color.FromArgb(127, 29, 29);
            btnExit.ForeColor = Color.White;
            btnExit.Size = new Size(158, 34);
            btnExit.Location = new Point(598, 568);
            btnExit.Click += delegate { if (ExitAppRequested != null) ExitAppRequested(); };
            Controls.Add(btnExit);

            FormClosing += OnMainFormClosing;
        }

        private void BuildDashboardTab(TabPage page)
        {
            Label lblModes = new Label();
            lblModes.Text = "Select Connection Mode";
            lblModes.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            lblModes.ForeColor = Color.FromArgb(226, 232, 240);
            lblModes.AutoSize = true;
            lblModes.Location = new Point(12, 8);
            page.Controls.Add(lblModes);

            _lblOsRoute = new Label();
            _lblOsRoute.Text = "Direct / Game OS Route: checking...";
            _lblOsRoute.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _lblOsRoute.ForeColor = Color.FromArgb(148, 163, 184);
            _lblOsRoute.AutoSize = true;
            _lblOsRoute.Location = new Point(280, 10);
            page.Controls.Add(_lblOsRoute);

            _btnCombined = CreateModeButton("Combined\nETH + Wi-Fi", new Point(12, 32), new Size(137, 58));
            _btnGameSplit = CreateModeButton("Game Split\nETH:Game | WiFi:DL", new Point(156, 32), new Size(146, 58));
            _btnOnlyEth = CreateModeButton("Only Ethernet\nCable Only", new Point(309, 32), new Size(137, 58));
            _btnOnlyWifi = CreateModeButton("Only Wi-Fi\nWireless Only", new Point(453, 32), new Size(137, 58));
            _btnDirect = CreateModeButton("Direct (Pause)\nNo Proxy", new Point(597, 32), new Size(129, 58));

            _btnCombined.Click += delegate { TriggerMode(WanMode.Combined); };
            _btnGameSplit.Click += delegate { TriggerMode(WanMode.GameSplit); };
            _btnOnlyEth.Click += delegate { TriggerMode(WanMode.OnlyEthernet); };
            _btnOnlyWifi.Click += delegate { TriggerMode(WanMode.OnlyWifi); };
            _btnDirect.Click += delegate { TriggerMode(WanMode.Direct); };

            page.Controls.Add(_btnCombined);
            page.Controls.Add(_btnGameSplit);
            page.Controls.Add(_btnOnlyEth);
            page.Controls.Add(_btnOnlyWifi);
            page.Controls.Add(_btnDirect);

            _lblGameBanner = new Label();
            _lblGameBanner.BackColor = Color.FromArgb(76, 5, 25);
            _lblGameBanner.ForeColor = Color.FromArgb(253, 164, 175);
            _lblGameBanner.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            _lblGameBanner.Location = new Point(12, 100);
            _lblGameBanner.Size = new Size(714, 24);
            _lblGameBanner.Padding = new Padding(8, 3, 8, 3);
            _lblGameBanner.Visible = false;
            page.Controls.Add(_lblGameBanner);

            _lblWarning = new Label();
            _lblWarning.BackColor = Color.FromArgb(69, 26, 3);
            _lblWarning.ForeColor = Color.FromArgb(253, 230, 138);
            _lblWarning.Font = new Font("Segoe UI", 8.2f);
            _lblWarning.Location = new Point(12, 128);
            _lblWarning.Size = new Size(714, 34);
            _lblWarning.Padding = new Padding(8, 3, 8, 3);
            _lblWarning.Visible = false;
            page.Controls.Add(_lblWarning);

            Panel ethCard = CreateAdapterCard("Ethernet Adapter", new Point(12, 170), out _lblEthMeta, out _lblEthSpeed, out _lblEthLatency, out _lblEthProxyStats, out _numEthWeight);
            Panel wifiCard = CreateAdapterCard("Wi-Fi Adapter", new Point(374, 170), out _lblWifiMeta, out _lblWifiSpeed, out _lblWifiLatency, out _lblWifiProxyStats, out _numWifiWeight);
            _numEthWeight.ValueChanged += OnControlSettingChanged;
            _numWifiWeight.ValueChanged += OnControlSettingChanged;
            page.Controls.Add(ethCard);
            page.Controls.Add(wifiCard);

            Panel settingsCard = new Panel();
            settingsCard.BackColor = Color.FromArgb(30, 41, 59);
            settingsCard.Location = new Point(12, 354);
            settingsCard.Size = new Size(714, 96);
            page.Controls.Add(settingsCard);

            _chkSystemProxy = CreateCheckBox("Auto-set Windows System Proxy (127.0.0.1:8080)", new Point(14, 12));
            _chkSpeedBar = CreateCheckBox("Show Bottom-Right Live Speed & Ping Bar", new Point(14, 38));
            _chkStartup = CreateCheckBox("Start at PC Startup (Run in Background)", new Point(14, 64));
            _chkMbps = CreateCheckBox("Display Speed in Mbps (uncheck for MB/s)", new Point(382, 12));

            Label lblStrat = new Label();
            lblStrat.Text = "Strategy:";
            lblStrat.ForeColor = Color.FromArgb(203, 213, 225);
            lblStrat.AutoSize = true;
            lblStrat.Location = new Point(382, 42);
            settingsCard.Controls.Add(lblStrat);

            _cmbStrategy = new ComboBox();
            _cmbStrategy.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbStrategy.Items.Add("Least-Busy (Auto adaptive)");
            _cmbStrategy.Items.Add("Weighted Round-Robin");
            _cmbStrategy.Items.Add("Lowest Ping (Latency first)");
            _cmbStrategy.Location = new Point(452, 38);
            _cmbStrategy.Size = new Size(246, 26);
            _cmbStrategy.SelectedIndexChanged += OnControlSettingChanged;
            settingsCard.Controls.Add(_cmbStrategy);

            _chkSystemProxy.CheckedChanged += OnControlSettingChanged;
            _chkSpeedBar.CheckedChanged += OnControlSettingChanged;
            _chkStartup.CheckedChanged += OnStartupCheckboxChanged;
            _chkMbps.CheckedChanged += OnControlSettingChanged;

            settingsCard.Controls.Add(_chkSystemProxy);
            settingsCard.Controls.Add(_chkSpeedBar);
            settingsCard.Controls.Add(_chkStartup);
            settingsCard.Controls.Add(_chkMbps);
        }

        private void BuildGameTab(TabPage page)
        {
            Panel topCard = new Panel();
            topCard.BackColor = Color.FromArgb(30, 41, 59);
            topCard.Location = new Point(12, 12);
            topCard.Size = new Size(714, 178);
            page.Controls.Add(topCard);

            Label lblGameHdr = new Label();
            lblGameHdr.Text = "Automatic Game Recognition & Anti-Lag Policy";
            lblGameHdr.UseMnemonic = false;
            lblGameHdr.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            lblGameHdr.ForeColor = Color.White;
            lblGameHdr.AutoSize = true;
            lblGameHdr.Location = new Point(14, 10);
            topCard.Controls.Add(lblGameHdr);

            _lblDetectedGame = new Label();
            _lblDetectedGame.Text = "Status: No active game detected";
            _lblDetectedGame.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _lblDetectedGame.ForeColor = Color.FromArgb(148, 163, 184);
            _lblDetectedGame.AutoSize = true;
            _lblDetectedGame.Location = new Point(390, 12);
            topCard.Controls.Add(_lblDetectedGame);

            _chkAutoGame = CreateCheckBox("Enable Auto Game Mode when a game launches", new Point(14, 38));
            _chkHeuristicGame = CreateCheckBox("Also auto-detect unknown Fullscreen + UDP games", new Point(14, 64));
            _chkBoostPriority = CreateCheckBox("Boost game CPU priority (AboveNormal) while active", new Point(14, 90));
            _chkStickyAffinity = CreateCheckBox("Sticky Host Affinity (keep logins/sessions on one link)", new Point(372, 38));
            _chkLatencyFailover = CreateCheckBox("Auto-avoid high-ping or packet-loss adapter", new Point(372, 64));

            Label lblPolicy = new Label();
            lblPolicy.Text = "When Game Runs:";
            lblPolicy.ForeColor = Color.FromArgb(203, 213, 225);
            lblPolicy.AutoSize = true;
            lblPolicy.Location = new Point(14, 124);
            topCard.Controls.Add(lblPolicy);

            _cmbGamePolicy = new ComboBox();
            _cmbGamePolicy.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbGamePolicy.Items.Add("Split: Games->Ethernet, Downloads->Wi-Fi (Recommended)");
            _cmbGamePolicy.Items.Add("Switch Proxy to Lowest-Ping Link");
            _cmbGamePolicy.Items.Add("Pause Proxy While Gaming (Direct OS Routing)");
            _cmbGamePolicy.Items.Add("Keep Combined + Sticky Host Routing");
            _cmbGamePolicy.Location = new Point(132, 120);
            _cmbGamePolicy.Size = new Size(440, 26);
            topCard.Controls.Add(_cmbGamePolicy);

            Label lblPingCfg = new Label();
            lblPingCfg.Text = "Ping Target:";
            lblPingCfg.ForeColor = Color.FromArgb(203, 213, 225);
            lblPingCfg.AutoSize = true;
            lblPingCfg.Location = new Point(14, 151);
            topCard.Controls.Add(lblPingCfg);

            _cmbPingTarget = new ComboBox();
            _cmbPingTarget.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbPingTarget.Items.Add("1.1.1.1");
            _cmbPingTarget.Items.Add("8.8.8.8");
            _cmbPingTarget.Items.Add("9.9.9.9");
            _cmbPingTarget.Location = new Point(98, 147);
            _cmbPingTarget.Size = new Size(100, 24);
            topCard.Controls.Add(_cmbPingTarget);

            Label lblMaxPing = new Label();
            lblMaxPing.Text = "Unhealthy Ping > (ms):";
            lblMaxPing.ForeColor = Color.FromArgb(203, 213, 225);
            lblMaxPing.AutoSize = true;
            lblMaxPing.Location = new Point(220, 151);
            topCard.Controls.Add(lblMaxPing);

            _numMaxPing = new NumericUpDown();
            _numMaxPing.Minimum = 40;
            _numMaxPing.Maximum = 1000;
            _numMaxPing.Increment = 10;
            _numMaxPing.Location = new Point(362, 148);
            _numMaxPing.Size = new Size(70, 24);
            topCard.Controls.Add(_numMaxPing);

            _chkAutoGame.CheckedChanged += OnGameSettingChanged;
            _chkHeuristicGame.CheckedChanged += OnGameSettingChanged;
            _chkBoostPriority.CheckedChanged += OnGameSettingChanged;
            _chkStickyAffinity.CheckedChanged += OnGameSettingChanged;
            _chkLatencyFailover.CheckedChanged += OnGameSettingChanged;
            _cmbGamePolicy.SelectedIndexChanged += OnGameSettingChanged;
            _cmbPingTarget.SelectedIndexChanged += OnGameSettingChanged;
            _numMaxPing.ValueChanged += OnGameSettingChanged;

            topCard.Controls.Add(_chkAutoGame);
            topCard.Controls.Add(_chkHeuristicGame);
            topCard.Controls.Add(_chkBoostPriority);
            topCard.Controls.Add(_chkStickyAffinity);
            topCard.Controls.Add(_chkLatencyFailover);

            // Custom Game List Card
            Panel listCard = new Panel();
            listCard.BackColor = Color.FromArgb(30, 41, 59);
            listCard.Location = new Point(12, 198);
            listCard.Size = new Size(714, 174);
            page.Controls.Add(listCard);

            Label lblCustom = new Label();
            lblCustom.Text = "Custom Game Processes (60+ popular games built in automatically; add any extra .exe below)";
            lblCustom.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            lblCustom.ForeColor = Color.White;
            lblCustom.AutoSize = true;
            lblCustom.Location = new Point(14, 10);
            listCard.Controls.Add(lblCustom);

            _lstCustomGames = new ListBox();
            _lstCustomGames.BackColor = Color.FromArgb(15, 23, 42);
            _lstCustomGames.ForeColor = Color.White;
            _lstCustomGames.Location = new Point(14, 36);
            _lstCustomGames.Size = new Size(250, 124);
            listCard.Controls.Add(_lstCustomGames);

            Label lblRunApps = new Label();
            lblRunApps.Text = "Pick from running apps:";
            lblRunApps.ForeColor = Color.FromArgb(148, 163, 184);
            lblRunApps.AutoSize = true;
            lblRunApps.Location = new Point(280, 38);
            listCard.Controls.Add(lblRunApps);

            _cmbRunningApps = new ComboBox();
            _cmbRunningApps.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRunningApps.Location = new Point(280, 58);
            _cmbRunningApps.Size = new Size(240, 26);
            listCard.Controls.Add(_cmbRunningApps);

            Button btnRefreshApps = CreateSmallButton("Refresh", new Point(528, 57), new Size(80, 26));
            Button btnAddRunning = CreateSmallButton("Add App", new Point(614, 57), new Size(84, 26));
            btnRefreshApps.Click += delegate { PopulateRunningApps(); };
            btnAddRunning.Click += delegate
            {
                if (_cmbRunningApps.SelectedItem != null)
                {
                    AddCustomGame(_cmbRunningApps.SelectedItem.ToString());
                }
            };
            listCard.Controls.Add(btnRefreshApps);
            listCard.Controls.Add(btnAddRunning);

            Label lblManual = new Label();
            lblManual.Text = "Or type process name (e.g. bgmi or MyGame.exe):";
            lblManual.ForeColor = Color.FromArgb(148, 163, 184);
            lblManual.AutoSize = true;
            lblManual.Location = new Point(280, 94);
            listCard.Controls.Add(lblManual);

            _txtCustomExe = new TextBox();
            _txtCustomExe.BackColor = Color.FromArgb(15, 23, 42);
            _txtCustomExe.ForeColor = Color.White;
            _txtCustomExe.Location = new Point(280, 114);
            _txtCustomExe.Size = new Size(240, 26);
            listCard.Controls.Add(_txtCustomExe);

            Button btnAddCustom = CreateSmallButton("Add Name", new Point(528, 113), new Size(80, 26));
            Button btnRemoveCustom = CreateSmallButton("Remove", new Point(614, 113), new Size(84, 26));
            btnAddCustom.Click += delegate
            {
                AddCustomGame(_txtCustomExe.Text);
                _txtCustomExe.Clear();
            };
            btnRemoveCustom.Click += delegate
            {
                if (_lstCustomGames.SelectedItem != null)
                {
                    string rem = _lstCustomGames.SelectedItem.ToString();
                    _config.CustomGames.Remove(rem);
                    _config.Save();
                    RefreshCustomGamesList();
                }
            };
            listCard.Controls.Add(btnAddCustom);
            listCard.Controls.Add(btnRemoveCustom);

            // Windows Latency & Stability Tweaks Card
            Panel tweakCard = new Panel();
            tweakCard.BackColor = Color.FromArgb(30, 41, 59);
            tweakCard.Location = new Point(12, 380);
            tweakCard.Size = new Size(714, 72);
            page.Controls.Add(tweakCard);

            Label lblTweak = new Label();
            lblTweak.Text = "Windows Low-Latency & Stability Tweaks (Nagle off: TcpAckFrequency=1, TCPNoDelay=1; NetworkThrottlingIndex=off)";
            lblTweak.UseMnemonic = false;
            lblTweak.ForeColor = Color.FromArgb(203, 213, 225);
            lblTweak.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            lblTweak.AutoSize = true;
            lblTweak.Location = new Point(14, 10);
            tweakCard.Controls.Add(lblTweak);

            Button btnApplyTweaks = CreateSmallButton("Apply Low-Latency Tweaks (Admin)", new Point(14, 34), new Size(240, 28));
            btnApplyTweaks.BackColor = Color.FromArgb(6, 95, 70);
            btnApplyTweaks.Click += delegate { if (ApplyTcpTweaksRequested != null) ApplyTcpTweaksRequested(); };
            tweakCard.Controls.Add(btnApplyTweaks);

            Button btnRevertTweaks = CreateSmallButton("Revert Windows Tweaks (Admin)", new Point(266, 34), new Size(220, 28));
            btnRevertTweaks.Click += delegate { if (RevertTcpTweaksRequested != null) RevertTcpTweaksRequested(); };
            tweakCard.Controls.Add(btnRevertTweaks);

            PopulateRunningApps();
        }

        private static Button CreateSmallButton(string text, Point loc, Size size)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = loc;
            btn.Size = size;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            btn.BackColor = Color.FromArgb(51, 65, 85);
            btn.ForeColor = Color.White;
            btn.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private static Button CreateModeButton(string text, Point loc, Size size)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Location = loc;
            btn.Size = size;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 2;
            btn.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
            btn.BackColor = Color.FromArgb(30, 41, 59);
            btn.ForeColor = Color.White;
            btn.Font = new Font("Segoe UI", 9.2f, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private static CheckBox CreateCheckBox(string text, Point loc)
        {
            CheckBox chk = new CheckBox();
            chk.Text = text;
            chk.UseMnemonic = false;
            chk.Location = loc;
            chk.AutoSize = true;
            chk.ForeColor = Color.FromArgb(226, 232, 240);
            return chk;
        }

        private static Panel CreateAdapterCard(
            string heading,
            Point loc,
            out Label metaLabel,
            out Label speedLabel,
            out Label latencyLabel,
            out Label proxyStatsLabel,
            out NumericUpDown weightControl)
        {
            Panel card = new Panel();
            card.BackColor = Color.FromArgb(30, 41, 59);
            card.Location = loc;
            card.Size = new Size(352, 174);

            Label lblTitle = new Label();
            lblTitle.Text = heading;
            lblTitle.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(12, 10);
            card.Controls.Add(lblTitle);

            Label lblW = new Label();
            lblW.Text = "Weight:";
            lblW.ForeColor = Color.FromArgb(148, 163, 184);
            lblW.Font = new Font("Segoe UI", 8.5f);
            lblW.AutoSize = true;
            lblW.Location = new Point(234, 13);
            card.Controls.Add(lblW);

            weightControl = new NumericUpDown();
            weightControl.Minimum = 1;
            weightControl.Maximum = 10;
            weightControl.Value = 1;
            weightControl.Location = new Point(288, 10);
            weightControl.Size = new Size(48, 24);
            card.Controls.Add(weightControl);

            metaLabel = new Label();
            metaLabel.Text = "Detecting...";
            metaLabel.ForeColor = Color.FromArgb(148, 163, 184);
            metaLabel.Font = new Font("Segoe UI", 8.3f);
            metaLabel.Location = new Point(12, 36);
            metaLabel.Size = new Size(328, 42);
            card.Controls.Add(metaLabel);

            speedLabel = new Label();
            speedLabel.Text = "↓ 0.0 Mbps   |   ↑ 0.0 Mbps";
            speedLabel.ForeColor = Color.FromArgb(52, 211, 153);
            speedLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            speedLabel.Location = new Point(12, 84);
            speedLabel.Size = new Size(328, 22);
            card.Controls.Add(speedLabel);

            latencyLabel = new Label();
            latencyLabel.Text = "Ping: -- ms  |  Jitter: --  |  Loss: --";
            latencyLabel.ForeColor = Color.FromArgb(251, 191, 36);
            latencyLabel.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            latencyLabel.Location = new Point(12, 112);
            latencyLabel.Size = new Size(328, 22);
            card.Controls.Add(latencyLabel);

            proxyStatsLabel = new Label();
            proxyStatsLabel.Text = "Proxy traffic: --";
            proxyStatsLabel.ForeColor = Color.FromArgb(56, 189, 248);
            proxyStatsLabel.Font = new Font("Segoe UI", 8f);
            proxyStatsLabel.Location = new Point(12, 140);
            proxyStatsLabel.Size = new Size(328, 22);
            card.Controls.Add(proxyStatsLabel);

            return card;
        }

        private void PopulateRunningApps()
        {
            _cmbRunningApps.Items.Clear();
            List<string> apps = GameDetector.ListCandidateRunningApps();
            for (int i = 0; i < apps.Count; i++)
            {
                _cmbRunningApps.Items.Add(apps[i]);
            }
            if (_cmbRunningApps.Items.Count > 0) _cmbRunningApps.SelectedIndex = 0;
        }

        private void AddCustomGame(string rawName)
        {
            string clean = (rawName ?? "").Trim();
            if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(0, clean.Length - 4);
            }
            if (string.IsNullOrEmpty(clean)) return;

            for (int i = 0; i < _config.CustomGames.Count; i++)
            {
                if (string.Equals(_config.CustomGames[i], clean, StringComparison.OrdinalIgnoreCase)) return;
            }
            _config.CustomGames.Add(clean);
            _config.Save();
            RefreshCustomGamesList();
        }

        private void RefreshCustomGamesList()
        {
            _lstCustomGames.Items.Clear();
            if (_config.CustomGames != null)
            {
                for (int i = 0; i < _config.CustomGames.Count; i++)
                {
                    _lstCustomGames.Items.Add(_config.CustomGames[i]);
                }
            }
        }

        private void LoadConfigToControls()
        {
            _suppressEvents = true;
            try
            {
                _numEthWeight.Value = Math.Max(1, Math.Min(10, _config.EthernetWeight));
                _numWifiWeight.Value = Math.Max(1, Math.Min(10, _config.WifiWeight));
                _chkSystemProxy.Checked = _config.AutoSetSystemProxy;
                _chkSpeedBar.Checked = _config.ShowSpeedBar;
                _chkMbps.Checked = _config.UseMbps;
                _chkStartup.Checked = NetworkDiscovery.IsStartupEnabled();

                if (_config.Strategy == BalanceStrategy.LeastBusy) _cmbStrategy.SelectedIndex = 0;
                else if (_config.Strategy == BalanceStrategy.RoundRobin) _cmbStrategy.SelectedIndex = 1;
                else _cmbStrategy.SelectedIndex = 2;

                _chkAutoGame.Checked = _config.AutoGameMode;
                _chkHeuristicGame.Checked = _config.AllowHeuristicGameDetect;
                _chkBoostPriority.Checked = _config.BoostGamePriority;
                _chkStickyAffinity.Checked = _config.StickyHostAffinity;
                _chkLatencyFailover.Checked = _config.LatencyFailover;

                int polIdx = (int)_config.GameModePolicy;
                if (polIdx >= 0 && polIdx < _cmbGamePolicy.Items.Count) _cmbGamePolicy.SelectedIndex = polIdx;
                else _cmbGamePolicy.SelectedIndex = 0;

                int targetIdx = _cmbPingTarget.Items.IndexOf(_config.PingProbeTarget);
                _cmbPingTarget.SelectedIndex = targetIdx >= 0 ? targetIdx : 0;
                _numMaxPing.Value = Math.Max(40, Math.Min(1000, _config.MaxHealthyPingMs));

                RefreshCustomGamesList();
                HighlightSelectedMode(_config.SelectedMode, false);
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private void TriggerMode(WanMode mode)
        {
            HighlightSelectedMode(mode, false);
            if (ModeSelected != null) ModeSelected(mode);
        }

        public void HighlightSelectedMode(WanMode effectiveMode, bool gameModeActive)
        {
            ResetButtonStyle(_btnCombined);
            ResetButtonStyle(_btnGameSplit);
            ResetButtonStyle(_btnOnlyEth);
            ResetButtonStyle(_btnOnlyWifi);
            ResetButtonStyle(_btnDirect);

            if (effectiveMode == WanMode.Combined)
            {
                SetActiveStyle(_btnCombined, Color.FromArgb(6, 95, 70), Color.FromArgb(16, 185, 129));
                _lblStatusBadge.Text = gameModeActive ? "GAME + COMBINED" : "COMBINED (BOTH)";
                _lblStatusBadge.BackColor = gameModeActive ? Color.FromArgb(244, 63, 94) : Color.FromArgb(16, 185, 129);
            }
            else if (effectiveMode == WanMode.GameSplit)
            {
                SetActiveStyle(_btnGameSplit, Color.FromArgb(136, 19, 55), Color.FromArgb(244, 63, 94));
                _lblStatusBadge.Text = "GAME->ETH | DL->WIFI";
                _lblStatusBadge.BackColor = Color.FromArgb(244, 63, 94);
            }
            else if (effectiveMode == WanMode.OnlyEthernet)
            {
                SetActiveStyle(_btnOnlyEth, Color.FromArgb(30, 58, 138), Color.FromArgb(59, 130, 246));
                _lblStatusBadge.Text = gameModeActive ? "GAME (ETH)" : "ONLY ETHERNET";
                _lblStatusBadge.BackColor = gameModeActive ? Color.FromArgb(244, 63, 94) : Color.FromArgb(59, 130, 246);
            }
            else if (effectiveMode == WanMode.OnlyWifi)
            {
                SetActiveStyle(_btnOnlyWifi, Color.FromArgb(88, 28, 135), Color.FromArgb(168, 85, 247));
                _lblStatusBadge.Text = "ONLY WI-FI";
                _lblStatusBadge.BackColor = Color.FromArgb(168, 85, 247);
            }
            else
            {
                SetActiveStyle(_btnDirect, Color.FromArgb(51, 65, 85), Color.FromArgb(148, 163, 184));
                _lblStatusBadge.Text = gameModeActive ? "GAME (DIRECT)" : "DIRECT (PAUSED)";
                _lblStatusBadge.BackColor = gameModeActive ? Color.FromArgb(244, 63, 94) : Color.FromArgb(100, 116, 139);
            }
        }

        private static void ResetButtonStyle(Button btn)
        {
            btn.BackColor = Color.FromArgb(30, 41, 59);
            btn.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
        }

        private static void SetActiveStyle(Button btn, Color bg, Color border)
        {
            btn.BackColor = bg;
            btn.FlatAppearance.BorderColor = border;
        }

        private void OnControlSettingChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            _config.EthernetWeight = (int)_numEthWeight.Value;
            _config.WifiWeight = (int)_numWifiWeight.Value;
            _config.AutoSetSystemProxy = _chkSystemProxy.Checked;
            _config.ShowSpeedBar = _chkSpeedBar.Checked;
            _config.UseMbps = _chkMbps.Checked;

            if (_cmbStrategy.SelectedIndex == 0) _config.Strategy = BalanceStrategy.LeastBusy;
            else if (_cmbStrategy.SelectedIndex == 1) _config.Strategy = BalanceStrategy.RoundRobin;
            else _config.Strategy = BalanceStrategy.LowestPing;

            _config.Save();
            if (SettingsChanged != null) SettingsChanged();
        }

        private void OnGameSettingChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            _config.AutoGameMode = _chkAutoGame.Checked;
            _config.AllowHeuristicGameDetect = _chkHeuristicGame.Checked;
            _config.BoostGamePriority = _chkBoostPriority.Checked;
            _config.StickyHostAffinity = _chkStickyAffinity.Checked;
            _config.LatencyFailover = _chkLatencyFailover.Checked;
            _config.GameModePolicy = (GamePolicy)Math.Max(0, _cmbGamePolicy.SelectedIndex);
            _config.PingProbeTarget = _cmbPingTarget.SelectedItem != null ? _cmbPingTarget.SelectedItem.ToString() : "1.1.1.1";
            _config.MaxHealthyPingMs = (int)_numMaxPing.Value;
            _config.Save();
            if (SettingsChanged != null) SettingsChanged();
        }

        private void OnStartupCheckboxChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            bool ok = NetworkDiscovery.SetStartupEnabled(_chkStartup.Checked, Application.ExecutablePath);
            if (!ok)
            {
                _suppressEvents = true;
                _chkStartup.Checked = NetworkDiscovery.IsStartupEnabled();
                _suppressEvents = false;
                MessageBox.Show(this, "Could not update Windows Startup registry entry.", "DualWAN Speed Balancer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void SyncStartupCheckbox()
        {
            _suppressEvents = true;
            _chkStartup.Checked = NetworkDiscovery.IsStartupEnabled();
            _suppressEvents = false;
        }

        public void UpdateLiveView(
            DualWanSnapshot snap,
            List<UplinkEndpoint> proxyStats,
            WanMode effectiveMode,
            bool useMbps,
            int activeHttpPort,
            int activeSocksPort,
            string engineError,
            LinkLatencyStats ethLatency,
            LinkLatencyStats wifiLatency,
            DetectedGameInfo activeGame,
            string gameModeDescription)
        {
            bool gameActive = activeGame != null && _config.AutoGameMode;
            HighlightSelectedMode(effectiveMode, gameActive);

            if (activeHttpPort > 0)
            {
                _chkSystemProxy.Text = string.Format("Auto-set Windows System Proxy (127.0.0.1:{0})", activeHttpPort);
                _lblFooterHint.Text = string.Format("HTTP: 127.0.0.1:{0} | SOCKS5: 127.0.0.1:{1}", activeHttpPort, activeSocksPort);
            }
            else
            {
                _chkSystemProxy.Text = string.Format("Auto-set Windows System Proxy (127.0.0.1:{0})", _config.HttpPort);
                _lblFooterHint.Text = "Proxy paused (Direct OS routing)";
            }

            if (activeGame != null)
            {
                _lblDetectedGame.Text = string.Format("Detected: {0} (PID {1} via {2})", activeGame.DisplayName, activeGame.ProcessId, activeGame.DetectionSource);
                _lblDetectedGame.ForeColor = Color.FromArgb(251, 113, 133);
            }
            else
            {
                _lblDetectedGame.Text = "Status: No active game detected";
                _lblDetectedGame.ForeColor = Color.FromArgb(148, 163, 184);
            }

            if (!string.IsNullOrEmpty(gameModeDescription))
            {
                _lblGameBanner.Text = gameModeDescription;
                _lblGameBanner.Visible = true;
            }
            else
            {
                _lblGameBanner.Visible = false;
            }

            if (snap == null) return;

            if (snap.DefaultOsRouteRole == "Ethernet")
            {
                _lblOsRoute.Text = "Direct / Game OS Route: Ethernet (Lowest Latency)";
                _lblOsRoute.ForeColor = Color.FromArgb(52, 211, 153);
            }
            else if (snap.DefaultOsRouteRole == "Wi-Fi")
            {
                _lblOsRoute.Text = "Direct / Game OS Route: Wi-Fi (Click 'Apply OS Metrics' to move Games -> Ethernet!)";
                _lblOsRoute.ForeColor = Color.FromArgb(251, 191, 36);
            }
            else
            {
                _lblOsRoute.Text = "Direct / Game OS Route: " + (snap.DefaultOsRouteRole ?? "--");
                _lblOsRoute.ForeColor = Color.FromArgb(148, 163, 184);
            }

            UpdateSingleAdapterCard(snap.Ethernet, ethLatency, proxyStats, "Ethernet", _lblEthMeta, _lblEthSpeed, _lblEthLatency, _lblEthProxyStats, useMbps);
            UpdateSingleAdapterCard(snap.Wifi, wifiLatency, proxyStats, "Wi-Fi", _lblWifiMeta, _lblWifiSpeed, _lblWifiLatency, _lblWifiProxyStats, useMbps);

            List<string> warnings = new List<string>();
            if (!string.IsNullOrEmpty(engineError)) warnings.Add(engineError);
            if (snap.Ethernet == null || !snap.Ethernet.IsUp) warnings.Add("Ethernet is disconnected.");
            if (snap.Wifi == null || !snap.Wifi.IsUp) warnings.Add("Wi-Fi is disconnected.");
            if (snap.DefaultOsRouteRole == "Wi-Fi" && snap.Ethernet != null && snap.Ethernet.IsUp && effectiveMode != WanMode.OnlyWifi)
            {
                warnings.Add("Windows is currently routing games/direct traffic via Wi-Fi. Click 'Apply OS Metrics (Admin)' to lock Games onto Ethernet.");
            }
            if (snap.SameSubnetWarning)
            {
                warnings.Add(string.Format("Same subnet {0} (AP/Bridge). Put TP-Link in Router mode (192.168.0.x) for cleanest split.", snap.SharedSubnet));
            }

            if (warnings.Count > 0)
            {
                _lblWarning.Text = "Warning: " + string.Join(" ", warnings.ToArray());
                _lblWarning.Visible = true;
            }
            else
            {
                _lblWarning.Visible = false;
            }
        }

        private static void UpdateSingleAdapterCard(
            AdapterSnapshot adapter,
            LinkLatencyStats latency,
            List<UplinkEndpoint> proxyStats,
            string labelKey,
            Label metaLabel,
            Label speedLabel,
            Label latencyLabel,
            Label proxyLabel,
            bool useMbps)
        {
            if (adapter == null)
            {
                metaLabel.Text = "Adapter not found";
                speedLabel.Text = "↓ --   |   ↑ --";
                speedLabel.ForeColor = Color.FromArgb(148, 163, 184);
                latencyLabel.Text = "Ping: -- ms  |  Jitter: --  |  Loss: --";
                latencyLabel.ForeColor = Color.FromArgb(148, 163, 184);
                proxyLabel.Text = "Proxy traffic: inactive";
                return;
            }

            string statusText = adapter.IsUp ? "Connected" : "Disconnected";
            metaLabel.Text = string.Format("{0} ({1})\nLink: {2}  |  IP: {3}  |  GW: {4}",
                adapter.Name,
                statusText,
                NetworkDiscovery.FormatLinkSpeed(adapter.LinkSpeedBps),
                string.IsNullOrEmpty(adapter.IPv4) ? "--" : adapter.IPv4,
                string.IsNullOrEmpty(adapter.Gateways) ? "--" : adapter.Gateways);

            speedLabel.Text = string.Format("↓ {0}   |   ↑ {1}",
                NetworkDiscovery.FormatRate(adapter.DownloadBytesPerSec, useMbps),
                NetworkDiscovery.FormatRate(adapter.UploadBytesPerSec, useMbps));
            speedLabel.ForeColor = adapter.IsUp ? Color.FromArgb(52, 211, 153) : Color.FromArgb(248, 113, 113);

            if (adapter.IsUp && latency != null && latency.HasMeasurement)
            {
                latencyLabel.Text = string.Format("Ping: {0:0} ms  |  Jitter: ±{1:0.0} ms  |  Loss: {2:0}% ({3})",
                    latency.CurrentPingMs,
                    latency.JitterMs,
                    latency.LossPercent,
                    latency.ProbeMethod);
                if (latency.IsUnhealthy) latencyLabel.ForeColor = Color.FromArgb(248, 113, 113);
                else if (latency.CurrentPingMs <= 45) latencyLabel.ForeColor = Color.FromArgb(52, 211, 153);
                else latencyLabel.ForeColor = Color.FromArgb(251, 191, 36);
            }
            else
            {
                latencyLabel.Text = adapter.IsUp ? "Ping: measuring..." : "Ping: disconnected";
                latencyLabel.ForeColor = Color.FromArgb(148, 163, 184);
            }

            UplinkEndpoint matched = null;
            if (proxyStats != null)
            {
                for (int i = 0; i < proxyStats.Count; i++)
                {
                    if (proxyStats[i].Label == labelKey)
                    {
                        matched = proxyStats[i];
                        break;
                    }
                }
            }

            if (matched != null)
            {
                proxyLabel.Text = string.Format("Proxy: {0} active / {1} total  |  DL {2:0.0} MB  UL {3:0.0} MB",
                    matched.ActiveConnections,
                    matched.TotalConnections,
                    matched.BytesDown / 1048576.0,
                    matched.BytesUp / 1048576.0);
            }
            else
            {
                proxyLabel.Text = "Proxy traffic: idle / bypassed";
            }
        }

        public void ShowAndFocus()
        {
            ShowInTaskbar = true;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            Activate();
        }

        public void HideToBackground()
        {
            ShowInTaskbar = false;
            Hide();
        }

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToBackground();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace DualWanBalancer
{
    public enum WanMode
    {
        Combined = 0,
        OnlyEthernet = 1,
        OnlyWifi = 2,
        Direct = 3,
        GameSplit = 4
    }

    public enum BalanceStrategy
    {
        RoundRobin = 0,
        LeastBusy = 1,
        LowestPing = 2
    }

    public enum GamePolicy
    {
        SplitGameEthDownloadWifi = 0,
        ForceLowestPingLink = 1,
        PauseProxyWhileGaming = 2,
        KeepCombinedWithAffinity = 3
    }

    public class AppConfig
    {
        public WanMode SelectedMode { get; set; }
        public BalanceStrategy Strategy { get; set; }
        public int EthernetWeight { get; set; }
        public int WifiWeight { get; set; }
        public int HttpPort { get; set; }
        public int SocksPort { get; set; }
        public bool AutoSetSystemProxy { get; set; }
        public bool ShowSpeedBar { get; set; }
        public bool UseMbps { get; set; }
        public bool StartupPrompted { get; set; }
        public int SpeedBarX { get; set; }
        public int SpeedBarY { get; set; }

        // Game recognition, ping reduction & stability
        public int ConfigVersion { get; set; }
        public bool AutoGameMode { get; set; }
        public GamePolicy GameModePolicy { get; set; }
        public bool AllowHeuristicGameDetect { get; set; }
        public bool BoostGamePriority { get; set; }
        public bool StickyHostAffinity { get; set; }
        public bool LatencyFailover { get; set; }
        public string PingProbeTarget { get; set; }
        public int MaxHealthyPingMs { get; set; }
        public List<string> CustomGames { get; set; }

        // Saved Windows proxy backup so it can always be restored cleanly
        public bool ProxyBackupSaved { get; set; }
        public int OriginalProxyEnable { get; set; }
        public string OriginalProxyServer { get; set; }
        public string OriginalProxyOverride { get; set; }
        public bool ProxyDirty { get; set; }

        public AppConfig()
        {
            SelectedMode = WanMode.Combined;
            Strategy = BalanceStrategy.LeastBusy;
            EthernetWeight = 1;
            WifiWeight = 2;
            HttpPort = 8080;
            SocksPort = 1080;
            AutoSetSystemProxy = true;
            ShowSpeedBar = true;
            UseMbps = true;
            StartupPrompted = false;
            SpeedBarX = -1;
            SpeedBarY = -1;

            ConfigVersion = 2;
            AutoGameMode = true;
            GameModePolicy = GamePolicy.SplitGameEthDownloadWifi;
            AllowHeuristicGameDetect = true;
            BoostGamePriority = true;
            StickyHostAffinity = true;
            LatencyFailover = true;
            PingProbeTarget = "1.1.1.1";
            MaxHealthyPingMs = 180;
            CustomGames = new List<string>();

            ProxyBackupSaved = false;
            OriginalProxyEnable = 0;
            OriginalProxyServer = "";
            OriginalProxyOverride = "<local>";
            ProxyDirty = false;
        }

        public static string GetConfigDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "DualWanBalancer");
        }

        public static string GetConfigPath()
        {
            return Path.Combine(GetConfigDirectory(), "config.json");
        }

        public static AppConfig Load()
        {
            try
            {
                string path = GetConfigPath();
                if (!File.Exists(path))
                {
                    return new AppConfig();
                }
                string json = File.ReadAllText(path);
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                AppConfig cfg = serializer.Deserialize<AppConfig>(json);
                if (cfg == null)
                {
                    return new AppConfig();
                }
                if (cfg.ConfigVersion < 2)
                {
                    // Migrate v1 configs with sensible v2 defaults
                    cfg.ConfigVersion = 2;
                    cfg.AutoGameMode = true;
                    cfg.GameModePolicy = GamePolicy.SplitGameEthDownloadWifi;
                    cfg.AllowHeuristicGameDetect = true;
                    cfg.BoostGamePriority = true;
                    cfg.StickyHostAffinity = true;
                    cfg.LatencyFailover = true;
                    cfg.PingProbeTarget = "1.1.1.1";
                    cfg.MaxHealthyPingMs = 180;
                }
                if (cfg.EthernetWeight < 1 || cfg.EthernetWeight > 20) cfg.EthernetWeight = 1;
                if (cfg.WifiWeight < 1 || cfg.WifiWeight > 20) cfg.WifiWeight = 1;
                if (cfg.HttpPort < 1 || cfg.HttpPort > 65535) cfg.HttpPort = 8080;
                if (cfg.SocksPort < 0 || cfg.SocksPort > 65535) cfg.SocksPort = 1080;
                if (cfg.MaxHealthyPingMs < 40 || cfg.MaxHealthyPingMs > 1000) cfg.MaxHealthyPingMs = 180;
                if (string.IsNullOrWhiteSpace(cfg.PingProbeTarget)) cfg.PingProbeTarget = "1.1.1.1";
                if (cfg.CustomGames == null) cfg.CustomGames = new List<string>();
                return cfg;
            }
            catch
            {
                return new AppConfig();
            }
        }

        public void Save()
        {
            try
            {
                string dir = GetConfigDirectory();
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(this);
                File.WriteAllText(GetConfigPath(), json);
            }
            catch
            {
                // Ignore non-fatal persistence errors
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DualWanBalancer
{
    public class AdapterSnapshot
    {
        public string Role { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Id { get; set; }
        public int InterfaceIndex { get; set; }
        public bool IsUp { get; set; }
        public long LinkSpeedBps { get; set; }
        public string IPv4 { get; set; }
        public int PrefixLength { get; set; }
        public string SubnetCidr { get; set; }
        public string Gateways { get; set; }
        public long TotalRxBytes { get; set; }
        public long TotalTxBytes { get; set; }
        public double DownloadBytesPerSec { get; set; }
        public double UploadBytesPerSec { get; set; }
    }

    public class DualWanSnapshot
    {
        public AdapterSnapshot Ethernet { get; set; }
        public AdapterSnapshot Wifi { get; set; }
        public bool SameSubnetWarning { get; set; }
        public string SharedSubnet { get; set; }
        public string DefaultOsRouteRole { get; set; }
    }

    public static class NetworkDiscovery
    {
        private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string StartupRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "DualWanBalancer";

        private static readonly Regex VirtualPattern = new Regex(
            @"(loopback|bluetooth|virtual|vethernet|hyper-v|vmware|virtualbox|tunnel|tap|tun|wireguard|zerotier|tailscale|et_\d+|wi-fi direct)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex WirelessPattern = new Regex(
            @"(wi-?fi|wlan|wireless|802\.11)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, long> PrevRx = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> PrevTx = new Dictionary<string, long>();
        private static readonly Dictionary<string, DateTime> PrevTime = new Dictionary<string, DateTime>();

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

        public static int GetBestRouteInterfaceIndex(string destIpStr)
        {
            try
            {
                IPAddress ip;
                if (!IPAddress.TryParse(destIpStr, out ip)) return -1;
                uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
                uint idx;
                if (GetBestInterface(dest, out idx) == 0)
                {
                    return (int)idx;
                }
            }
            catch { }
            return -1;
        }

        public static bool IsVirtualOrTunnel(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                return true;
            }
            string combined = (nic.Name ?? "") + " " + (nic.Description ?? "");
            return VirtualPattern.IsMatch(combined);
        }

        public static bool IsWirelessAdapter(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                return true;
            }
            string combined = (nic.Name ?? "") + " " + (nic.Description ?? "");
            return WirelessPattern.IsMatch(combined);
        }

        public static bool IsPhysicalEthernetAdapter(NetworkInterface nic)
        {
            if (IsVirtualOrTunnel(nic) || IsWirelessAdapter(nic))
            {
                return false;
            }
            return nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                   nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                   nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT ||
                   nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx;
        }

        public static string ComputeCidr(IPAddress ip, IPAddress mask, out int prefixLength)
        {
            prefixLength = 0;
            if (ip == null || mask == null) return "";
            byte[] ipBytes = ip.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();
            if (ipBytes.Length != 4 || maskBytes.Length != 4) return "";

            for (int i = 0; i < 4; i++)
            {
                byte b = maskBytes[i];
                for (int bit = 7; bit >= 0; bit--)
                {
                    if (((b >> bit) & 1) == 1) prefixLength++;
                }
            }

            byte[] netBytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                netBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            }
            return string.Format("{0}.{1}.{2}.{3}/{4}", netBytes[0], netBytes[1], netBytes[2], netBytes[3], prefixLength);
        }

        public static string FormatLinkSpeed(long bps)
        {
            if (bps <= 0) return "--";
            if (bps >= 1000000000L)
            {
                return string.Format("{0:0.##} Gbps", bps / 1000000000.0);
            }
            return string.Format("{0:0.#} Mbps", bps / 1000000.0);
        }

        public static string FormatRate(double bytesPerSec, bool useMbps)
        {
            if (bytesPerSec < 0) bytesPerSec = 0;
            if (useMbps)
            {
                double mbps = (bytesPerSec * 8.0) / 1000000.0;
                if (mbps < 1.0)
                {
                    double kbps = (bytesPerSec * 8.0) / 1000.0;
                    return string.Format("{0:0} Kbps", kbps);
                }
                return string.Format("{0:0.0} Mbps", mbps);
            }
            else
            {
                if (bytesPerSec >= 1048576.0)
                {
                    return string.Format("{0:0.00} MB/s", bytesPerSec / 1048576.0);
                }
                if (bytesPerSec >= 1024.0)
                {
                    return string.Format("{0:0.0} KB/s", bytesPerSec / 1024.0);
                }
                return string.Format("{0:0} B/s", bytesPerSec);
            }
        }

        public static DualWanSnapshot CaptureSnapshot()
        {
            NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
            NetworkInterface bestEth = null;
            NetworkInterface bestWifi = null;

            for (int i = 0; i < all.Length; i++)
            {
                NetworkInterface nic = all[i];
                if (IsVirtualOrTunnel(nic)) continue;

                if (IsPhysicalEthernetAdapter(nic))
                {
                    if (bestEth == null || (bestEth.OperationalStatus != OperationalStatus.Up && nic.OperationalStatus == OperationalStatus.Up))
                    {
                        bestEth = nic;
                    }
                }
                else if (IsWirelessAdapter(nic))
                {
                    if (bestWifi == null || (bestWifi.OperationalStatus != OperationalStatus.Up && nic.OperationalStatus == OperationalStatus.Up))
                    {
                        bestWifi = nic;
                    }
                }
            }

            DualWanSnapshot snap = new DualWanSnapshot();
            snap.Ethernet = BuildAdapterSnapshot("Ethernet", bestEth);
            snap.Wifi = BuildAdapterSnapshot("Wi-Fi", bestWifi);

            if (snap.Ethernet != null && snap.Wifi != null &&
                snap.Ethernet.IsUp && snap.Wifi.IsUp &&
                !string.IsNullOrEmpty(snap.Ethernet.SubnetCidr) &&
                snap.Ethernet.SubnetCidr == snap.Wifi.SubnetCidr)
            {
                snap.SameSubnetWarning = true;
                snap.SharedSubnet = snap.Ethernet.SubnetCidr;
            }

            int bestIdx = GetBestRouteInterfaceIndex("1.1.1.1");
            if (snap.Ethernet != null && snap.Ethernet.InterfaceIndex == bestIdx)
            {
                snap.DefaultOsRouteRole = "Ethernet";
            }
            else if (snap.Wifi != null && snap.Wifi.InterfaceIndex == bestIdx)
            {
                snap.DefaultOsRouteRole = "Wi-Fi";
            }
            else
            {
                snap.DefaultOsRouteRole = "Other";
            }

            return snap;
        }

        private static AdapterSnapshot BuildAdapterSnapshot(string role, NetworkInterface nic)
        {
            if (nic == null) return null;

            AdapterSnapshot item = new AdapterSnapshot();
            item.Role = role;
            item.Name = nic.Name;
            item.Description = nic.Description;
            item.Id = nic.Id;
            item.IsUp = (nic.OperationalStatus == OperationalStatus.Up);
            item.LinkSpeedBps = nic.Speed;

            try
            {
                IPInterfaceProperties props = nic.GetIPProperties();
                try
                {
                    IPv4InterfaceProperties ipv4Props = props.GetIPv4Properties();
                    if (ipv4Props != null) item.InterfaceIndex = ipv4Props.Index;
                }
                catch { }
                for (int i = 0; i < props.UnicastAddresses.Count; i++)
                {
                    UnicastIPAddressInformation uni = props.UnicastAddresses[i];
                    if (uni.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string addrStr = uni.Address.ToString();
                        if (!addrStr.StartsWith("169.254."))
                        {
                            item.IPv4 = addrStr;
                            int prefix;
                            item.SubnetCidr = ComputeCidr(uni.Address, uni.IPv4Mask, out prefix);
                            item.PrefixLength = prefix;
                            break;
                        }
                    }
                }

                List<string> gws = new List<string>();
                for (int i = 0; i < props.GatewayAddresses.Count; i++)
                {
                    GatewayIPAddressInformation gw = props.GatewayAddresses[i];
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        gws.Add(gw.Address.ToString());
                    }
                }
                item.Gateways = string.Join(", ", gws.ToArray());
            }
            catch
            {
                // Ignore transient IP property read failures during adapter transitions
            }

            try
            {
                IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                long rx = stats.BytesReceived;
                long tx = stats.BytesSent;
                item.TotalRxBytes = rx;
                item.TotalTxBytes = tx;

                DateTime now = DateTime.UtcNow;
                long oldRx;
                long oldTx;
                DateTime oldTime;
                if (PrevRx.TryGetValue(nic.Id, out oldRx) &&
                    PrevTx.TryGetValue(nic.Id, out oldTx) &&
                    PrevTime.TryGetValue(nic.Id, out oldTime))
                {
                    double dt = (now - oldTime).TotalSeconds;
                    if (dt > 0.1)
                    {
                        long dRx = rx - oldRx;
                        long dTx = tx - oldTx;
                        if (dRx >= 0) item.DownloadBytesPerSec = dRx / dt;
                        if (dTx >= 0) item.UploadBytesPerSec = dTx / dt;
                    }
                }

                PrevRx[nic.Id] = rx;
                PrevTx[nic.Id] = tx;
                PrevTime[nic.Id] = now;
            }
            catch
            {
                // Ignore transient statistics read failures
            }

            return item;
        }

        public static void EnableLocalSystemProxy(AppConfig cfg, int activeHttpPort)
        {
            int portToUse = activeHttpPort > 0 ? activeHttpPort : cfg.HttpPort;
            string localProxy = string.Format("127.0.0.1:{0}", portToUse);
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, true))
            {
                if (key == null) return;

                if (!cfg.ProxyBackupSaved)
                {
                    object enObj = key.GetValue("ProxyEnable", 0);
                    object srvObj = key.GetValue("ProxyServer", "");
                    object ovrObj = key.GetValue("ProxyOverride", "<local>");
                    string currentSrv = srvObj != null ? srvObj.ToString() : "";

                    if (!currentSrv.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase))
                    {
                        int origEnable = 0;
                        if (enObj != null) int.TryParse(enObj.ToString(), out origEnable);
                        cfg.OriginalProxyEnable = origEnable;
                        cfg.OriginalProxyServer = currentSrv;
                        cfg.OriginalProxyOverride = ovrObj != null ? ovrObj.ToString() : "<local>";
                        cfg.ProxyBackupSaved = true;
                    }
                }

                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", localProxy, RegistryValueKind.String);
                key.SetValue(
                    "ProxyOverride",
                    "<local>;192.168.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*.*;172.30.*;172.31.*",
                    RegistryValueKind.String);
                cfg.ProxyDirty = true;
                cfg.Save();
            }

            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }

        public static void RestoreSystemProxy(AppConfig cfg)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, true))
                {
                    if (key == null) return;

                    if (cfg != null && cfg.ProxyBackupSaved)
                    {
                        key.SetValue("ProxyEnable", cfg.OriginalProxyEnable, RegistryValueKind.DWord);
                        key.SetValue("ProxyServer", cfg.OriginalProxyServer ?? "", RegistryValueKind.String);
                        key.SetValue("ProxyOverride", string.IsNullOrEmpty(cfg.OriginalProxyOverride) ? "<local>" : cfg.OriginalProxyOverride, RegistryValueKind.String);
                        cfg.ProxyBackupSaved = false;
                    }
                    else
                    {
                        string localProxy = string.Format("127.0.0.1:{0}", cfg != null ? cfg.HttpPort : 8080);
                        object srvObj = key.GetValue("ProxyServer", "");
                        if (srvObj != null && string.Equals(srvObj.ToString(), localProxy, StringComparison.OrdinalIgnoreCase))
                        {
                            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                            key.SetValue("ProxyServer", "", RegistryValueKind.String);
                        }
                    }

                    if (cfg != null)
                    {
                        cfg.ProxyDirty = false;
                        cfg.Save();
                    }
                }

                InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
            }
            catch
            {
                // Best-effort restore on exit
            }
        }

        public static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRunKey, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(StartupValueName);
                    return val != null && !string.IsNullOrEmpty(val.ToString());
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetStartupEnabled(bool enable, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRunKey, true))
                {
                    if (key == null) return false;
                    if (enable)
                    {
                        string cmd = string.Format("\"{0}\" --background", exePath);
                        key.SetValue(StartupValueName, cmd, RegistryValueKind.String);
                    }
                    else
                    {
                        if (key.GetValue(StartupValueName) != null)
                        {
                            key.DeleteValue(StartupValueName, false);
                        }
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool IsAdministrator()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public static bool ApplyOsInterfaceMetrics(DualWanSnapshot snap, WanMode mode, out string message)
        {
            message = "";
            if (snap == null)
            {
                message = "No network adapters detected.";
                return false;
            }

            List<string> cmds = new List<string>();
            if (snap.Ethernet != null && snap.Ethernet.IsUp)
            {
                string ethName = snap.Ethernet.Name.Replace("'", "''");
                if (mode == WanMode.OnlyEthernet || mode == WanMode.GameSplit || mode == WanMode.Combined)
                {
                    // Prioritize Ethernet (lowest metric) so Games & direct OS traffic always use wired cable
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 10", ethName));
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv6 -AutomaticMetric Disabled -InterfaceMetric 10", ethName));
                }
                else if (mode == WanMode.OnlyWifi)
                {
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 50", ethName));
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv6 -AutomaticMetric Disabled -InterfaceMetric 50", ethName));
                }
                else
                {
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv4 -AutomaticMetric Enabled", ethName));
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv6 -AutomaticMetric Enabled", ethName));
                }
            }

            if (snap.Wifi != null && snap.Wifi.IsUp)
            {
                string wifiName = snap.Wifi.Name.Replace("'", "''");
                if (mode == WanMode.OnlyWifi)
                {
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 10", wifiName));
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv6 -AutomaticMetric Disabled -InterfaceMetric 10", wifiName));
                }
                else if (mode == WanMode.OnlyEthernet || mode == WanMode.GameSplit || mode == WanMode.Combined)
                {
                    // Higher OS metric keeps games off Wi-Fi; proxy still binds Wi-Fi IP explicitly when needed
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 50", wifiName));
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv6 -AutomaticMetric Disabled -InterfaceMetric 50", wifiName));
                }
                else
                {
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv4 -AutomaticMetric Enabled", wifiName));
                    cmds.Add(string.Format("Set-NetIPInterface -InterfaceAlias '{0}' -AddressFamily IPv6 -AutomaticMetric Enabled", wifiName));
                }
            }

            if (cmds.Count == 0)
            {
                message = "No active adapters available to update metrics.";
                return false;
            }

            try
            {
                string joined = string.Join("; ", cmds.ToArray());
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -Command \"{0}\"", joined.Replace("\"", "\\\""));
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                if (!IsAdministrator())
                {
                    psi.Verb = "runas";
                }
                Process proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(15000);
                    if (proc.ExitCode == 0)
                    {
                        message = "Windows interface metrics updated for " + mode.ToString() + ".";
                        return true;
                    }
                    message = "Metric update exited with code " + proc.ExitCode;
                    return false;
                }
                return false;
            }
            catch (Exception ex)
            {
                message = "Could not apply OS interface metrics: " + ex.Message;
                return false;
            }
        }
    }
}

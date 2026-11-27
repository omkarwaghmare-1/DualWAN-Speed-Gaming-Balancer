using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DualWanBalancer
{
    public static class NetworkOptimizer
    {
        private static string GetBackupFilePath()
        {
            return Path.Combine(AppConfig.GetConfigDirectory(), "tcp-tweaks-backup.json");
        }

        /// <summary>
        /// Applies reversible Windows low-latency TCP/multimedia tweaks via elevated PowerShell:
        /// 1. NetworkThrottlingIndex = 0xFFFFFFFF & SystemResponsiveness = 0
        /// 2. TcpAckFrequency = 1 & TCPNoDelay = 1 (Disable Nagle) on active adapter GUIDs
        /// </summary>
        public static bool ApplyLowLatencyTweaks(DualWanSnapshot snap, out string message)
        {
            List<string> guids = CollectAdapterGuids(snap);
            if (guids.Count == 0)
            {
                message = "No active Ethernet or Wi-Fi adapter GUIDs found.";
                return false;
            }

            StringBuilder ps = new StringBuilder();
            ps.AppendLine("$ErrorActionPreference = 'Stop'");
            ps.AppendLine("$mmPath = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile'");
            ps.AppendLine("$tcpBase = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces'");
            ps.AppendLine("$backupPath = '" + GetBackupFilePath().Replace("'", "''") + "'");
            ps.AppendLine("$dir = Split-Path -Parent $backupPath; if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }");

            // Save original values only if backup does not exist yet
            ps.AppendLine("if (-not (Test-Path $backupPath)) {");
            ps.AppendLine("  $mm = Get-ItemProperty -Path $mmPath -ErrorAction SilentlyContinue");
            ps.AppendLine("  $ifaces = @{}");
            foreach (string g in guids)
            {
                string safeG = g.Replace("'", "''");
                ps.AppendLine("  $p = Join-Path $tcpBase '" + safeG + "'");
                ps.AppendLine("  if (Test-Path $p) { $v = Get-ItemProperty -Path $p -ErrorAction SilentlyContinue; $ifaces['" + safeG + "'] = @{ TcpAckFrequency = $v.TcpAckFrequency; TCPNoDelay = $v.TCPNoDelay } }");
            }
            ps.AppendLine("  @{ NetworkThrottlingIndex = $mm.NetworkThrottlingIndex; SystemResponsiveness = $mm.SystemResponsiveness; Interfaces = $ifaces } | ConvertTo-Json -Depth 4 | Set-Content -Path $backupPath -Encoding UTF8");
            ps.AppendLine("}");

            // Apply low-latency values
            ps.AppendLine("Set-ItemProperty -Path $mmPath -Name 'NetworkThrottlingIndex' -Value ([uint32]4294967295) -Type DWord");
            ps.AppendLine("Set-ItemProperty -Path $mmPath -Name 'SystemResponsiveness' -Value 0 -Type DWord");
            foreach (string g in guids)
            {
                string safeG = g.Replace("'", "''");
                ps.AppendLine("$p = Join-Path $tcpBase '" + safeG + "'");
                ps.AppendLine("if (Test-Path $p) {");
                ps.AppendLine("  Set-ItemProperty -Path $p -Name 'TcpAckFrequency' -Value 1 -Type DWord");
                ps.AppendLine("  Set-ItemProperty -Path $p -Name 'TCPNoDelay' -Value 1 -Type DWord");
                ps.AppendLine("}");
            }

            return RunElevatedScript(ps.ToString(), "Applied Nagle off (TcpAckFrequency/TCPNoDelay=1) + disabled NetworkThrottlingIndex.", out message);
        }

        public static bool RevertLowLatencyTweaks(DualWanSnapshot snap, out string message)
        {
            StringBuilder ps = new StringBuilder();
            ps.AppendLine("$ErrorActionPreference = 'Stop'");
            ps.AppendLine("$mmPath = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile'");
            ps.AppendLine("$tcpBase = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces'");
            ps.AppendLine("$backupPath = '" + GetBackupFilePath().Replace("'", "''") + "'");

            ps.AppendLine("if (Test-Path $backupPath) {");
            ps.AppendLine("  $b = Get-Content -Path $backupPath -Raw | ConvertFrom-Json");
            ps.AppendLine("  if ($null -ne $b.NetworkThrottlingIndex) { Set-ItemProperty -Path $mmPath -Name 'NetworkThrottlingIndex' -Value ([uint32]$b.NetworkThrottlingIndex) -Type DWord } else { Set-ItemProperty -Path $mmPath -Name 'NetworkThrottlingIndex' -Value 10 -Type DWord }");
            ps.AppendLine("  if ($null -ne $b.SystemResponsiveness) { Set-ItemProperty -Path $mmPath -Name 'SystemResponsiveness' -Value ([int]$b.SystemResponsiveness) -Type DWord } else { Set-ItemProperty -Path $mmPath -Name 'SystemResponsiveness' -Value 20 -Type DWord }");
            ps.AppendLine("  if ($b.Interfaces) {");
            ps.AppendLine("    foreach ($prop in $b.Interfaces.PSObject.Properties) {");
            ps.AppendLine("      $p = Join-Path $tcpBase $prop.Name");
            ps.AppendLine("      if (Test-Path $p) {");
            ps.AppendLine("        if ($null -ne $prop.Value.TcpAckFrequency) { Set-ItemProperty -Path $p -Name 'TcpAckFrequency' -Value ([int]$prop.Value.TcpAckFrequency) -Type DWord } else { Remove-ItemProperty -Path $p -Name 'TcpAckFrequency' -ErrorAction SilentlyContinue }");
            ps.AppendLine("        if ($null -ne $prop.Value.TCPNoDelay) { Set-ItemProperty -Path $p -Name 'TCPNoDelay' -Value ([int]$prop.Value.TCPNoDelay) -Type DWord } else { Remove-ItemProperty -Path $p -Name 'TCPNoDelay' -ErrorAction SilentlyContinue }");
            ps.AppendLine("      }");
            ps.AppendLine("    }");
            ps.AppendLine("  }");
            ps.AppendLine("  Remove-Item -Path $backupPath -Force -ErrorAction SilentlyContinue");
            ps.AppendLine("} else {");
            ps.AppendLine("  Set-ItemProperty -Path $mmPath -Name 'NetworkThrottlingIndex' -Value 10 -Type DWord");
            ps.AppendLine("  Set-ItemProperty -Path $mmPath -Name 'SystemResponsiveness' -Value 20 -Type DWord");
            foreach (string g in CollectAdapterGuids(snap))
            {
                string safeG = g.Replace("'", "''");
                ps.AppendLine("  $p = Join-Path $tcpBase '" + safeG + "'");
                ps.AppendLine("  if (Test-Path $p) { Remove-ItemProperty -Path $p -Name 'TcpAckFrequency','TCPNoDelay' -ErrorAction SilentlyContinue }");
            }
            ps.AppendLine("}");

            return RunElevatedScript(ps.ToString(), "Reverted Windows TCP & Multimedia throttling tweaks to defaults.", out message);
        }

        private static List<string> CollectAdapterGuids(DualWanSnapshot snap)
        {
            List<string> guids = new List<string>();
            if (snap != null)
            {
                if (snap.Ethernet != null && !string.IsNullOrEmpty(snap.Ethernet.Id)) guids.Add(snap.Ethernet.Id);
                if (snap.Wifi != null && !string.IsNullOrEmpty(snap.Wifi.Id)) guids.Add(snap.Wifi.Id);
            }
            return guids;
        }

        private static bool RunElevatedScript(string scriptContent, string successMessage, out string message)
        {
            string tempPs1 = Path.Combine(Path.GetTempPath(), "dualwan_net_tweak_" + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                File.WriteAllText(tempPs1, scriptContent, Encoding.UTF8);
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = string.Format("-NoProfile -ExecutionPolicy Bypass -File \"{0}\"", tempPs1);
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                if (!NetworkDiscovery.IsAdministrator())
                {
                    psi.Verb = "runas";
                }
                Process proc = Process.Start(psi);
                if (proc == null)
                {
                    message = "Failed to start PowerShell helper.";
                    return false;
                }
                proc.WaitForExit(15000);
                if (proc.ExitCode == 0)
                {
                    message = successMessage;
                    return true;
                }
                message = "Helper exited with code " + proc.ExitCode;
                return false;
            }
            catch (Exception ex)
            {
                message = "Action cancelled or failed: " + ex.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(tempPs1)) File.Delete(tempPs1); } catch { }
            }
        }
    }
}

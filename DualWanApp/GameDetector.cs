using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DualWanBalancer
{
    public class DetectedGameInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string DisplayName { get; set; }
        public string DetectionSource { get; set; }
        public bool IsFullscreen { get; set; }
    }

    public static class GameDetector
    {
        private static readonly Dictionary<string, string> KnownGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "VALORANT-Win64-Shipping", "VALORANT" },
            { "cs2", "Counter-Strike 2" },
            { "csgo", "Counter-Strike: GO" },
            { "FortniteClient-Win64-Shipping", "Fortnite" },
            { "r5apex", "Apex Legends" },
            { "r5apex_dx12", "Apex Legends (DX12)" },
            { "League of Legends", "League of Legends" },
            { "dota2", "Dota 2" },
            { "Overwatch", "Overwatch 2" },
            { "TslGame", "PUBG: Battlegrounds" },
            { "GTA5", "Grand Theft Auto V" },
            { "GTA5_Enhanced", "GTA V Enhanced" },
            { "FiveM_GTAProcess", "FiveM" },
            { "FiveM_b2802_GTAProcess", "FiveM" },
            { "FiveM_b3095_GTAProcess", "FiveM" },
            { "cod", "Call of Duty" },
            { "ModernWarfare", "Call of Duty: MW" },
            { "RocketLeague", "Rocket League" },
            { "RainbowSix", "Rainbow Six Siege" },
            { "RainbowSix_BE", "Rainbow Six Siege" },
            { "RainbowSix_Vulkan", "Rainbow Six Siege" },
            { "RobloxPlayerBeta", "Roblox" },
            { "GenshinImpact", "Genshin Impact" },
            { "StarRail", "Honkai: Star Rail" },
            { "Wuthering Waves", "Wuthering Waves" },
            { "Client-Win64-Shipping", "Wuthering Waves" },
            { "eldenring", "Elden Ring" },
            { "RustClient", "Rust" },
            { "EscapeFromTarkov", "Escape from Tarkov" },
            { "helldivers2", "Helldivers 2" },
            { "Marvel-Win64-Shipping", "Marvel Rivals" },
            { "DeltaForceClient-Win64-Shipping", "Delta Force" },
            { "Deadlock", "Deadlock" },
            { "project8", "Deadlock" },
            { "Warframe.x64", "Warframe" },
            { "PathOfExile", "Path of Exile" },
            { "PathOfExileSteam", "Path of Exile" },
            { "PathOfExile2", "Path of Exile 2" },
            { "destiny2", "Destiny 2" },
            { "Palworld-Win64-Shipping", "Palworld" },
            { "FC24", "EA Sports FC 24" },
            { "FC25", "EA Sports FC 25" },
            { "FIFA23", "FIFA 23" },
            { "bf2042", "Battlefield 2042" },
            { "aces", "War Thunder" },
            { "WorldOfTanks", "World of Tanks" },
            { "WorldOfWarships", "World of Warships" },
            { "bgmi", "BGMI" },
            { "aow_exe", "Gameloop Emulator" },
            { "HD-Player", "BlueStacks" },
            { "Ld9BoxHeadless", "LDPlayer" },
            { "Minecraft.Windows", "Minecraft Bedrock" },
            { "NarakaBladepoint", "Naraka: Bladepoint" },
            { "DeadByDaylight-Win64-Shipping", "Dead by Daylight" },
            { "SoTGame", "Sea of Thieves" },
            { "ForzaHorizon5", "Forza Horizon 5" }
        };

        private static readonly HashSet<string> NonGameProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
            "DualWanBalancer", "powershell", "pwsh", "cmd", "WindowsTerminal", "conhost",
            "Code", "devenv", "rider64", "idea64", "Discord", "Teams", "Slack", "Zoom",
            "vlc", "mpc-hc64", "PotPlayerMini64", "mpv", "Spotify", "obs64",
            "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost", "ApplicationFrameHost",
            "LockApp", "TextInputHost", "SystemSettings", "Taskmgr", "svchost", "dwm",
            "steam", "steamwebhelper", "EpicGamesLauncher", "RiotClientServices", "Battle.net",
            "node", "python", "javaw"
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(
            IntPtr pUdpTable,
            ref int pdwSize,
            bool bOrder,
            int ulAf,
            int tableClass,
            uint reserved);

        private const int AF_INET = 2;
        private const int UDP_TABLE_OWNER_PID = 1;

        public static HashSet<int> GetActiveUdpPids()
        {
            HashSet<int> pids = new HashSet<int>();
            int bufferSize = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (bufferSize <= 0) return pids;

            IntPtr mem = Marshal.AllocHGlobal(bufferSize);
            try
            {
                uint res = GetExtendedUdpTable(mem, ref bufferSize, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
                if (res != 0) return pids;

                int numEntries = Marshal.ReadInt32(mem);
                // MIB_UDPROW_OWNER_PID = dwLocalAddr(4) + dwLocalPort(4) + dwOwningPid(4) = 12 bytes
                IntPtr rowPtr = IntPtr.Add(mem, 4);
                for (int i = 0; i < numEntries; i++)
                {
                    int pid = Marshal.ReadInt32(rowPtr, 8);
                    if (pid > 4) pids.Add(pid);
                    rowPtr = IntPtr.Add(rowPtr, 12);
                }
            }
            catch
            {
                // Ignore transient IP helper errors
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
            return pids;
        }

        public static DetectedGameInfo DetectActiveGame(List<string> customGames, bool allowFullscreenUdpHeuristic)
        {
            HashSet<string> customSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (customGames != null)
            {
                for (int i = 0; i < customGames.Count; i++)
                {
                    string item = (customGames[i] ?? "").Trim();
                    if (item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        item = item.Substring(0, item.Length - 4);
                    }
                    if (!string.IsNullOrEmpty(item)) customSet.Add(item);
                }
            }

            Process[] processes = Process.GetProcesses();
            try
            {
                // 1. Exact match on user's custom list or built-in known game list
                for (int i = 0; i < processes.Length; i++)
                {
                    Process p = processes[i];
                    string name;
                    try { name = p.ProcessName; } catch { continue; }

                    if (customSet.Contains(name))
                    {
                        return new DetectedGameInfo
                        {
                            ProcessId = p.Id,
                            ProcessName = name,
                            DisplayName = name,
                            DetectionSource = "Custom List"
                        };
                    }

                    string friendly;
                    if (KnownGames.TryGetValue(name, out friendly))
                    {
                        return new DetectedGameInfo
                        {
                            ProcessId = p.Id,
                            ProcessName = name,
                            DisplayName = friendly,
                            DetectionSource = "Known Game DB"
                        };
                    }

                    // Special case: Minecraft Java edition runs under javaw.exe
                    if (string.Equals(name, "javaw", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(p.MainWindowTitle) &&
                                p.MainWindowTitle.IndexOf("Minecraft", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return new DetectedGameInfo
                                {
                                    ProcessId = p.Id,
                                    ProcessName = name,
                                    DisplayName = "Minecraft (Java)",
                                    DetectionSource = "Known Game DB"
                                };
                            }
                        }
                        catch { }
                    }
                }

                // 2. Heuristic: Fullscreen non-browser/non-IDE foreground app with active UDP socket
                if (allowFullscreenUdpHeuristic)
                {
                    IntPtr fg = GetForegroundWindow();
                    if (fg != IntPtr.Zero)
                    {
                        uint fgPid;
                        GetWindowThreadProcessId(fg, out fgPid);
                        RECT rect;
                        if (fgPid > 4 && GetWindowRect(fg, out rect))
                        {
                            Rectangle winBounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                            Rectangle screenBounds = Screen.FromHandle(fg).Bounds;
                            bool isFullscreen = winBounds.Width >= screenBounds.Width - 2 && winBounds.Height >= screenBounds.Height - 2;

                            if (isFullscreen)
                            {
                                Process fgProc = null;
                                for (int i = 0; i < processes.Length; i++)
                                {
                                    if (processes[i].Id == (int)fgPid) { fgProc = processes[i]; break; }
                                }
                                if (fgProc != null && !NonGameProcesses.Contains(fgProc.ProcessName))
                                {
                                    HashSet<int> udpPids = GetActiveUdpPids();
                                    if (udpPids.Contains(fgProc.Id))
                                    {
                                        return new DetectedGameInfo
                                        {
                                            ProcessId = fgProc.Id,
                                            ProcessName = fgProc.ProcessName,
                                            DisplayName = fgProc.ProcessName,
                                            DetectionSource = "Fullscreen + UDP",
                                            IsFullscreen = true
                                        };
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try { processes[i].Dispose(); } catch { }
                }
            }

            return null;
        }

        public static List<string> ListCandidateRunningApps()
        {
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] procs = Process.GetProcesses();
            try
            {
                for (int i = 0; i < procs.Length; i++)
                {
                    try
                    {
                        if (procs[i].MainWindowHandle == IntPtr.Zero) continue;
                        string name = procs[i].ProcessName;
                        if (NonGameProcesses.Contains(name)) continue;
                        unique.Add(name);
                    }
                    catch { }
                }
            }
            finally
            {
                for (int i = 0; i < procs.Length; i++)
                {
                    try { procs[i].Dispose(); } catch { }
                }
            }

            List<string> sorted = new List<string>(unique);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return sorted;
        }

        public static void TryBoostProcessPriority(int pid, bool boost)
        {
            if (pid <= 4) return;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    ProcessPriorityClass target = boost ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;
                    if (p.PriorityClass != target && p.PriorityClass != ProcessPriorityClass.High)
                    {
                        p.PriorityClass = target;
                    }
                }
            }
            catch
            {
                // Ignore if process exited or protected by anti-cheat
            }
        }
    }
}

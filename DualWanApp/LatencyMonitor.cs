using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DualWanBalancer
{
    public class LinkLatencyStats
    {
        public string Label { get; set; }
        public string LocalIPv4 { get; set; }
        public double CurrentPingMs { get; set; }
        public double AvgPingMs { get; set; }
        public double JitterMs { get; set; }
        public double LossPercent { get; set; }
        public bool HasMeasurement { get; set; }
        public bool IsUnhealthy { get; set; }
        public string ProbeMethod { get; set; }
    }

    internal class RollingProbeHistory
    {
        private readonly Queue<double> _samples = new Queue<double>(); // -1 means timeout/loss
        private const int MaxSamples = 12;

        public void AddSample(double rttMs)
        {
            _samples.Enqueue(rttMs);
            while (_samples.Count > MaxSamples)
            {
                _samples.Dequeue();
            }
        }

        public LinkLatencyStats Summarize(string label, string ip, string method, int maxPingMs)
        {
            LinkLatencyStats stats = new LinkLatencyStats();
            stats.Label = label;
            stats.LocalIPv4 = ip;
            stats.ProbeMethod = method;

            if (_samples.Count == 0)
            {
                stats.HasMeasurement = false;
                return stats;
            }

            int total = 0;
            int lost = 0;
            double sum = 0;
            double lastValid = -1;
            double prevValid = -1;
            double jitterSum = 0;
            int jitterCount = 0;

            foreach (double s in _samples)
            {
                total++;
                if (s < 0)
                {
                    lost++;
                }
                else
                {
                    sum += s;
                    prevValid = lastValid;
                    lastValid = s;
                    if (prevValid >= 0)
                    {
                        jitterSum += Math.Abs(lastValid - prevValid);
                        jitterCount++;
                    }
                }
            }

            int ok = total - lost;
            stats.LossPercent = total > 0 ? (lost * 100.0) / total : 0;
            if (ok > 0)
            {
                stats.HasMeasurement = true;
                stats.CurrentPingMs = lastValid >= 0 ? lastValid : (sum / ok);
                stats.AvgPingMs = sum / ok;
                stats.JitterMs = jitterCount > 0 ? (jitterSum / jitterCount) : 0;
            }
            else
            {
                stats.HasMeasurement = true;
                stats.CurrentPingMs = 999;
                stats.AvgPingMs = 999;
                stats.JitterMs = 0;
            }

            stats.IsUnhealthy = (stats.LossPercent >= 25.0) || (stats.AvgPingMs > maxPingMs);
            return stats;
        }
    }

    public static class LatencyMonitor
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern IntPtr IcmpCreateFile();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern bool IcmpCloseHandle(IntPtr handle);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint IcmpSendEcho2Ex(
            IntPtr icmpHandle,
            IntPtr hEvent,
            IntPtr apcRoutine,
            IntPtr apcContext,
            uint sourceAddress,
            uint destinationAddress,
            byte[] requestData,
            ushort requestSize,
            IntPtr requestOptions,
            byte[] replyBuffer,
            uint replySize,
            uint timeout);

        private static readonly object HistoryLock = new object();
        private static readonly Dictionary<string, RollingProbeHistory> Histories = new Dictionary<string, RollingProbeHistory>();

        public static void Reset()
        {
            lock (HistoryLock)
            {
                Histories.Clear();
            }
        }

        private static uint IPv4ToUInt32NetworkOrder(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();
            return BitConverter.ToUInt32(bytes, 0);
        }

        /// <summary>
        /// Probes RTT strictly via sourceIp using source-bound ICMP (IcmpSendEcho2Ex),
        /// falling back to a source-bound TCP connect probe if ICMP is blocked.
        /// </summary>
        public static async Task<LinkLatencyStats> ProbeLinkAsync(string label, string sourceIpStr, string targetIpStr, int maxPingMs)
        {
            if (string.IsNullOrEmpty(sourceIpStr))
            {
                return new LinkLatencyStats { Label = label, HasMeasurement = false };
            }

            IPAddress srcIp;
            IPAddress dstIp;
            if (!IPAddress.TryParse(sourceIpStr, out srcIp) || !IPAddress.TryParse(targetIpStr, out dstIp))
            {
                return new LinkLatencyStats { Label = label, LocalIPv4 = sourceIpStr, HasMeasurement = false };
            }

            string method = "ICMP";
            double rtt = await Task.Run(() => TryIcmpPing(srcIp, dstIp, 1500)).ConfigureAwait(false);
            if (rtt < 0)
            {
                // Fallback to TCP 443 RTT in case router/ISP drops ICMP
                double tcpRtt = await TryTcpRttAsync(srcIp, dstIp, 443, 1800).ConfigureAwait(false);
                if (tcpRtt >= 0)
                {
                    rtt = tcpRtt;
                    method = "TCP:443";
                }
            }

            lock (HistoryLock)
            {
                RollingProbeHistory history;
                string key = label + "|" + sourceIpStr;
                if (!Histories.TryGetValue(key, out history))
                {
                    history = new RollingProbeHistory();
                    Histories[key] = history;
                }
                history.AddSample(rtt);
                return history.Summarize(label, sourceIpStr, method, maxPingMs);
            }
        }

        public static LinkLatencyStats GetCachedSummary(string label, string sourceIpStr, int maxPingMs)
        {
            if (string.IsNullOrEmpty(sourceIpStr))
            {
                return new LinkLatencyStats { Label = label, HasMeasurement = false };
            }

            lock (HistoryLock)
            {
                RollingProbeHistory history;
                string key = label + "|" + sourceIpStr;
                if (Histories.TryGetValue(key, out history))
                {
                    return history.Summarize(label, sourceIpStr, "Cached", maxPingMs);
                }
            }
            return new LinkLatencyStats { Label = label, LocalIPv4 = sourceIpStr, HasMeasurement = false };
        }

        private static double TryIcmpPing(IPAddress srcIp, IPAddress dstIp, uint timeoutMs)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = IcmpCreateFile();
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return -1;
                }

                uint src = IPv4ToUInt32NetworkOrder(srcIp);
                uint dst = IPv4ToUInt32NetworkOrder(dstIp);
                byte[] payload = new byte[] { 0x44, 0x57, 0x42, 0x50, 0x49, 0x4E, 0x47, 0x31 }; // "DWBPING1"
                byte[] reply = new byte[1024];

                uint count = IcmpSendEcho2Ex(
                    handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    src,
                    dst,
                    payload,
                    (ushort)payload.Length,
                    IntPtr.Zero,
                    reply,
                    (uint)reply.Length,
                    timeoutMs);

                if (count > 0)
                {
                    uint status = BitConverter.ToUInt32(reply, 4);
                    if (status == 0) // IP_SUCCESS
                    {
                        uint rtt = BitConverter.ToUInt32(reply, 8);
                        return rtt;
                    }
                }
                return -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    try { IcmpCloseHandle(handle); } catch { }
                }
            }
        }

        private static async Task<double> TryTcpRttAsync(IPAddress srcIp, IPAddress dstIp, int port, int timeoutMs)
        {
            Socket sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sock.NoDelay = true;
                sock.Bind(new IPEndPoint(srcIp, 0));

                Stopwatch sw = Stopwatch.StartNew();
                Task connectTask = Task.Factory.FromAsync(
                    sock.BeginConnect,
                    sock.EndConnect,
                    new IPEndPoint(dstIp, port),
                    null);

                Task delayTask = Task.Delay(timeoutMs);
                Task winner = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);
                if (winner != connectTask)
                {
                    return -1;
                }
                await connectTask.ConfigureAwait(false);
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds;
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (sock != null)
                {
                    try { sock.Close(); } catch { }
                }
            }
        }
    }
}

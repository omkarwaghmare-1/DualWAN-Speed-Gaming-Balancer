using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DualWanBalancer
{
    public class UplinkEndpoint
    {
        public string Label;
        public IPAddress LocalAddress;
        public int Weight;
        public int CurrentWeight;
        public int ActiveConnections;
        public long TotalConnections;
        public long BytesUp;
        public long BytesDown;
        public DateTime CooldownUntilUtc;
        public double PingMs;
        public double JitterMs;
        public double LossPercent;
        public bool IsUnhealthy;
    }

    internal class HostAffinityEntry
    {
        public string LocalIp;
        public DateTime ExpiresUtc;
    }

    public class DualWanProxyEngine : IDisposable
    {
        private readonly object _lock = new object();
        private readonly List<UplinkEndpoint> _uplinks = new List<UplinkEndpoint>();
        private readonly Dictionary<string, HostAffinityEntry> _hostAffinity = new Dictionary<string, HostAffinityEntry>(StringComparer.OrdinalIgnoreCase);
        private BalanceStrategy _strategy = BalanceStrategy.LeastBusy;
        private bool _stickyHostAffinity = true;
        private bool _latencyFailover = true;
        private int _httpPort = 8080;
        private int _socksPort = 1080;
        private TcpListener _httpListener;
        private TcpListener _socksListener;
        private CancellationTokenSource _cts;
        private bool _running;

        private static readonly HashSet<string> HopByHopHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "connection",
            "proxy-connection",
            "keep-alive",
            "proxy-authenticate",
            "proxy-authorization",
            "te",
            "trailers",
            "transfer-encoding",
            "upgrade"
        };

        public int ActiveHttpPort { get; private set; }
        public int ActiveSocksPort { get; private set; }

        public bool IsRunning
        {
            get { lock (_lock) { return _running; } }
        }

        public void Configure(List<UplinkEndpoint> uplinks, BalanceStrategy strategy, int httpPort, int socksPort)
        {
            Configure(uplinks, strategy, httpPort, socksPort, true, true);
        }

        public void Configure(List<UplinkEndpoint> uplinks, BalanceStrategy strategy, int httpPort, int socksPort, bool stickyHostAffinity, bool latencyFailover)
        {
            bool portsChanged;
            lock (_lock)
            {
                portsChanged = (_httpPort != httpPort || _socksPort != socksPort);
                _strategy = strategy;
                _stickyHostAffinity = stickyHostAffinity;
                _latencyFailover = latencyFailover;
                _httpPort = httpPort;
                _socksPort = socksPort;

                // Preserve byte counters & live health metrics if the same IP stays active
                Dictionary<string, UplinkEndpoint> existing = new Dictionary<string, UplinkEndpoint>();
                for (int i = 0; i < _uplinks.Count; i++)
                {
                    existing[_uplinks[i].LocalAddress.ToString()] = _uplinks[i];
                }

                _uplinks.Clear();
                for (int i = 0; i < uplinks.Count; i++)
                {
                    UplinkEndpoint incoming = uplinks[i];
                    UplinkEndpoint old;
                    if (existing.TryGetValue(incoming.LocalAddress.ToString(), out old))
                    {
                        old.Label = incoming.Label;
                        old.Weight = Math.Max(1, incoming.Weight);
                        old.CooldownUntilUtc = DateTime.MinValue;
                        _uplinks.Add(old);
                    }
                    else
                    {
                        incoming.Weight = Math.Max(1, incoming.Weight);
                        _uplinks.Add(incoming);
                    }
                }
                if (!_stickyHostAffinity)
                {
                    _hostAffinity.Clear();
                }
            }

            if (portsChanged && IsRunning)
            {
                Stop();
            }
            if (!IsRunning && uplinks.Count > 0)
            {
                Start();
            }
        }

        public void UpdateUplinkHealth(string label, double pingMs, double jitterMs, double lossPercent, bool isUnhealthy)
        {
            lock (_lock)
            {
                for (int i = 0; i < _uplinks.Count; i++)
                {
                    if (string.Equals(_uplinks[i].Label, label, StringComparison.OrdinalIgnoreCase))
                    {
                        _uplinks[i].PingMs = pingMs;
                        _uplinks[i].JitterMs = jitterMs;
                        _uplinks[i].LossPercent = lossPercent;
                        _uplinks[i].IsUnhealthy = isUnhealthy;
                    }
                }
            }
        }

        public List<UplinkEndpoint> GetUplinkStats()
        {
            lock (_lock)
            {
                List<UplinkEndpoint> copy = new List<UplinkEndpoint>(_uplinks.Count);
                for (int i = 0; i < _uplinks.Count; i++)
                {
                    UplinkEndpoint u = _uplinks[i];
                    UplinkEndpoint c = new UplinkEndpoint();
                    c.Label = u.Label;
                    c.LocalAddress = u.LocalAddress;
                    c.Weight = u.Weight;
                    c.ActiveConnections = u.ActiveConnections;
                    c.TotalConnections = u.TotalConnections;
                    c.BytesUp = Interlocked.Read(ref u.BytesUp);
                    c.BytesDown = Interlocked.Read(ref u.BytesDown);
                    copy.Add(c);
                }
                return copy;
            }
        }

        private static TcpListener StartListenerWithFallback(int preferredPort, out int boundPort)
        {
            int[] candidates = new int[12];
            for (int i = 0; i < 11; i++) candidates[i] = preferredPort + i;
            candidates[11] = 0;

            for (int i = 0; i < candidates.Length; i++)
            {
                int port = candidates[i];
                if (port < 0 || port > 65535) continue;
                TcpListener l = null;
                try
                {
                    l = new TcpListener(IPAddress.Loopback, port);
                    l.Start();
                    boundPort = ((IPEndPoint)l.LocalEndpoint).Port;
                    return l;
                }
                catch (SocketException)
                {
                    if (l != null) { try { l.Stop(); } catch { } }
                }
            }
            throw new InvalidOperationException("Unable to bind a local loopback proxy port.");
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _cts = new CancellationTokenSource();
                _running = true;

                try
                {
                    if (_httpPort > 0)
                    {
                        int boundHttp;
                        _httpListener = StartListenerWithFallback(_httpPort, out boundHttp);
                        ActiveHttpPort = boundHttp;
                        Task unusedHttp = Task.Run(() => AcceptLoopAsync(_httpListener, true, _cts.Token));
                    }
                    else
                    {
                        ActiveHttpPort = 0;
                    }

                    if (_socksPort > 0)
                    {
                        int boundSocks;
                        _socksListener = StartListenerWithFallback(_socksPort, out boundSocks);
                        ActiveSocksPort = boundSocks;
                        Task unusedSocks = Task.Run(() => AcceptLoopAsync(_socksListener, false, _cts.Token));
                    }
                    else
                    {
                        ActiveSocksPort = 0;
                    }
                }
                catch
                {
                    Stop();
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                try { if (_cts != null) _cts.Cancel(); } catch { }
                try { if (_httpListener != null) _httpListener.Stop(); } catch { }
                try { if (_socksListener != null) _socksListener.Stop(); } catch { }
                _httpListener = null;
                _socksListener = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private async Task AcceptLoopAsync(TcpListener listener, bool isHttp, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    client.NoDelay = true;
                    TcpClient captured = client;
                    if (isHttp)
                    {
                        Task unusedHttp = Task.Run(() => HandleHttpClientAsync(captured, token));
                    }
                    else
                    {
                        Task unusedSocks = Task.Run(() => HandleSocksClientAsync(captured, token));
                    }
                }
                catch
                {
                    if (client != null)
                    {
                        try { client.Close(); } catch { }
                    }
                    if (token.IsCancellationRequested) break;
                }
            }
        }

        public List<UplinkEndpoint> GetOrderedCandidates(string targetHost = null)
        {
            lock (_lock)
            {
                if (_uplinks.Count == 0) return new List<UplinkEndpoint>();
                if (_uplinks.Count == 1) return new List<UplinkEndpoint> { _uplinks[0] };

                DateTime now = DateTime.UtcNow;
                List<UplinkEndpoint> pool = new List<UplinkEndpoint>();
                for (int i = 0; i < _uplinks.Count; i++)
                {
                    UplinkEndpoint cand = _uplinks[i];
                    if (cand.CooldownUntilUtc <= now && (!_latencyFailover || !cand.IsUnhealthy))
                    {
                        pool.Add(cand);
                    }
                }
                if (pool.Count == 0)
                {
                    for (int i = 0; i < _uplinks.Count; i++)
                    {
                        if (_uplinks[i].CooldownUntilUtc <= now) pool.Add(_uplinks[i]);
                    }
                }
                if (pool.Count == 0)
                {
                    pool.AddRange(_uplinks);
                }

                UplinkEndpoint primary = null;
                if (_stickyHostAffinity && !string.IsNullOrEmpty(targetHost))
                {
                    HostAffinityEntry entry;
                    if (_hostAffinity.TryGetValue(targetHost, out entry) && entry.ExpiresUtc > now)
                    {
                        for (int i = 0; i < pool.Count; i++)
                        {
                            if (pool[i].LocalAddress.ToString() == entry.LocalIp)
                            {
                                primary = pool[i];
                                break;
                            }
                        }
                    }
                }

                if (primary == null)
                {
                    primary = pool[0];
                    if (_strategy == BalanceStrategy.LowestPing)
                    {
                        double bestPingScore = double.MaxValue;
                        for (int i = 0; i < pool.Count; i++)
                        {
                            UplinkEndpoint u = pool[i];
                            double basePing = u.PingMs > 0 ? u.PingMs : 25.0;
                            double score = basePing + (u.JitterMs * 2.0) + (u.LossPercent * 10.0) + (u.ActiveConnections * 0.5);
                            if (score < bestPingScore)
                            {
                                bestPingScore = score;
                                primary = u;
                            }
                        }
                    }
                    else if (_strategy == BalanceStrategy.LeastBusy)
                    {
                        double bestScore = double.MaxValue;
                        for (int i = 0; i < pool.Count; i++)
                        {
                            UplinkEndpoint u = pool[i];
                            double score = (u.ActiveConnections + 1.0) / Math.Max(1, u.Weight);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                primary = u;
                            }
                        }
                    }
                    else
                    {
                        int totalWeight = 0;
                        for (int i = 0; i < pool.Count; i++)
                        {
                            UplinkEndpoint u = pool[i];
                            u.CurrentWeight += u.Weight;
                            totalWeight += u.Weight;
                            if (u.CurrentWeight > primary.CurrentWeight)
                            {
                                primary = u;
                            }
                        }
                        primary.CurrentWeight -= totalWeight;
                    }

                    if (_stickyHostAffinity && !string.IsNullOrEmpty(targetHost))
                    {
                        if (_hostAffinity.Count >= 2048)
                        {
                            _hostAffinity.Clear();
                        }
                        _hostAffinity[targetHost] = new HostAffinityEntry
                        {
                            LocalIp = primary.LocalAddress.ToString(),
                            ExpiresUtc = now.AddMinutes(5)
                        };
                    }
                }

                List<UplinkEndpoint> ordered = new List<UplinkEndpoint>(_uplinks.Count);
                ordered.Add(primary);
                for (int i = 0; i < _uplinks.Count; i++)
                {
                    if (!ReferenceEquals(_uplinks[i], primary))
                    {
                        ordered.Add(_uplinks[i]);
                    }
                }
                return ordered;
            }
        }

        private bool IsSelfLoop(string host, int port)
        {
            string h = (host ?? "").Trim().ToLowerInvariant();
            if (h == "127.0.0.1" || h == "localhost" || h == "::1")
            {
                return port == _httpPort || port == _socksPort || (ActiveHttpPort > 0 && port == ActiveHttpPort) || (ActiveSocksPort > 0 && port == ActiveSocksPort);
            }
            return false;
        }

        private async Task<Tuple<Socket, UplinkEndpoint>> ConnectWithFailoverAsync(string host, int port, CancellationToken token)
        {
            if (IsSelfLoop(host, port))
            {
                throw new InvalidOperationException("Self-proxy loop blocked.");
            }

            IPAddress targetIp = null;
            IPAddress parsed;
            if (IPAddress.TryParse(host, out parsed))
            {
                if (parsed.AddressFamily != AddressFamily.InterNetwork)
                {
                    throw new NotSupportedException("Only IPv4 targets are supported.");
                }
                targetIp = parsed;
            }
            else
            {
                IPAddress[] resolved = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                for (int i = 0; i < resolved.Length; i++)
                {
                    if (resolved[i].AddressFamily == AddressFamily.InterNetwork)
                    {
                        targetIp = resolved[i];
                        break;
                    }
                }
                if (targetIp == null)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }
            }

            List<UplinkEndpoint> candidates = GetOrderedCandidates(host);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException("No active network uplinks configured.");
            }

            Exception lastEx = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                UplinkEndpoint uplink = candidates[i];
                Socket sock = null;
                try
                {
                    sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    sock.NoDelay = true;
                    sock.Bind(new IPEndPoint(uplink.LocalAddress, 0));

                    Task connectTask = Task.Factory.FromAsync(
                        sock.BeginConnect,
                        sock.EndConnect,
                        new IPEndPoint(targetIp, port),
                        null);

                    Task timeoutTask = Task.Delay(7000, token);
                    Task completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
                    if (completed != connectTask)
                    {
                        try { sock.Close(); } catch { }
                        throw new TimeoutException("Connect timed out on " + uplink.Label);
                    }
                    await connectTask.ConfigureAwait(false);

                    lock (_lock)
                    {
                        uplink.CooldownUntilUtc = DateTime.MinValue;
                        uplink.ActiveConnections++;
                        uplink.TotalConnections++;
                    }
                    return Tuple.Create(sock, uplink);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (sock != null)
                    {
                        try { sock.Close(); } catch { }
                    }
                    lock (_lock)
                    {
                        uplink.CooldownUntilUtc = DateTime.UtcNow.AddSeconds(5);
                    }
                }
            }

            throw lastEx ?? new SocketException((int)SocketError.NetworkUnreachable);
        }

        private void ReleaseConnection(UplinkEndpoint uplink)
        {
            if (uplink == null) return;
            lock (_lock)
            {
                if (uplink.ActiveConnections > 0) uplink.ActiveConnections--;
            }
        }

        private static int FindHeaderEnd(byte[] data, int len)
        {
            for (int i = 0; i <= len - 4; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                {
                    return i + 4;
                }
            }
            return -1;
        }

        private static bool TryParseAuthority(string authority, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;
            if (string.IsNullOrWhiteSpace(authority)) return false;

            int colon = authority.LastIndexOf(':');
            if (colon <= 0)
            {
                host = authority.Trim();
                return host.Length > 0;
            }
            host = authority.Substring(0, colon).Trim();
            string portStr = authority.Substring(colon + 1).Trim();
            return host.Length > 0 && int.TryParse(portStr, out port) && port > 0 && port <= 65535;
        }

        private async Task HandleHttpClientAsync(TcpClient client, CancellationToken token)
        {
            UplinkEndpoint activeUplink = null;
            Socket remoteSock = null;
            bool tunnelEstablished = false;

            try
            {
                NetworkStream clientStream = client.GetStream();
                byte[] headerBuf = new byte[16384];
                int totalRead = 0;
                int headerEnd = -1;

                while (totalRead < headerBuf.Length)
                {
                    int n = await clientStream.ReadAsync(headerBuf, totalRead, headerBuf.Length - totalRead, token).ConfigureAwait(false);
                    if (n <= 0) return;
                    totalRead += n;
                    headerEnd = FindHeaderEnd(headerBuf, totalRead);
                    if (headerEnd != -1) break;
                }

                if (headerEnd == -1)
                {
                    await WriteAsciiAsync(clientStream, "HTTP/1.1 400 Bad Request\r\n\r\n", token).ConfigureAwait(false);
                    return;
                }

                string headerText = Encoding.ASCII.GetString(headerBuf, 0, headerEnd);
                string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;

                string[] reqParts = lines[0].Split(' ');
                if (reqParts.Length < 3)
                {
                    await WriteAsciiAsync(clientStream, "HTTP/1.1 400 Bad Request\r\n\r\n", token).ConfigureAwait(false);
                    return;
                }

                string method = reqParts[0].ToUpperInvariant();
                string rawTarget = reqParts[1];
                string version = reqParts[2];

                int bodyOverflowLen = totalRead - headerEnd;
                byte[] overflow = null;
                if (bodyOverflowLen > 0)
                {
                    overflow = new byte[bodyOverflowLen];
                    Buffer.BlockCopy(headerBuf, headerEnd, overflow, 0, bodyOverflowLen);
                }

                if (method == "CONNECT")
                {
                    string host;
                    int port;
                    if (!TryParseAuthority(rawTarget, 443, out host, out port))
                    {
                        await WriteAsciiAsync(clientStream, "HTTP/1.1 400 Bad Request\r\n\r\n", token).ConfigureAwait(false);
                        return;
                    }

                    Tuple<Socket, UplinkEndpoint> conn = await ConnectWithFailoverAsync(host, port, token).ConfigureAwait(false);
                    remoteSock = conn.Item1;
                    activeUplink = conn.Item2;

                    await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", token).ConfigureAwait(false);
                    tunnelEstablished = true;

                    using (NetworkStream remoteStream = new NetworkStream(remoteSock, false))
                    {
                        if (overflow != null && overflow.Length > 0)
                        {
                            await remoteStream.WriteAsync(overflow, 0, overflow.Length, token).ConfigureAwait(false);
                            Interlocked.Add(ref activeUplink.BytesUp, overflow.Length);
                        }
                        await PumpBidirectionalAsync(clientStream, remoteStream, activeUplink, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    Uri uri;
                    if (!Uri.TryCreate(rawTarget, UriKind.Absolute, out uri))
                    {
                        string hostHeader = null;
                        for (int i = 1; i < lines.Length; i++)
                        {
                            if (lines[i].StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                            {
                                hostHeader = lines[i].Substring(5).Trim();
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(hostHeader) || !Uri.TryCreate("http://" + hostHeader + rawTarget, UriKind.Absolute, out uri))
                        {
                            await WriteAsciiAsync(clientStream, "HTTP/1.1 400 Bad Request\r\n\r\n", token).ConfigureAwait(false);
                            return;
                        }
                    }

                    if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsciiAsync(clientStream, "HTTP/1.1 400 Bad Request\r\n\r\n", token).ConfigureAwait(false);
                        return;
                    }

                    string host = uri.Host;
                    int port = uri.Port > 0 ? uri.Port : 80;

                    Tuple<Socket, UplinkEndpoint> conn = await ConnectWithFailoverAsync(host, port, token).ConfigureAwait(false);
                    remoteSock = conn.Item1;
                    activeUplink = conn.Item2;

                    StringBuilder rebuilt = new StringBuilder();
                    rebuilt.AppendFormat("{0} {1} {2}\r\n", method, uri.PathAndQuery, version);
                    bool sawHost = false;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (string.IsNullOrEmpty(line)) continue;
                        int colon = line.IndexOf(':');
                        if (colon <= 0) continue;
                        string key = line.Substring(0, colon).Trim();
                        if (HopByHopHeaders.Contains(key)) continue;
                        if (string.Equals(key, "Host", StringComparison.OrdinalIgnoreCase)) sawHost = true;
                        rebuilt.Append(line).Append("\r\n");
                    }
                    if (!sawHost)
                    {
                        rebuilt.Append("Host: ").Append(uri.Authority).Append("\r\n");
                    }
                    rebuilt.Append("Connection: close\r\n\r\n");

                    byte[] reqBytes = Encoding.ASCII.GetBytes(rebuilt.ToString());
                    using (NetworkStream remoteStream = new NetworkStream(remoteSock, false))
                    {
                        await remoteStream.WriteAsync(reqBytes, 0, reqBytes.Length, token).ConfigureAwait(false);
                        Interlocked.Add(ref activeUplink.BytesUp, reqBytes.Length);
                        tunnelEstablished = true;

                        if (overflow != null && overflow.Length > 0)
                        {
                            await remoteStream.WriteAsync(overflow, 0, overflow.Length, token).ConfigureAwait(false);
                            Interlocked.Add(ref activeUplink.BytesUp, overflow.Length);
                        }

                        await PumpBidirectionalAsync(clientStream, remoteStream, activeUplink, token).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                if (!tunnelEstablished)
                {
                    try
                    {
                        if (client.Connected)
                        {
                            byte[] err = Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\n\r\n");
                            client.GetStream().Write(err, 0, err.Length);
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                ReleaseConnection(activeUplink);
                if (remoteSock != null) { try { remoteSock.Close(); } catch { } }
                try { client.Close(); } catch { }
            }
        }

        private async Task HandleSocksClientAsync(TcpClient client, CancellationToken token)
        {
            UplinkEndpoint activeUplink = null;
            Socket remoteSock = null;
            bool tunnelEstablished = false;

            try
            {
                NetworkStream stream = client.GetStream();

                // 1. Greeting: VER(1) + NMETHODS(1) + METHODS(NMETHODS)
                byte[] head = await ReadExactAsync(stream, 2, token).ConfigureAwait(false);
                if (head == null || head[0] != 0x05) return;

                byte[] methods = await ReadExactAsync(stream, head[1], token).ConfigureAwait(false);
                if (methods == null) return;

                bool supportsNoAuth = false;
                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i] == 0x00) { supportsNoAuth = true; break; }
                }
                if (!supportsNoAuth)
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0xFF }, 0, 2, token).ConfigureAwait(false);
                    return;
                }
                await stream.WriteAsync(new byte[] { 0x05, 0x00 }, 0, 2, token).ConfigureAwait(false);

                // 2. Request: VER(1) + CMD(1) + RSV(1) + ATYP(1)
                byte[] reqHead = await ReadExactAsync(stream, 4, token).ConfigureAwait(false);
                if (reqHead == null || reqHead[0] != 0x05) return;

                byte cmd = reqHead[1];
                byte atyp = reqHead[3];
                string host = null;

                if (atyp == 0x01)
                {
                    byte[] addr = await ReadExactAsync(stream, 4, token).ConfigureAwait(false);
                    if (addr == null) return;
                    host = string.Format("{0}.{1}.{2}.{3}", addr[0], addr[1], addr[2], addr[3]);
                }
                else if (atyp == 0x03)
                {
                    byte[] lenBuf = await ReadExactAsync(stream, 1, token).ConfigureAwait(false);
                    if (lenBuf == null) return;
                    byte[] domBuf = await ReadExactAsync(stream, lenBuf[0], token).ConfigureAwait(false);
                    if (domBuf == null) return;
                    host = Encoding.UTF8.GetString(domBuf);
                }
                else
                {
                    await SendSocksReplyAsync(stream, 0x08, token).ConfigureAwait(false);
                    return;
                }

                byte[] portBuf = await ReadExactAsync(stream, 2, token).ConfigureAwait(false);
                if (portBuf == null) return;
                int port = (portBuf[0] << 8) | portBuf[1];

                if (cmd != 0x01)
                {
                    await SendSocksReplyAsync(stream, 0x07, token).ConfigureAwait(false);
                    return;
                }

                Tuple<Socket, UplinkEndpoint> conn = await ConnectWithFailoverAsync(host, port, token).ConfigureAwait(false);
                remoteSock = conn.Item1;
                activeUplink = conn.Item2;

                await SendSocksReplyAsync(stream, 0x00, token).ConfigureAwait(false);
                tunnelEstablished = true;
                using (NetworkStream remoteStream = new NetworkStream(remoteSock, false))
                {
                    await PumpBidirectionalAsync(stream, remoteStream, activeUplink, token).ConfigureAwait(false);
                }
            }
            catch
            {
                if (!tunnelEstablished)
                {
                    try
                    {
                        if (client.Connected)
                        {
                            byte[] err = new byte[] { 0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                            client.GetStream().Write(err, 0, err.Length);
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                ReleaseConnection(activeUplink);
                if (remoteSock != null) { try { remoteSock.Close(); } catch { } }
                try { client.Close(); } catch { }
            }
        }

        private static async Task SendSocksReplyAsync(NetworkStream stream, byte rep, CancellationToken token)
        {
            byte[] reply = new byte[] { 0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            await stream.WriteAsync(reply, 0, reply.Length, token).ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken token)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = await stream.ReadAsync(buf, offset, count - offset, token).ConfigureAwait(false);
                if (n <= 0) return null;
                offset += n;
            }
            return buf;
        }

        private static Task WriteAsciiAsync(NetworkStream stream, string text, CancellationToken token)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            return stream.WriteAsync(bytes, 0, bytes.Length, token);
        }

        private static async Task PumpBidirectionalAsync(NetworkStream clientStream, NetworkStream remoteStream, UplinkEndpoint uplink, CancellationToken token)
        {
            Task upTask = CopyTrackAsync(clientStream, remoteStream, true, uplink, token);
            Task downTask = CopyTrackAsync(remoteStream, clientStream, false, uplink, token);
            await Task.WhenAny(upTask, downTask).ConfigureAwait(false);
            try { clientStream.Close(); } catch { }
            try { remoteStream.Close(); } catch { }
            try { await Task.WhenAll(upTask, downTask).ConfigureAwait(false); } catch { }
        }

        private static async Task CopyTrackAsync(Stream src, Stream dst, bool isUpload, UplinkEndpoint uplink, CancellationToken token)
        {
            byte[] buf = new byte[32768];
            while (!token.IsCancellationRequested)
            {
                int read = await src.ReadAsync(buf, 0, buf.Length, token).ConfigureAwait(false);
                if (read <= 0) break;
                await dst.WriteAsync(buf, 0, read, token).ConfigureAwait(false);
                if (isUpload)
                {
                    Interlocked.Add(ref uplink.BytesUp, read);
                }
                else
                {
                    Interlocked.Add(ref uplink.BytesDown, read);
                }
            }
        }
    }
}

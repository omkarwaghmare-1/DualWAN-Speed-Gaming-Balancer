'use strict';

/**
 * Dual-WAN Local Load-Balancing Proxy (HTTP/HTTPS CONNECT + SOCKS5)
 *
 * Author: Omkar Waghmare
 * License: MIT
 *
 * Zero-dependency Node.js proxy that distributes outbound TCP connections
 * across multiple local network interfaces (e.g., Ethernet + Wi-Fi) by
 * explicitly binding each outbound socket's `localAddress`.
 *
 * Note: Under Windows 10/11 Strong Host Send, binding `localAddress` forces
 * the OS to route that TCP connection through the interface owning that IP.
 * Each individual TCP connection still uses one interface; parallel connections
 * (browsers, IDM, aria2, multi-part uploads/downloads) share both links.
 */

const net = require('net');
const http = require('http');
const os = require('os');

const HOP_BY_HOP_HEADERS = new Set([
  'connection',
  'proxy-connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'te',
  'trailers',
  'transfer-encoding',
  'upgrade'
]);

function parseArgs(argv) {
  const opts = {
    bind: '127.0.0.1',
    httpPort: 8080,
    socksPort: 1080,
    strategy: 'round-robin', // 'round-robin' | 'least-busy'
    statsIntervalMs: 3000,
    connectTimeoutMs: 8000,
    includeVirtual: false,
    uplinksSpec: null
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    const next = () => {
      if (i + 1 >= argv.length) {
        throw new Error(`Missing value for ${arg}`);
      }
      i += 1;
      return argv[i];
    };

    switch (arg) {
      case '--bind':
        opts.bind = next();
        break;
      case '--http-port':
        opts.httpPort = parsePort(next(), '--http-port');
        break;
      case '--socks-port':
        opts.socksPort = parsePort(next(), '--socks-port');
        break;
      case '--strategy':
        opts.strategy = next().toLowerCase();
        if (opts.strategy !== 'round-robin' && opts.strategy !== 'least-busy') {
          throw new Error(`Invalid --strategy "${opts.strategy}". Use round-robin or least-busy.`);
        }
        break;
      case '--uplinks':
        opts.uplinksSpec = next();
        break;
      case '--stats-interval':
        opts.statsIntervalMs = Math.max(0, Number(next()));
        if (Number.isNaN(opts.statsIntervalMs)) {
          throw new Error('Invalid --stats-interval value.');
        }
        break;
      case '--connect-timeout':
        opts.connectTimeoutMs = Math.max(500, Number(next()));
        if (Number.isNaN(opts.connectTimeoutMs)) {
          throw new Error('Invalid --connect-timeout value.');
        }
        break;
      case '--include-virtual':
        opts.includeVirtual = true;
        break;
      case '--help':
      case '-h':
        printHelp();
        process.exit(0);
        break;
      default:
        throw new Error(`Unknown argument: ${arg}`);
    }
  }

  return opts;
}

function parsePort(value, flagName) {
  const port = Number(value);
  if (!Number.isInteger(port) || port < 0 || port > 65535) {
    throw new Error(`Invalid port for ${flagName}: ${value}`);
  }
  return port;
}

function printHelp() {
  console.log(`
Dual-WAN Load-Balancing Proxy (HTTP/HTTPS CONNECT + SOCKS5)

Usage:
  node dual-wan-proxy.js [options]

Options:
  --uplinks <spec>         Comma-separated uplinks: Name=IP[:Weight],...
                           Example: Ethernet=192.168.0.10:1,Wi-Fi=192.168.1.20:2
                           (Auto-detects non-virtual IPv4 interfaces if omitted)
  --bind <ip>              Local listen IP (default: 127.0.0.1)
  --http-port <port>       HTTP/HTTPS CONNECT proxy port, 0 to disable (default: 8080)
  --socks-port <port>      SOCKS5 proxy port, 0 to disable (default: 1080)
  --strategy <mode>        round-robin (default) or least-busy
  --stats-interval <ms>    Throughput log interval in ms, 0 to disable (default: 3000)
  --connect-timeout <ms>   Per-uplink connect timeout before failover (default: 8000)
  --include-virtual        Include tunnel/virtual interfaces during auto-discovery
  --help, -h               Show this help
`.trim());
}

function isLikelyVirtualInterface(name) {
  return /(loopback|bluetooth|virtual|vethernet|hyper-v|vmware|vbox|tunnel|tap|tun|wg|zerotier|tailscale|et_\d+)/i.test(name);
}

function parseUplinks(opts) {
  if (opts.uplinksSpec) {
    return opts.uplinksSpec
      .split(',')
      .map((entry) => entry.trim())
      .filter(Boolean)
      .map((entry, idx) => {
        // Format: Label=IP[:Weight] or IP[:Weight]
        let label = `Uplink${idx + 1}`;
        let rest = entry;
        const eqIdx = entry.indexOf('=');
        if (eqIdx !== -1) {
          label = entry.slice(0, eqIdx).trim() || label;
          rest = entry.slice(eqIdx + 1).trim();
        }
        const parts = rest.split(':');
        const ip = parts[0].trim();
        const weight = parts.length > 1 ? Math.max(1, Math.min(20, parseInt(parts[1], 10) || 1)) : 1;
        if (!net.isIPv4(ip)) {
          throw new Error(`Invalid IPv4 uplink address "${ip}" in "${entry}".`);
        }
        return createUplinkState(label, ip, weight);
      });
  }

  const ifaces = os.networkInterfaces();
  const discovered = [];
  for (const [name, addrs] of Object.entries(ifaces)) {
    if (!addrs) continue;
    if (!opts.includeVirtual && isLikelyVirtualInterface(name)) continue;
    for (const addr of addrs) {
      if (addr.family === 'IPv4' && !addr.internal && !addr.address.startsWith('169.254.')) {
        discovered.push(createUplinkState(name, addr.address, 1));
      }
    }
  }
  return discovered;
}

function createUplinkState(label, ip, weight) {
  return {
    label,
    ip,
    weight,
    currentWeight: 0,
    activeConnections: 0,
    totalConnections: 0,
    failures: 0,
    cooldownUntil: 0,
    bytesUp: 0,
    bytesDown: 0,
    prevBytesUp: 0,
    prevBytesDown: 0
  };
}

class UplinkScheduler {
  constructor(uplinks, strategy) {
    this.uplinks = uplinks;
    this.strategy = strategy;
  }

  /**
   * Returns uplinks ordered by preference (primary choice first, then backups
   * for automatic failover if the primary connection attempt fails).
   */
  orderedCandidates() {
    const now = Date.now();
    const healthy = this.uplinks.filter((u) => u.cooldownUntil <= now);
    const pool = healthy.length > 0 ? healthy : this.uplinks;

    let primary;
    if (this.strategy === 'least-busy') {
      primary = pool.reduce((best, candidate) => {
        const bestScore = (best.activeConnections + 1) / best.weight;
        const candScore = (candidate.activeConnections + 1) / candidate.weight;
        return candScore < bestScore ? candidate : best;
      });
    } else {
      // Smooth weighted round-robin (Nginx-style) for even distribution
      let totalWeight = 0;
      primary = pool[0];
      for (const u of pool) {
        u.currentWeight += u.weight;
        totalWeight += u.weight;
        if (u.currentWeight > primary.currentWeight) {
          primary = u;
        }
      }
      primary.currentWeight -= totalWeight;
    }

    const backups = this.uplinks.filter((u) => u !== primary);
    return [primary, ...backups];
  }
}

function isSelfProxyTarget(host, port, opts) {
  const normalized = (host || '').toLowerCase();
  const isLocalHost = normalized === '127.0.0.1' || normalized === 'localhost' || normalized === '::1';
  return isLocalHost && (port === opts.httpPort || port === opts.socksPort);
}

function connectSingleUplink(host, port, uplink, timeoutMs) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const isLoopbackUplink = uplink.ip.startsWith('127.');
    const connectOpts = {
      host,
      port,
      family: 4,
      localAddress: uplink.ip,
      noDelay: true
    };
    // Node allows explicit IPv4 resolution even for localhost when family: 4 is set.
    if (isLoopbackUplink && host === 'localhost') {
      connectOpts.host = '127.0.0.1';
    }

    const socket = net.connect(connectOpts);
    socket.setTimeout(timeoutMs);

    const cleanup = () => {
      socket.removeListener('connect', onConnect);
      socket.removeListener('error', onError);
      socket.removeListener('timeout', onTimeout);
    };

    const onConnect = () => {
      if (settled) return;
      settled = true;
      socket.setTimeout(0);
      cleanup();
      resolve(socket);
    };

    const onError = (err) => {
      if (settled) return;
      settled = true;
      cleanup();
      socket.destroy();
      reject(err);
    };

    const onTimeout = () => {
      if (settled) return;
      settled = true;
      cleanup();
      socket.destroy();
      const err = new Error(`Connection timed out via ${uplink.label} (${uplink.ip})`);
      err.code = 'ETIMEDOUT';
      reject(err);
    };

    socket.once('connect', onConnect);
    socket.once('error', onError);
    socket.once('timeout', onTimeout);
  });
}

async function connectWithFailover(host, port, scheduler, opts) {
  if (isSelfProxyTarget(host, port, opts)) {
    const err = new Error('Refused connection back to local proxy port');
    err.code = 'ELOOP';
    throw err;
  }

  if (net.isIPv6(host)) {
    const err = new Error('IPv6 literal targets are unsupported when binding IPv4 uplinks');
    err.code = 'EAFNOSUPPORT';
    throw err;
  }

  const candidates = scheduler.orderedCandidates();
  let lastError = null;

  for (const uplink of candidates) {
    try {
      const socket = await connectSingleUplink(host, port, uplink, opts.connectTimeoutMs);
      uplink.cooldownUntil = 0;
      uplink.activeConnections += 1;
      uplink.totalConnections += 1;

      let released = false;
      const release = () => {
        if (!released) {
          released = true;
          uplink.activeConnections = Math.max(0, uplink.activeConnections - 1);
        }
      };
      socket.once('close', release);
      return { socket, uplink };
    } catch (err) {
      lastError = err;
      uplink.failures += 1;
      // Short 5s cooldown if a specific interface fails to connect
      uplink.cooldownUntil = Date.now() + 5000;
    }
  }

  throw lastError || new Error('All uplinks failed to connect');
}

function bridgeSockets(clientSocket, remoteSocket, uplink, initialRemoteBytes) {
  clientSocket.setNoDelay(true);
  remoteSocket.setNoDelay(true);

  if (initialRemoteBytes && initialRemoteBytes.length > 0) {
    uplink.bytesUp += initialRemoteBytes.length;
    remoteSocket.write(initialRemoteBytes);
  }

  clientSocket.on('data', (chunk) => {
    uplink.bytesUp += chunk.length;
  });
  remoteSocket.on('data', (chunk) => {
    uplink.bytesDown += chunk.length;
  });

  clientSocket.pipe(remoteSocket);
  remoteSocket.pipe(clientSocket);

  const teardown = () => {
    if (!clientSocket.destroyed) clientSocket.destroy();
    if (!remoteSocket.destroyed) remoteSocket.destroy();
  };

  clientSocket.on('error', teardown);
  remoteSocket.on('error', teardown);
  clientSocket.on('close', () => {
    if (!remoteSocket.destroyed) remoteSocket.end();
  });
  remoteSocket.on('close', () => {
    if (!clientSocket.destroyed) clientSocket.end();
  });
}

function parseAuthority(authority, defaultPort) {
  if (!authority) return null;
  if (authority.startsWith('[')) {
    const closeIdx = authority.indexOf(']');
    if (closeIdx === -1) return null;
    const host = authority.slice(1, closeIdx);
    const portStr = authority.slice(closeIdx + 1);
    const port = portStr.startsWith(':') ? parseInt(portStr.slice(1), 10) : defaultPort;
    return { host, port };
  }
  const colonIdx = authority.lastIndexOf(':');
  if (colonIdx === -1) {
    return { host: authority, port: defaultPort };
  }
  const host = authority.slice(0, colonIdx);
  const port = parseInt(authority.slice(colonIdx + 1), 10);
  if (!host || !Number.isInteger(port) || port <= 0 || port > 65535) return null;
  return { host, port };
}

function sanitizeHeaders(rawHeaders, targetHostHeader) {
  const headers = {};
  for (const [key, value] of Object.entries(rawHeaders)) {
    const lower = key.toLowerCase();
    if (HOP_BY_HOP_HEADERS.has(lower)) continue;
    headers[key] = value;
  }
  if (!headers.host && !headers.Host && targetHostHeader) {
    headers.Host = targetHostHeader;
  }
  return headers;
}

function startHttpProxy(scheduler, opts) {
  const server = http.createServer((clientReq, clientRes) => {
    let targetUrl;
    try {
      if (/^https?:\/\//i.test(clientReq.url)) {
        targetUrl = new URL(clientReq.url);
      } else {
        const hostHeader = clientReq.headers.host;
        if (!hostHeader) {
          clientRes.writeHead(400, { 'Content-Type': 'text/plain' });
          clientRes.end('Bad Request: Missing Host header');
          return;
        }
        targetUrl = new URL(`http://${hostHeader}${clientReq.url}`);
      }
    } catch {
      clientRes.writeHead(400, { 'Content-Type': 'text/plain' });
      clientRes.end('Bad Request: Invalid URL');
      return;
    }

    if (targetUrl.protocol !== 'http:') {
      clientRes.writeHead(400, { 'Content-Type': 'text/plain' });
      clientRes.end('Unsupported protocol; use CONNECT for HTTPS');
      return;
    }

    const hostname = targetUrl.hostname;
    const port = Number(targetUrl.port || 80);
    const path = `${targetUrl.pathname}${targetUrl.search}`;

    // Pause incoming upload stream while resolving/connecting an uplink socket
    clientReq.pause();
    let clientAborted = false;
    clientReq.on('aborted', () => {
      clientAborted = true;
    });
    clientReq.on('error', () => {
      clientAborted = true;
    });

    connectWithFailover(hostname, port, scheduler, opts)
      .then(({ socket, uplink }) => {
        if (clientAborted || clientReq.destroyed || clientRes.writableEnded) {
          socket.destroy();
          return;
        }
        const headers = sanitizeHeaders(clientReq.headers, targetUrl.host);

        const proxyReq = http.request({
          hostname,
          port,
          path,
          method: clientReq.method,
          headers,
          agent: false,
          createConnection: () => socket
        });

        // Count outbound HTTP request body bytes (uploads)
        clientReq.on('data', (chunk) => {
          uplink.bytesUp += chunk.length;
        });

        proxyReq.on('response', (proxyRes) => {
          const resHeaders = sanitizeHeaders(proxyRes.headers);
          clientRes.writeHead(proxyRes.statusCode || 502, proxyRes.statusMessage, resHeaders);
          proxyRes.on('data', (chunk) => {
            uplink.bytesDown += chunk.length;
          });
          proxyRes.pipe(clientRes);
        });

        proxyReq.on('error', (err) => {
          if (!clientRes.headersSent) {
            clientRes.writeHead(502, { 'Content-Type': 'text/plain' });
          }
          clientRes.end(`Upstream error (${uplink.label}): ${err.message}`);
        });

        clientReq.on('aborted', () => {
          proxyReq.destroy();
        });

        clientReq.pipe(proxyReq);
        clientReq.resume();
      })
      .catch((err) => {
        if (!clientRes.headersSent) {
          clientRes.writeHead(502, { 'Content-Type': 'text/plain' });
        }
        clientRes.end(`Bad Gateway: ${err.message}`);
      });
  });

  server.on('connect', (req, clientSocket, head) => {
    const target = parseAuthority(req.url, 443);
    if (!target) {
      clientSocket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
      return;
    }

    clientSocket.on('error', () => {});

    connectWithFailover(target.host, target.port, scheduler, opts)
      .then(({ socket: remoteSocket, uplink }) => {
        if (clientSocket.destroyed) {
          remoteSocket.destroy();
          return;
        }
        clientSocket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        bridgeSockets(clientSocket, remoteSocket, uplink, head);
      })
      .catch(() => {
        if (!clientSocket.destroyed) {
          clientSocket.end('HTTP/1.1 502 Bad Gateway\r\n\r\n');
        }
      });
  });

  server.on('clientError', (err, socket) => {
    if (err.code === 'ECONNRESET' || !socket.writable) return;
    socket.end('HTTP/1.1 400 Bad Request\r\n\r\n');
  });

  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(opts.httpPort, opts.bind, () => {
      server.removeListener('error', reject);
      resolve(server);
    });
  });
}

function sendSocksReply(socket, rep) {
  if (!socket.writable) return;
  socket.write(Buffer.from([0x05, rep, 0x00, 0x01, 0, 0, 0, 0, 0, 0]));
}

function handleSocksClient(clientSocket, scheduler, opts) {
  let state = 'GREETING';
  let buffer = Buffer.alloc(0);

  const onError = () => {
    if (!clientSocket.destroyed) clientSocket.destroy();
  };
  clientSocket.on('error', onError);

  const onData = (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);

    while (state !== 'TUNNELED') {
      if (state === 'GREETING') {
        if (buffer.length < 2) return;
        const ver = buffer[0];
        const nmethods = buffer[1];
        if (ver !== 0x05) {
          clientSocket.destroy();
          return;
        }
        if (buffer.length < 2 + nmethods) return;
        const methods = buffer.subarray(2, 2 + nmethods);
        buffer = buffer.subarray(2 + nmethods);

        if (!methods.includes(0x00)) {
          clientSocket.end(Buffer.from([0x05, 0xff]));
          return;
        }
        clientSocket.write(Buffer.from([0x05, 0x00]));
        state = 'REQUEST';
        continue;
      }

      if (state === 'REQUEST') {
        if (buffer.length < 4) return;
        const ver = buffer[0];
        const cmd = buffer[1];
        const atyp = buffer[3];
        if (ver !== 0x05) {
          clientSocket.destroy();
          return;
        }

        let host = null;
        let offset = 4;
        if (atyp === 0x01) {
          // IPv4
          if (buffer.length < offset + 4 + 2) return;
          host = `${buffer[offset]}.${buffer[offset + 1]}.${buffer[offset + 2]}.${buffer[offset + 3]}`;
          offset += 4;
        } else if (atyp === 0x03) {
          // Domain name
          if (buffer.length < offset + 1) return;
          const len = buffer[offset];
          offset += 1;
          if (buffer.length < offset + len + 2) return;
          host = buffer.subarray(offset, offset + len).toString('utf8');
          offset += len;
        } else if (atyp === 0x04) {
          // IPv6
          if (buffer.length < offset + 16 + 2) return;
          sendSocksReply(clientSocket, 0x08); // Address type not supported (IPv4 uplinks)
          clientSocket.end();
          return;
        } else {
          sendSocksReply(clientSocket, 0x08);
          clientSocket.end();
          return;
        }

        const port = buffer.readUInt16BE(offset);
        offset += 2;
        const trailing = buffer.subarray(offset);
        buffer = Buffer.alloc(0);

        if (cmd !== 0x01) {
          sendSocksReply(clientSocket, 0x07); // Command not supported
          clientSocket.end();
          return;
        }

        state = 'TUNNELED';
        clientSocket.removeListener('data', onData);
        clientSocket.pause();

        connectWithFailover(host, port, scheduler, opts)
          .then(({ socket: remoteSocket, uplink }) => {
            if (clientSocket.destroyed) {
              remoteSocket.destroy();
              return;
            }
            sendSocksReply(clientSocket, 0x00);
            bridgeSockets(clientSocket, remoteSocket, uplink, trailing);
            clientSocket.resume();
          })
          .catch((err) => {
            const rep = err && err.code === 'ECONNREFUSED' ? 0x05 : 0x04;
            sendSocksReply(clientSocket, rep);
            clientSocket.end();
          });
        return;
      }
    }
  };

  clientSocket.on('data', onData);
}

function startSocksProxy(scheduler, opts) {
  const server = net.createServer((socket) => {
    handleSocksClient(socket, scheduler, opts);
  });

  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(opts.socksPort, opts.bind, () => {
      server.removeListener('error', reject);
      resolve(server);
    });
  });
}

function formatMbps(deltaBytes, intervalMs) {
  const mbps = (deltaBytes * 8) / (intervalMs * 1000);
  return mbps.toFixed(2);
}

function formatMB(bytes) {
  return (bytes / (1024 * 1024)).toFixed(2);
}

async function main() {
  let opts;
  try {
    opts = parseArgs(process.argv.slice(2));
  } catch (err) {
    console.error(`Error: ${err.message}`);
    process.exit(1);
  }

  let uplinks;
  try {
    uplinks = parseUplinks(opts);
  } catch (err) {
    console.error(`Error: ${err.message}`);
    process.exit(1);
  }

  if (uplinks.length === 0) {
    console.error('No active IPv4 uplinks found. Connect Ethernet/Wi-Fi or pass --uplinks.');
    process.exit(1);
  }

  if (uplinks.length === 1) {
    console.warn(`Warning: Only 1 uplink detected (${uplinks[0].label} = ${uplinks[0].ip}). Load balancing requires 2+ active adapters.`);
  }

  const scheduler = new UplinkScheduler(uplinks, opts.strategy);
  const servers = [];

  if (opts.httpPort > 0) {
    const httpServer = await startHttpProxy(scheduler, opts);
    servers.push(httpServer);
  }
  if (opts.socksPort > 0) {
    const socksServer = await startSocksProxy(scheduler, opts);
    servers.push(socksServer);
  }

  console.log('=== Dual-WAN Load-Balancing Proxy Started ===');
  console.log(`Strategy : ${opts.strategy}`);
  if (opts.httpPort > 0) {
    console.log(`HTTP/HTTPS Proxy : http://${opts.bind}:${opts.httpPort}`);
  }
  if (opts.socksPort > 0) {
    console.log(`SOCKS5 Proxy     : socks5://${opts.bind}:${opts.socksPort}`);
  }
  console.log('Uplinks  :');
  for (const u of uplinks) {
    console.log(`  - ${u.label.padEnd(16)} IP=${u.ip.padEnd(16)} Weight=${u.weight}`);
  }
  console.log('Press Ctrl+C to stop.\n');

  let statsTimer = null;
  if (opts.statsIntervalMs > 0) {
    statsTimer = setInterval(() => {
      const parts = uplinks.map((u) => {
        const dlDelta = u.bytesDown - u.prevBytesDown;
        const ulDelta = u.bytesUp - u.prevBytesUp;
        u.prevBytesDown = u.bytesDown;
        u.prevBytesUp = u.bytesUp;
        return `${u.label}[act:${u.activeConnections} tot:${u.totalConnections} DL:${formatMbps(dlDelta, opts.statsIntervalMs)}Mbps(${formatMB(u.bytesDown)}MB) UL:${formatMbps(ulDelta, opts.statsIntervalMs)}Mbps(${formatMB(u.bytesUp)}MB)]`;
      });
      console.log(`[${new Date().toLocaleTimeString()}] ${parts.join(' | ')}`);
    }, opts.statsIntervalMs);
    statsTimer.unref();
  }

  const shutdown = () => {
    if (statsTimer) clearInterval(statsTimer);
    for (const s of servers) {
      s.close();
    }
    console.log('\nProxy stopped.');
    process.exit(0);
  };

  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}

if (require.main === module) {
  main().catch((err) => {
    console.error(`Fatal error: ${err.message}`);
    process.exit(1);
  });
}

module.exports = {
  parseArgs,
  parseUplinks,
  UplinkScheduler,
  parseAuthority,
  sanitizeHeaders,
  isSelfProxyTarget,
  isLikelyVirtualInterface,
  handleSocksClient,
  bridgeSockets,
  formatMbps,
  formatMB
};

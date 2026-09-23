using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace pCUE
{
    /// <summary>
    /// Advertises this pCUE instance on the LAN so a controller does not need to be told the test
    /// bench's IP address.
    ///
    /// Deliberately a PASSIVE responder, not a periodic broadcaster: it answers a probe and is
    /// otherwise silent, so it adds no background chatter and cannot be used to map a network by
    /// simply listening. The reply carries only what a controller needs to connect - app name,
    /// version, machine name, the API URL, and whether a token is required. It never contains the
    /// token itself.
    ///
    /// LAN-TRUST ONLY: replies are unauthenticated and any local process can answer probes too
    /// (ReuseAddress). Controllers must confirm the URL before sending a token (the in-app client
    /// and CLI list candidates; auto-use only when exactly one answers). Replies are rate-limited
    /// per sender so a probe flood cannot turn the beacon into an amplifier.
    ///
    /// Protocol (UDP, default port 5057):
    ///   probe  -> "PCUE_DISCOVER"      (broadcast or unicast)
    ///   reply  -> {"app":"pCUE","version":"1.3.2","host":"BENCH-PC","url":"http://10.0.0.5:5056/",
    ///              "requiresToken":true}
    /// </summary>
    public sealed class DiscoveryBeacon : IDisposable
    {
        public const int DefaultPort = 5057;
        private const string Probe = "PCUE_DISCOVER";
        // At most one reply per sender per interval; bursts beyond that are dropped.
        private const int MinReplyIntervalMs = 1000;
        private const int MaxTrackedSenders = 256;

        private readonly int _port;
        private readonly int _apiPort;
        private readonly bool _requiresToken;
        private UdpClient _udp;
        private CancellationTokenSource _cts;
        private bool _disposed;
        private readonly System.Collections.Generic.Dictionary<string, long> _lastReplyBySender =
            new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object _replyGate = new object();

        public DiscoveryBeacon(int apiPort, bool requiresToken, int port = DefaultPort)
        {
            _apiPort = apiPort;
            _requiresToken = requiresToken;
            _port = port;
        }

        public bool IsRunning { get; private set; }

        public void Start()
        {
            if (IsRunning) return;
            try
            {
                _udp = new UdpClient();
                //Allow several listeners on one machine (e.g. a dev instance beside a test instance)
                //rather than failing outright.
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
                IsRunning = true;

                _cts = new CancellationTokenSource();
                Task.Run(() => ListenAsync(_cts.Token));
                AppLog.Info("Discovery beacon answering probes on UDP " + _port + ".");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                AppLog.Warn("Discovery beacon could not start: " + ex.Message);
            }
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsRunning)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _udp.ReceiveAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    AppLog.Warn("Discovery receive failed: " + ex.Message);
                    break;
                }

                try
                {
                    string text = Encoding.UTF8.GetString(received.Buffer).Trim();
                    if (!text.StartsWith(Probe, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!AllowReply(received.RemoteEndPoint.ToString())) continue;

                    //Answer from the address the probe arrived on, so the URL we hand back is the
                    //one the caller can actually reach.
                    string localIp = LocalAddressFor(received.RemoteEndPoint.Address);

                    string json = new JavaScriptSerializer().Serialize(new
                    {
                        app = "pCUE",
                        protocolVersion = PcueRemoteClient.MinimumProtocolVersion,
                        version = AppUpdateService.InstalledVersion,
                        host = Environment.MachineName,
                        url = "http://" + localIp + ":" + _apiPort + "/",
                        requiresToken = _requiresToken,
                    });

                    byte[] reply = Encoding.UTF8.GetBytes(json);
                    await _udp.SendAsync(reply, reply.Length, received.RemoteEndPoint).ConfigureAwait(false);
                    AppLog.Debug("Discovery probe from " + received.RemoteEndPoint + " answered.");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Discovery reply failed: " + ex.Message);
                }
            }
        }

        /// <summary>Picks the local address on the same route as the caller (best effort).</summary>
        private static string LocalAddressFor(IPAddress remote)
        {
            try
            {
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(new IPEndPoint(remote, 9));
                    return ((IPEndPoint)probe.LocalEndPoint).Address.ToString();
                }
            }
            catch
            {
                return Dns.GetHostName();
            }
        }

        private bool AllowReply(string sender)
        {
            long now = Environment.TickCount;
            lock (_replyGate)
            {
                long last;
                if (_lastReplyBySender.TryGetValue(sender, out last) && (now - last) < MinReplyIntervalMs)
                    return false;
                if (_lastReplyBySender.Count >= MaxTrackedSenders)
                    _lastReplyBySender.Clear();
                _lastReplyBySender[sender] = now;
                return true;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _udp?.Close(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _cts?.Dispose(); } catch { }
        }
    }
}

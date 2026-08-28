using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace pCUE
{
    /// <summary>
    /// In-process client for another pCUE instance. It consumes the same HTTP/SSE surface as the
    /// CLI, but exposes typed snapshots and commands to the WPF window without spawning a shell or
    /// depending on any companion program.
    /// </summary>
    public sealed class PcueRemoteClient : IDisposable
    {
        public const int MinimumProtocolVersion = 2;
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(7);
        private static readonly int[] ReconnectDelaysMs = { 1000, 2000, 5000, 10000 };

        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly SemaphoreSlim _commandGate = new SemaphoreSlim(1, 1);
        private HttpClient _http;
        private CancellationTokenSource _lifetime;
        private Task _streamWorker;
        private Uri _baseUri;
        private string _token = "";
        private bool _disposed;

        public event EventHandler<PcueStatusSnapshot> SnapshotReceived;
        public event EventHandler<PcueRemoteConnectionEventArgs> ConnectionChanged;

        public bool IsConnected { get; private set; }
        public string Endpoint { get { return _baseUri == null ? "" : _baseUri.ToString().TrimEnd('/'); } }
        public DateTime LastSnapshotUtc { get; private set; } = DateTime.MinValue;

        public async Task<PcueStatusSnapshot> ConnectAsync(string host, int port, string token)
        {
            ThrowIfDisposed();
            Disconnect();

            _baseUri = BuildBaseUri(host, port);
            _token = token ?? "";
            _http = new HttpClient { BaseAddress = _baseUri, Timeout = Timeout.InfiniteTimeSpan };
            _lifetime = new CancellationTokenSource();

            PcueStatusSnapshot first;
            try
            {
                first = await GetStatusAsync(_lifetime.Token).ConfigureAwait(false);
                ValidateSnapshot(first);
            }
            catch
            {
                Disconnect();
                throw;
            }

            SetConnected(true, "Connected to " + first.machine + " (pCUE " + first.version + ")");
            Publish(first);
            CancellationToken streamToken = _lifetime.Token;
            _streamWorker = Task.Run(() => StreamWithReconnectAsync(streamToken));
            return first;
        }

        public void Disconnect()
        {
            CancellationTokenSource lifetime = _lifetime;
            HttpClient http = _http;
            _lifetime = null;
            _streamWorker = null;
            _http = null;

            try { lifetime?.Cancel(); } catch { }
            try { http?.CancelPendingRequests(); } catch { }
            try { http?.Dispose(); } catch { }
            try { lifetime?.Dispose(); } catch { }

            bool wasConnected = IsConnected;
            IsConnected = false;
            LastSnapshotUtc = DateTime.MinValue;
            if (wasConnected) RaiseConnectionChanged(false, "Disconnected");
        }

        public Task<PcueApiActionResponse> SetFanModeAsync(int fan, string mode) =>
            PostAsync("fan/mode?fan=" + fan + "&value=" + Uri.EscapeDataString(mode ?? ""), null);

        public Task<PcueApiActionResponse> SetCommanderOpenAsync(bool open) =>
            PostAsync(open ? "commander/open" : "commander/close", null);

        public Task<PcueApiActionResponse> SetCpuMonitoringAsync(bool on) =>
            PostAsync(on ? "cpu/start" : "cpu/stop", null);

        public Task<PcueApiActionResponse> SetTachConnectedAsync(bool connected) =>
            PostAsync(connected ? "tach/connect" : "tach/disconnect", null);

        public Task<PcueApiActionResponse> SetTachAssignmentAsync(int fan) =>
            PostAsync("tach/assign?fan=" + fan, null);

        public Task<PcueApiActionResponse> StopHoldAsync() => PostAsync("hold/stop", null);
        public Task<PcueApiActionResponse> ResetStatsAsync() => PostAsync("reset", null);
        public Task<PcueApiActionResponse> KillIcueAsync() => PostAsync("system/kill-icue", null);

        public Task<PcueApiActionResponse> SetAverageValuesAsync(bool on) =>
            PostAsync("settings/average?value=" + (on ? "1" : "0"), null);

        public Task<PcueApiActionResponse> SetAutoStartAsync(bool on) =>
            PostAsync("settings/auto-start?value=" + (on ? "1" : "0"), null);

        public Task<PcueApiActionResponse> SetAutoConnectAsync(bool on) =>
            PostAsync("settings/auto-connect?value=" + (on ? "1" : "0"), null);

        public Task<PcueApiActionResponse> SetTachoAdjustAsync(bool on) =>
            PostAsync("settings/tacho-adjust?value=" + (on ? "1" : "0"), null);

        public Task<PcueApiActionResponse> ApplyFanSetpointsAsync(IReadOnlyList<int> values)
        {
            if (values == null || values.Count != 6)
                throw new ArgumentException("Exactly six fan setpoints are required.", nameof(values));
            return PostAsync("fans/apply", new Dictionary<string, object>
            {
                ["values"] = values.ToArray(),
            });
        }

        public async Task<PcueDiscoveryResult> DiscoverAsync(int timeoutMs = 1800)
        {
            ThrowIfDisposed();
            using var udp = new UdpClient(0);
            udp.EnableBroadcast = true;
            byte[] probe = Encoding.UTF8.GetBytes("PCUE_DISCOVER");
            await udp.SendAsync(probe, probe.Length,
                new IPEndPoint(IPAddress.Broadcast, DiscoveryBeacon.DefaultPort)).ConfigureAwait(false);

            Task<UdpReceiveResult> receive = udp.ReceiveAsync();
            Task timeout = Task.Delay(Math.Max(250, timeoutMs));
            Task completed = await Task.WhenAny(receive, timeout).ConfigureAwait(false);
            if (completed != receive) return null;

            string json = Encoding.UTF8.GetString(receive.Result.Buffer);
            PcueDiscoveryResult result = _serializer.Deserialize<PcueDiscoveryResult>(json);
            return result != null && string.Equals(result.app, "pCUE", StringComparison.OrdinalIgnoreCase)
                ? result
                : null;
        }

        private async Task StreamWithReconnectAsync(CancellationToken token)
        {
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ConsumeStreamAsync(token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested) throw new IOException("Remote status stream ended.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    IsConnected = false;
                    RaiseConnectionChanged(false, "Connection lost: " + ex.Message);

                    int delay = ReconnectDelaysMs[Math.Min(failures, ReconnectDelaysMs.Length - 1)];
                    failures++;
                    try { await Task.Delay(delay, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    try
                    {
                        PcueStatusSnapshot recovered = await GetStatusAsync(token).ConfigureAwait(false);
                        ValidateSnapshot(recovered);
                        failures = 0;
                        SetConnected(true, "Reconnected to " + recovered.machine);
                        Publish(recovered);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch { /* the loop retries with bounded backoff */ }
                }
            }
        }

        private async Task ConsumeStreamAsync(CancellationToken token)
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "stream?interval=500");
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await CreateHttpErrorAsync(response).ConfigureAwait(false);

            using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!token.IsCancellationRequested)
            {
                string line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                PcueStatusSnapshot snapshot = _serializer.Deserialize<PcueStatusSnapshot>(line.Substring(6));
                ValidateSnapshot(snapshot);
                if (!IsConnected) SetConnected(true, "Connected");
                Publish(snapshot);
            }
        }

        private async Task<PcueStatusSnapshot> GetStatusAsync(CancellationToken outerToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            timeout.CancelAfter(CommandTimeout);
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "status");
            using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateHttpError(response.StatusCode, json);
            return _serializer.Deserialize<PcueStatusSnapshot>(json);
        }

        private async Task<PcueApiActionResponse> PostAsync(string path, object body)
        {
            ThrowIfDisposed();
            if (_http == null || _lifetime == null || !IsConnected)
                return new PcueApiActionResponse { ok = false, error = "Remote pCUE is not connected." };

            await _commandGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(CommandTimeout);
                using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
                if (body != null)
                {
                    string json = _serializer.Serialize(body);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                PcueApiActionResponse result;
                try { result = _serializer.Deserialize<PcueApiActionResponse>(text); }
                catch { result = null; }
                if (result == null) result = new PcueApiActionResponse();
                if (!response.IsSuccessStatusCode)
                {
                    result.ok = false;
                    if (string.IsNullOrWhiteSpace(result.error))
                        result.error = "Remote pCUE returned HTTP " + (int)response.StatusCode + ".";
                }
                if (result.status != null) Publish(result.status);
                return result;
            }
            catch (Exception ex)
            {
                return new PcueApiActionResponse { ok = false, error = ex.Message };
            }
            finally { _commandGate.Release(); }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string relative)
        {
            var request = new HttpRequestMessage(method, relative);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(_token)) request.Headers.Add("X-pCUE-Token", _token);
            return request;
        }

        private static async Task<Exception> CreateHttpErrorAsync(HttpResponseMessage response)
        {
            string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return CreateHttpError(response.StatusCode, text);
        }

        private static Exception CreateHttpError(HttpStatusCode status, string body)
        {
            string detail = body;
            try
            {
                var parsed = new JavaScriptSerializer().Deserialize<PcueApiActionResponse>(body);
                if (!string.IsNullOrWhiteSpace(parsed?.error)) detail = parsed.error;
            }
            catch { }
            if (status == HttpStatusCode.Unauthorized)
                detail = "Authentication failed. Check the remote pCUE token.";
            return new InvalidOperationException(detail ?? ("HTTP " + (int)status));
        }

        private static Uri BuildBaseUri(string host, int port)
        {
            string value = (host ?? "").Trim();
            if (value.Length == 0) throw new ArgumentException("Enter the remote PC name or IP address.");
            if (!value.Contains("://")) value = "http://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri parsed))
                throw new ArgumentException("The remote PC address is not valid.");
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("The remote pCUE address must use HTTP or HTTPS.");
            if (!string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) ||
                !string.IsNullOrEmpty(parsed.Fragment))
                throw new ArgumentException("Enter only a host name or pCUE base URL.");
            if (port < 1 || port > 65535) throw new ArgumentException("Port must be 1-65535.");

            var builder = new UriBuilder(parsed) { Port = port, Path = "/", Query = "", Fragment = "" };
            return builder.Uri;
        }

        private static void ValidateSnapshot(PcueStatusSnapshot snapshot)
        {
            if (snapshot == null || !string.Equals(snapshot.app, "pCUE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The target did not identify itself as pCUE.");
            if (snapshot.protocolVersion < MinimumProtocolVersion)
                throw new InvalidOperationException("Remote pCUE is too old for GUI control. Update it first.");
            if (snapshot.fans == null || snapshot.fans.Count != 6)
                throw new InvalidOperationException("Remote pCUE returned an incomplete fan status.");
        }

        private void Publish(PcueStatusSnapshot snapshot)
        {
            LastSnapshotUtc = DateTime.UtcNow;
            SnapshotReceived?.Invoke(this, snapshot);
        }

        private void SetConnected(bool connected, string message)
        {
            bool changed = IsConnected != connected;
            IsConnected = connected;
            if (changed || connected) RaiseConnectionChanged(connected, message);
        }

        private void RaiseConnectionChanged(bool connected, string message) =>
            ConnectionChanged?.Invoke(this, new PcueRemoteConnectionEventArgs(connected, message));

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PcueRemoteClient));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _commandGate.Dispose();
        }
    }

    public sealed class PcueRemoteConnectionEventArgs : EventArgs
    {
        public PcueRemoteConnectionEventArgs(bool connected, string message)
        {
            Connected = connected;
            Message = message ?? "";
        }

        public bool Connected { get; }
        public string Message { get; }
    }
}

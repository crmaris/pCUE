using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace pCUE
{
    /// <summary>
    /// What the remote API is allowed to do to the app. MainWindow implements this and marshals
    /// every call to the UI thread, so the HTTP worker threads never touch WPF objects directly.
    /// </summary>
    public interface IRemoteControlTarget
    {
        /// <summary>Everything a caller might want to read, as a serializable object.</summary>
        object GetStatus();

        /// <summary>Set fan power (0-100 %). Returns null on success, or an error message.</summary>
        string SetFanDuty(int fan, int duty);

        /// <summary>Set the Commander's own fixed-RPM target. Returns null on success, or an error.</summary>
        string SetFanRpm(int fan, int rpm);

        /// <summary>Set connection mode: auto | 3pin | 4pin | disconnect.</summary>
        string SetFanMode(int fan, string mode);

        /// <summary>Start the software closed-loop RPM hold on a fan.</summary>
        string StartHold(int fan, int rpm);

        /// <summary>Stop the closed-loop RPM hold.</summary>
        string StopHold();

        /// <summary>Open or close the Commander PRO connection.</summary>
        string SetCommanderOpen(bool open);

        /// <summary>Start or stop CPU monitoring.</summary>
        string SetCpuMonitoring(bool on);

        /// <summary>Connect or disconnect the bench tachometer.</summary>
        string SetTachConnected(bool connected);

        /// <summary>Assign the tachometer to a fan (1-6), or 0 for None.</summary>
        string SetTachAssignment(int fan);

        /// <summary>Reset the Min/Max/Avg statistics.</summary>
        string ResetStats();

        /// <summary>Current closed-loop tunables.</summary>
        object GetHoldConfig();

        /// <summary>Live-tune the loop. The callback returns null for keys the caller omitted.</summary>
        string SetHoldConfig(Func<string, double?> get);
    }

    /// <summary>
    /// Built-in HTTP remote-control server, modelled on Powenetics V3's RemoteControlServer so the
    /// two apps behave the same way. Lets pCUE be driven over the network (or scripted locally)
    /// instead of through the GUI - which is also the only practical way to diagnose it on a
    /// headless or remote test bench.
    ///
    /// SECURITY - the defaults are deliberately tight, because this API spins fans on real hardware:
    ///   * The server is OFF unless explicitly enabled.
    ///   * It binds to 127.0.0.1 by default.
    ///   * With NO token configured, requests from anywhere other than loopback are REFUSED, even if
    ///     the prefix was widened. Exposing it on the LAN therefore REQUIRES setting a token.
    ///   * The token is accepted as the X-pCUE-Token header or a ?token= query parameter.
    /// Note that binding to a non-loopback prefix needs either an admin process (pCUE already runs
    /// elevated) or a netsh urlacl reservation.
    ///
    /// Threading: an accept loop on a background Task; each request handled on the thread pool.
    /// </summary>
    public sealed class RemoteControlServer : IDisposable
    {
        public const string DefaultPrefix = "http://127.0.0.1:5056/";

        private readonly IRemoteControlTarget _target;
        private readonly string _token;
        private readonly HttpListener _listener = new HttpListener();
        private CancellationTokenSource _cts;
        private Task _worker;
        private bool _disposed;

        public string Prefix { get; }
        public bool IsRunning { get; private set; }

        public RemoteControlServer(IRemoteControlTarget target, string prefix, string token)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            Prefix = string.IsNullOrWhiteSpace(prefix) ? DefaultPrefix : prefix;
            if (!Prefix.EndsWith("/")) Prefix += "/";
            _token = token ?? "";
            _listener.Prefixes.Add(Prefix);
        }

        public void Start()
        {
            if (IsRunning) return;
            _listener.Start();
            IsRunning = true;
            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Debug.WriteLine("pCUE: remote control listening on " + Prefix +
                            (string.IsNullOrEmpty(_token) ? " (loopback only, no token)" : " (token required)"));
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            Debug.WriteLine("pCUE: remote control stopped.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _listener.Close(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }      // listener stopped
                catch (Exception ex)
                {
                    Debug.WriteLine("pCUE: remote accept failed: " + ex.Message);
                    break;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                if (!IsAuthorized(context.Request))
                {
                    await WriteJsonAsync(context, HttpStatusCode.Unauthorized, new
                    {
                        error = "Unauthorized. Send X-pCUE-Token (or ?token=). " +
                                "Without a configured token only loopback requests are accepted."
                    }).ConfigureAwait(false);
                    return;
                }

                string path = (context.Request.Url.AbsolutePath ?? "/").TrimEnd('/');
                if (path.Length == 0) path = "/";

                switch (path.ToLowerInvariant())
                {
                    case "/":
                        await WriteJsonAsync(context, HttpStatusCode.OK, Index()).ConfigureAwait(false);
                        return;

                    case "/status":
                        await WriteJsonAsync(context, HttpStatusCode.OK, _target.GetStatus()).ConfigureAwait(false);
                        return;

                    case "/fan/duty":
                        await Act(context, body => _target.SetFanDuty(
                            ReadInt(context, body, "fan", -1), ReadInt(context, body, "value", -1))).ConfigureAwait(false);
                        return;

                    case "/fan/rpm":
                        await Act(context, body => _target.SetFanRpm(
                            ReadInt(context, body, "fan", -1), ReadInt(context, body, "value", -1))).ConfigureAwait(false);
                        return;

                    case "/fan/mode":
                        await Act(context, body => _target.SetFanMode(
                            ReadInt(context, body, "fan", -1), ReadString(context, body, "value"))).ConfigureAwait(false);
                        return;

                    case "/hold/start":
                        await Act(context, body => _target.StartHold(
                            ReadInt(context, body, "fan", -1), ReadInt(context, body, "rpm", -1))).ConfigureAwait(false);
                        return;

                    case "/hold/stop":
                        await Act(context, body => _target.StopHold()).ConfigureAwait(false);
                        return;

                    case "/hold/config":
                        {
                            //No parameters at all = read; otherwise apply what was supplied.
                            Dictionary<string, object> body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
                            bool any = (body != null && body.Count > 0) || context.Request.QueryString.Count > 0;
                            if (!any)
                            {
                                await WriteJsonAsync(context, HttpStatusCode.OK, _target.GetHoldConfig()).ConfigureAwait(false);
                                return;
                            }

                            string err = _target.SetHoldConfig(name =>
                            {
                                if (body != null && body.TryGetValue(name, out object bv) && bv != null &&
                                    double.TryParse(Convert.ToString(bv), System.Globalization.NumberStyles.Any,
                                                    System.Globalization.CultureInfo.InvariantCulture, out double bd))
                                    return bd;
                                string q = context.Request.QueryString[name];
                                if (!string.IsNullOrWhiteSpace(q) &&
                                    double.TryParse(q, System.Globalization.NumberStyles.Any,
                                                    System.Globalization.CultureInfo.InvariantCulture, out double qd))
                                    return qd;
                                return null;
                            });

                            if (err == null)
                                await WriteJsonAsync(context, HttpStatusCode.OK,
                                    new { ok = true, config = _target.GetHoldConfig() }).ConfigureAwait(false);
                            else
                                await WriteJsonAsync(context, HttpStatusCode.BadRequest,
                                    new { ok = false, error = err }).ConfigureAwait(false);
                            return;
                        }

                    case "/commander/open":
                        await Act(context, body => _target.SetCommanderOpen(true)).ConfigureAwait(false);
                        return;

                    case "/commander/close":
                        await Act(context, body => _target.SetCommanderOpen(false)).ConfigureAwait(false);
                        return;

                    case "/cpu/start":
                        await Act(context, body => _target.SetCpuMonitoring(true)).ConfigureAwait(false);
                        return;

                    case "/cpu/stop":
                        await Act(context, body => _target.SetCpuMonitoring(false)).ConfigureAwait(false);
                        return;

                    case "/tach/connect":
                        await Act(context, body => _target.SetTachConnected(true)).ConfigureAwait(false);
                        return;

                    case "/tach/disconnect":
                        await Act(context, body => _target.SetTachConnected(false)).ConfigureAwait(false);
                        return;

                    case "/tach/assign":
                        await Act(context, body => _target.SetTachAssignment(
                            ReadInt(context, body, "fan", -1))).ConfigureAwait(false);
                        return;

                    case "/reset":
                        await Act(context, body => _target.ResetStats()).ConfigureAwait(false);
                        return;

                    // ---- diagnostics -------------------------------------------------------
                    case "/log":
                        await WriteJsonAsync(context, HttpStatusCode.OK, new
                        {
                            level = AppLog.Level.ToString(),
                            file = AppLog.FileEnabled ? AppLog.FilePath : null,
                            lines = AppLog.Tail(ReadIntQuery(context.Request, "tail", 200)),
                        }).ConfigureAwait(false);
                        return;

                    case "/log/level":
                        {
                            string want = (context.Request.QueryString["value"] ?? "").Trim();
                            if (want.Length == 0)
                            {
                                await WriteJsonAsync(context, HttpStatusCode.OK,
                                    new { level = AppLog.Level.ToString() }).ConfigureAwait(false);
                                return;
                            }
                            if (!Enum.TryParse(want, true, out LogLevel parsed))
                            {
                                await WriteJsonAsync(context, HttpStatusCode.BadRequest,
                                    new { error = "value must be debug | info | warn | error." }).ConfigureAwait(false);
                                return;
                            }
                            AppLog.Level = parsed;
                            AppLog.Info("Log level set to " + parsed + " via remote API.");
                            await WriteJsonAsync(context, HttpStatusCode.OK,
                                new { ok = true, level = parsed.ToString() }).ConfigureAwait(false);
                            return;
                        }

                    case "/log/clear":
                        AppLog.Clear();
                        await WriteJsonAsync(context, HttpStatusCode.OK, new { ok = true }).ConfigureAwait(false);
                        return;

                    case "/stream":
                        await StreamAsync(context).ConfigureAwait(false);
                        return;

                    default:
                        await WriteJsonAsync(context, HttpStatusCode.NotFound,
                            new { error = "Unknown endpoint: " + path }).ConfigureAwait(false);
                        return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: remote request failed: " + ex.Message);
                try
                {
                    await WriteJsonAsync(context, HttpStatusCode.InternalServerError,
                        new { error = ex.Message }).ConfigureAwait(false);
                }
                catch { }
            }
        }

        /// <summary>Runs an action that returns null on success or an error string, and replies.</summary>
        private async Task Act(HttpListenerContext context, Func<Dictionary<string, object>, string> action)
        {
            Dictionary<string, object> body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            string error = action(body);
            if (error == null)
                await WriteJsonAsync(context, HttpStatusCode.OK, new { ok = true, status = _target.GetStatus() }).ConfigureAwait(false);
            else
                await WriteJsonAsync(context, HttpStatusCode.BadRequest, new { ok = false, error }).ConfigureAwait(false);
        }

        /// <summary>Server-sent events: the full status, repeatedly. ?interval=ms (default 1000).</summary>
        private async Task StreamAsync(HttpListenerContext context)
        {
            int interval = ReadIntQuery(context.Request, "interval", 1000);
            if (interval < 200) interval = 200;

            HttpListenerResponse response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache";

            var serializer = new JavaScriptSerializer();
            try
            {
                Stream output = response.OutputStream;
                await WriteChunk(output, ": pCUE status stream\nretry: 2000\n\n").ConfigureAwait(false);

                while (IsRunning)
                {
                    string json = serializer.Serialize(_target.GetStatus());
                    await WriteChunk(output, "data: " + json + "\n\n").ConfigureAwait(false);
                    await Task.Delay(interval).ConfigureAwait(false);
                }
            }
            catch (HttpListenerException) { /* client went away */ }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Debug.WriteLine("pCUE: remote stream ended: " + ex.Message); }
            finally { try { response.Close(); } catch { } }
        }

        private static async Task WriteChunk(Stream output, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await output.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }

        private object Index()
        {
            return new
            {
                app = "pCUE",
                version = AppUpdateService.InstalledVersion,
                endpoints = new[]
                {
                    "GET  /status                     - full snapshot (connection, fans, CPU, tachometer, hold)",
                    "GET  /stream?interval=1000       - the same snapshot as server-sent events",
                    "POST /fan/duty?fan=1&value=60    - set fan power, 0-100 %",
                    "POST /fan/rpm?fan=1&value=800    - Commander fixed RPM (4-pin/PWM channels only)",
                    "POST /fan/mode?fan=1&value=3pin  - auto | 3pin | 4pin | disconnect",
                    "POST /hold/start?fan=1&rpm=800   - software closed-loop RPM hold",
                    "POST /hold/stop                  - stop the hold",
                    "POST /commander/open|close       - connect / disconnect the Commander PRO",
                    "POST /cpu/start|stop             - CPU monitoring",
                    "POST /tach/connect|disconnect    - bench tachometer",
                    "POST /tach/assign?fan=2          - feed fan N's RPM from the tachometer (0 = none)",
                    "POST /reset                      - reset Min/Max/Avg statistics",
                    "GET  /log?tail=200               - recent diagnostic log lines",
                    "GET  /log/level?value=debug      - read or set the log level",
                    "POST /log/clear                  - clear the in-memory log",
                },
                notes = new[]
                {
                    "fan is 1-6 (as labelled in the UI).",
                    "Parameters may be sent as a query string or a JSON body.",
                    "The Commander PRO only regulates by RPM on 4-pin/PWM channels; use /hold/start for 3-pin fans.",
                },
                auth = string.IsNullOrEmpty(_token)
                    ? "No token configured, so only loopback requests are accepted."
                    : "Send X-pCUE-Token or ?token=.",
            };
        }

        /// <summary>
        /// Loopback is always allowed. Anything else requires a matching token - so simply widening
        /// the prefix cannot accidentally expose unauthenticated fan control on the network.
        /// </summary>
        private bool IsAuthorized(HttpListenerRequest request)
        {
            bool loopback = request.IsLocal || IPAddress.IsLoopback(request.RemoteEndPoint.Address);
            if (string.IsNullOrWhiteSpace(_token)) return loopback;

            string header = request.Headers["X-pCUE-Token"] ?? "";
            string query = request.QueryString["token"] ?? "";
            return string.Equals(header, _token, StringComparison.Ordinal)
                || string.Equals(query, _token, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ parameter helpers
        private static async Task<Dictionary<string, object>> ReadBodyAsync(HttpListenerRequest request)
        {
            if (!request.HasEntityBody) return new Dictionary<string, object>();
            try
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
                string text = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
                var parsed = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                return parsed ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: remote body parse failed: " + ex.Message);
                return new Dictionary<string, object>();
            }
        }

        private static int ReadInt(HttpListenerContext context, Dictionary<string, object> body, string name, int fallback)
        {
            if (body != null && body.TryGetValue(name, out object v) && v != null &&
                int.TryParse(Convert.ToString(v), out int fromBody))
                return fromBody;
            return ReadIntQuery(context.Request, name, fallback);
        }

        private static int ReadIntQuery(HttpListenerRequest request, string name, int fallback)
        {
            return int.TryParse(request.QueryString[name] ?? "", out int value) ? value : fallback;
        }

        private static string ReadString(HttpListenerContext context, Dictionary<string, object> body, string name)
        {
            if (body != null && body.TryGetValue(name, out object v) && v != null)
                return Convert.ToString(v);
            return context.Request.QueryString[name] ?? "";
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, HttpStatusCode status, object payload)
        {
            string json = new JavaScriptSerializer().Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            HttpListenerResponse response = context.Response;
            response.StatusCode = (int)status;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.Close();
        }
    }
}

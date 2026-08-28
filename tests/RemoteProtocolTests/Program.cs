using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using pCUE;

namespace pCUE.RemoteProtocolTests
{
    internal static class Program
    {
        private const string Token = "integration-test-token";

        private static int Main()
        {
            try
            {
                RunAsync().GetAwaiter().GetResult();
                Console.WriteLine("Remote protocol integration tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Remote protocol integration tests FAILED: " + ex);
                return 1;
            }
        }

        private static async Task RunAsync()
        {
            int port = GetFreeTcpPort();
            var target = new FakeTarget();
            using var server = new RemoteControlServer(target, "http://127.0.0.1:" + port + "/", Token);
            server.Start();

            using (var invalid = new PcueRemoteClient())
            {
                await ThrowsAsync<InvalidOperationException>(
                    () => invalid.ConnectAsync("127.0.0.1", port, "wrong-token"),
                    "Authentication failed");
            }

            using (var client = new PcueRemoteClient())
            {
                int snapshots = 0;
                using var streamed = new ManualResetEventSlim(false);
                using var connectionLost = new ManualResetEventSlim(false);
                using var reconnected = new ManualResetEventSlim(false);
                bool connectedOnce = false;
                client.SnapshotReceived += delegate
                {
                    if (Interlocked.Increment(ref snapshots) >= 2) streamed.Set();
                };
                client.ConnectionChanged += delegate(object sender, PcueRemoteConnectionEventArgs e)
                {
                    if (e.Connected)
                    {
                        if (connectionLost.IsSet) reconnected.Set();
                        connectedOnce = true;
                    }
                    else if (connectedOnce) connectionLost.Set();
                };

                PcueStatusSnapshot status = await client.ConnectAsync("127.0.0.1", port, Token);
                Assert(client.IsConnected, "client should be connected");
                Assert(status.machine == "REMOTE-TEST", "machine name should round-trip");
                Assert(status.protocolVersion == PcueRemoteClient.MinimumProtocolVersion,
                       "protocol version should round-trip");
                Assert(streamed.Wait(TimeSpan.FromSeconds(3)), "SSE should deliver a live snapshot");

                await Ok(client.SetFanModeAsync(2, "3pin"));
                Assert(target.FanMode == "2:3pin", "fan mode route");

                int[] setpoints = { 11, 22, 33, 44, 55, 66 };
                PcueApiActionResponse applied = await client.ApplyFanSetpointsAsync(setpoints);
                Assert(applied.ok, "batch setpoints should succeed");
                Assert(target.Setpoints.SequenceEqual(setpoints), "batch setpoints body");
                Assert(applied.status != null && applied.status.fans.Count == 6,
                       "actions should return a typed status snapshot");

                await Ok(client.SetCommanderOpenAsync(true));
                await Ok(client.SetCpuMonitoringAsync(true));
                await Ok(client.SetTachConnectedAsync(true));
                await Ok(client.SetTachAssignmentAsync(4));
                await Ok(client.SetAverageValuesAsync(true));
                await Ok(client.SetAutoStartAsync(true));
                await Ok(client.SetAutoConnectAsync(true));
                await Ok(client.SetTachoAdjustAsync(true));
                await Ok(client.ResetStatsAsync());
                await Ok(client.KillIcueAsync());

                Assert(target.CommanderOpen, "Commander route");
                Assert(target.CpuMonitoring, "CPU route");
                Assert(target.TachConnected && target.TachAssignment == 4, "tach routes");
                Assert(target.AverageValues && target.AutoStart && target.AutoConnect && target.TachoAdjust,
                       "remote settings routes");
                Assert(target.ResetCount == 1 && target.KillCount == 1, "reset and kill routes");

                server.Stop();
                Assert(connectionLost.Wait(TimeSpan.FromSeconds(4)), "stream loss should be detected");
                server.Start();
                Assert(reconnected.Wait(TimeSpan.FromSeconds(8)), "client should reconnect automatically");
                Assert(client.IsConnected, "reconnect should restore connection state");

                client.Disconnect();
                Assert(!client.IsConnected, "disconnect should clear connection state");
            }

            target.ProtocolVersion = 1;
            using (var oldClient = new PcueRemoteClient())
            {
                await ThrowsAsync<InvalidOperationException>(
                    () => oldClient.ConnectAsync("127.0.0.1", port, Token),
                    "too old");
            }
        }

        private static async Task Ok(Task<PcueApiActionResponse> task)
        {
            PcueApiActionResponse result = await task;
            Assert(result != null && result.ok, result?.error ?? "remote action returned no result");
        }

        private static async Task ThrowsAsync<T>(Func<Task> action, string messagePart) where T : Exception
        {
            try { await action(); }
            catch (T ex)
            {
                Assert(ex.Message.IndexOf(messagePart, StringComparison.OrdinalIgnoreCase) >= 0,
                       "exception should contain '" + messagePart + "' but was '" + ex.Message + "'");
                return;
            }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class FakeTarget : IRemoteControlTarget
        {
            public int ProtocolVersion { get; set; } = PcueRemoteClient.MinimumProtocolVersion;
            public string FanMode { get; private set; }
            public int[] Setpoints { get; private set; } = new int[6];
            public bool CommanderOpen { get; private set; }
            public bool CpuMonitoring { get; private set; }
            public bool TachConnected { get; private set; }
            public int TachAssignment { get; private set; }
            public bool AverageValues { get; private set; }
            public bool AutoStart { get; private set; }
            public bool AutoConnect { get; private set; }
            public bool TachoAdjust { get; private set; }
            public int ResetCount { get; private set; }
            public int KillCount { get; private set; }

            public PcueStatusSnapshot GetStatus()
            {
                return new PcueStatusSnapshot
                {
                    app = "pCUE",
                    protocolVersion = ProtocolVersion,
                    version = "9.9.9",
                    machine = "REMOTE-TEST",
                    updatedUtc = DateTime.UtcNow.ToString("o"),
                    commander = new PcueCommanderStatus { connected = CommanderOpen, firmware = "0.9.212" },
                    cpu = new PcueCpuStatus
                    {
                        temperature = "42.0",
                        mhz = "5000",
                        load = "12.0",
                        monitoring = CpuMonitoring,
                        temperatureStats = Metric(42),
                        mhzStats = Metric(5000),
                        loadStats = Metric(12),
                    },
                    fans = Enumerable.Range(1, 6).Select(i => new PcueFanStatus
                    {
                        fan = i,
                        rpm = i * 100,
                        mode = i == 2 ? "3pin" : "4pin",
                        setpoint = Setpoints[i - 1],
                        min = i * 90,
                        max = i * 110,
                        average = i * 100,
                    }).ToList(),
                    tachometer = new PcueTachometerStatus
                    {
                        connected = TachConnected,
                        rpm = TachConnected ? (double?)1234 : null,
                        batteryLow = false,
                        assignedFan = TachAssignment == 0 ? (int?)null : TachAssignment,
                    },
                    hold = new PcueHoldStatus
                    {
                        running = false,
                        status = "Idle",
                        display = "",
                        target = 0,
                        tachoAdjust = TachoAdjust,
                    },
                    settings = new PcueUiSettingsStatus
                    {
                        averageValues = AverageValues,
                        autoStart = AutoStart,
                        autoConnect = AutoConnect,
                    },
                };
            }

            private static PcueMetricStatus Metric(double value) => new PcueMetricStatus
            {
                current = value,
                min = value - 1,
                max = value + 1,
                average = value,
            };

            public string SetFanDuty(int fan, int duty) => null;
            public string SetFanRpm(int fan, int rpm) => null;
            public string SetFanMode(int fan, string mode) { FanMode = fan + ":" + mode; return null; }
            public string ApplyFanSetpoints(int[] values) { Setpoints = values; return null; }
            public string StartHold(int fan, int rpm) => null;
            public string StopHold() => null;
            public string SetCommanderOpen(bool open) { CommanderOpen = open; return null; }
            public string SetCpuMonitoring(bool on) { CpuMonitoring = on; return null; }
            public string SetTachConnected(bool connected) { TachConnected = connected; return null; }
            public string SetTachAssignment(int fan) { TachAssignment = fan; return null; }
            public string ResetStats() { ResetCount++; return null; }
            public string SetAverageValues(bool on) { AverageValues = on; return null; }
            public string SetAutoStart(bool on) { AutoStart = on; return null; }
            public string SetAutoConnect(bool on) { AutoConnect = on; return null; }
            public string SetTachoAdjust(bool on) { TachoAdjust = on; return null; }
            public string KillIcue() { KillCount++; return null; }
            public object GetHoldConfig() => new { };
            public string SetHoldConfig(Func<string, double?> get) => null;
            public byte[] CaptureScreenshot(string window) => null;
        }
    }
}

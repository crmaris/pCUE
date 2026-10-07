using Pcue.Cooling;
using Pcue.Linux;
using pCUE;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

static class Tests
{
    static int checks;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    static void Reject(Action action, string message) { try { action(); } catch (Exception) { checks++; return; } throw new Exception(message); }
    public static async Task<int> Main(string[] args)
    {
        string scratch = Path.Combine(Path.GetFullPath(args[0]), "test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        try
        {
            TestRecovery(scratch); TestRecoveryMarker(scratch); TestSensors(scratch); TestSerialization();
            using (var mutex = new Mutex(false, @"Global\CorsairLinkReadWriteGuardMutex")) { Check(mutex.WaitOne(1000), "Shared controller mutex unsupported on this platform"); mutex.ReleaseMutex(); }
            using (var ownership = BenchTachometerOwnership.Acquire()) Check(ownership != null, "Shared tachometer ownership unsupported");
            if (OperatingSystem.IsLinux()) TestPackageLock(scratch);
            await TestRemote();
            Console.WriteLine($"Linux tests passed: {checks} checks; no USB hardware opened."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(scratch, true); }
    }
    static void TestRecoveryMarker(string scratch)
    {
        string folder = Path.Combine(scratch, "managed-state"), marker = Path.Combine(scratch, "recovery-marker");
        var device = new CoolingUsbInfo { Id = "corext:managed", Kind = "corext", Outputs = 2 };
        string record = Path.Combine(folder, "cooling-recovery.json");
        var fake = new FakeController(() => File.Exists(record) && File.Exists(marker));
        using var session = new NativeSession(folder, _ => fake, marker);
        session.Connect(device); session.Apply(new[] { 50, -1 }, new[] { false, false });
        Check(File.Exists(marker), "Scheduled updater was not blocked by pending recovery");
        Reject(() => new NativeSession(Path.Combine(scratch, "another-state"), _ => fake, marker), "Changing XDG state directory bypassed pending recovery");
        fake.FailRestore = true; Reject(() => session.Restore(new[] { device }), "Failed marker restore accepted");
        Check(File.Exists(marker) && File.Exists(record), "Failed restore removed persistent updater guard");
        fake.FailRestore = false; session.Restore(new[] { device });
        Check(!File.Exists(marker) && !File.Exists(record), "Successful restore did not release persistent updater guard");
    }
    static void TestPackageLock(string scratch)
    {
        string path = Path.Combine(scratch, "package.lock"); File.WriteAllText(path, "");
        string TryLock()
        {
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/python3") { RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import fcntl,sys\nf=open(sys.argv[1],'rb')\ntry:\n fcntl.flock(f,fcntl.LOCK_EX|fcntl.LOCK_NB); print('free')\nexcept BlockingIOError: print('busy')");
            start.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(start); string output = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit(); return output;
        }
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) Check(TryLock() == "busy", "Scheduled updater lock did not detect running .NET application");
        Check(TryLock() == "free", "Scheduled updater lock remained held after exit");
    }
    static void TestRecovery(string folder)
    {
        string recovery = Path.Combine(folder, "cooling-recovery.json");
        var device = new CoolingUsbInfo { Id = "corext:exact-device", Kind = "corext", Path = "/dev/hidraw9", Outputs = 2 };
        var fake = new FakeController(() => File.Exists(recovery));
        using var session = new NativeSession(folder, _ => fake);
        session.Connect(device);
        Reject(() => session.Apply(new[] { 10, -1 }, new[] { true, false }), "Pump floor accepted");
        Check(fake.Writes == 0 && !File.Exists(recovery), "Invalid request wrote recovery or hardware");
        session.Apply(new[] { 60, -1 }, new[] { false, false });
        Check(File.Exists(recovery) && fake.Powers.SequenceEqual(new[] { 60, 77 }), "First apply did not preserve unselected output");
        var original = File.ReadAllBytes(recovery);
        if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(recovery);
            if (mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite)) Check(true, "Recovery permissions");
            else if (Path.GetFullPath(recovery).StartsWith("/mnt/")) Console.WriteLine("Unix permission assertion unavailable on the Windows-mounted validation filesystem; native Linux state uses mode 0600.");
            else throw new Exception("Recovery was exposed to other users");
        }
        session.Apply(new[] { 65, -1 }, new[] { false, false });
        Check(original.SequenceEqual(File.ReadAllBytes(recovery)), "Baseline replaced by second apply");
        Reject(() => session.Apply(new[] { -1, 70 }, new[] { false, false }), "Selection changed without restore");
        Reject(() => session.Connect(device), "Reconnect bypassed pending recovery");
        Reject(() => session.Restore(new[] { new CoolingUsbInfo { Id = "different", Kind = "corext" } }), "Recovery targeted another device");
        Check(File.Exists(recovery), "Missing-device recovery was discarded");
        fake.FailRestore = true;
        Reject(() => session.Restore(new[] { device }), "Failed restore accepted");
        Check(File.Exists(recovery) && !session.Connected, "Failed restore lost recovery or kept dead session");
        fake.FailRestore = false; fake.Powers[1] = 88;
        session.Restore(new[] { device });
        Check(!File.Exists(recovery) && fake.Powers.SequenceEqual(new[] { 33, 88 }), "Successful restore failed selected/unselected preservation");
        session.Connect(device); fake.FailApply = true;
        Reject(() => session.Apply(new[] { 90, -1 }, new[] { false, false }), "Write failure accepted");
        Check(File.Exists(recovery) && !session.Connected, "Partial write failed to retain recovery");
        fake.FailApply = false; session.Restore(new[] { device });
        Check(!File.Exists(recovery), "Retry restore did not clear recovery");
        Directory.CreateDirectory(recovery); session.Connect(device);
        Reject(() => session.Apply(new[] { 80, -1 }, new[] { false, false }), "A failed durable save permitted writes");
        Directory.Delete(recovery);
        Check(!File.Exists(recovery + ".new"), "Temporary recovery leaked after failed save");
        foreach (int length in new[] { 64, 65, 96, 97, 1024, 1025 }) Check(CoreProtocol.Request(length, new byte[] { 6 }, Array.Empty<byte>())[1] == 8, "CORE framing changed");
        Reject(() => CoreProtocol.HidReply(new byte[] { 0, 0, 6, 0 }, 4, 8), "Unrelated CORE response accepted");
    }
    static void TestSerialization()
    {
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { RecursionLimit = 8 };
        var values = serializer.DeserializeObject("{\"fan\":2,\"values\":[1,2,3],\"enabled\":true}") as Dictionary<string, object>;
        Check((int)values["fan"] == 2 && values["values"] is object[] a && a.Length == 3, "Legacy body parsing changed");
        var snapshot = serializer.Deserialize<PcueStatusSnapshot>(serializer.Serialize(new PcueStatusSnapshot { app = "pCUE", protocolVersion = 2, fans = new() { new() { fan = 2, rpm = 1400 } } }));
        Check(snapshot.fans[0].rpm == 1400 && snapshot.protocolVersion == 2, "Protocol JSON parity");
        Reject(() => serializer.DeserializeObject("{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":{\"i\":0}}}}}}}}}"), "Recursion limit ignored");
        Check(new Version("1.10.0") > new Version("1.9.9"), "Numeric update version ordering");
    }
    static void TestSensors(string scratch)
    {
        string folder = Path.Combine(scratch, "sensors"); Directory.CreateDirectory(Path.Combine(folder, "proc"));
        Directory.CreateDirectory(Path.Combine(folder, "sys/class/hwmon/hwmon0"));
        File.WriteAllText(Path.Combine(folder, "proc/stat"), "cpu  100 0 100 800 0 0 0 0\n");
        File.WriteAllText(Path.Combine(folder, "proc/cpuinfo"), "cpu MHz : 4000.0\ncpu MHz : 3000.0\n");
        File.WriteAllText(Path.Combine(folder, "sys/class/hwmon/hwmon0/name"), "coretemp\n");
        File.WriteAllText(Path.Combine(folder, "sys/class/hwmon/hwmon0/temp1_input"), "48200\n");
        var sensor = new LinuxSensors(folder); var first = sensor.Read();
        Check(first.temperature == "48.2 °C" && first.mhz == "3500 MHz" && first.load == "N/A", "Linux first telemetry sample");
        File.WriteAllText(Path.Combine(folder, "proc/stat"), "cpu  110 0 110 880 0 0 0 0\n");
        Check(sensor.Read().load == "20.0%", "CPU delta load incorrect");
        Check(new LinuxSensors(Path.Combine(scratch, "missing")).Read().temperature == "N/A", "Missing temperature shown as zero");
    }
    static async Task TestRemote()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var fake = new FakeTarget(); using var server = new RemoteControlServer(fake, $"http://127.0.0.1:{port}/", "test-token"); server.Start();
        using var http = new HttpClient();
        var denied = await http.GetAsync($"http://127.0.0.1:{port}/status"); Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Wrong-token request accepted");
        using var client = new PcueRemoteClient(); var snapshot = await client.ConnectAsync("127.0.0.1", port, "test-token");
        Check(snapshot.protocolVersion == 2 && client.IsConnected, "Protocol-v2 connection failed");
        var result = await client.SetFanDutyAsync(2, 45);
        Check(result.ok && fake.LastFan == 2 && fake.Calls == 1, "Single selected remote duty touched another fan");
        Check((await client.SetFanRpmAsync(2, 1200)).ok && fake.Calls == 2, "Single remote RPM endpoint");
        Check((await client.StartHoldAsync(2, 1400)).ok && fake.LastFan == 2, "Remote hold endpoint");
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/fan/duty?fan=1&value=90"); request.Headers.Add("X-pCUE-Token", "test-token"); request.Headers.Add("Origin", "https://evil.example");
        Check((await http.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Cross-origin mutation accepted");
        client.Disconnect(); Check(!client.IsConnected, "Disconnect state wrong");
    }
    sealed class FakeController(Func<bool> saved) : INativeCoolingController
    {
        public int[] Powers = { 33, 77 }; public int Writes; public bool FailRestore, FailApply;
        public int Count => 2; public bool HasExt => false;
        public CoreState Read() => new() { Connected = new[] { true, true }, Rpm = new int?[] { 1000, 2000 }, Modes = new[] { 0, 0 }, Powers = (int[])Powers.Clone() };
        public CoreBaseline Capture(bool[] selected) => new() { Selected = (bool[])selected.Clone(), Modes = new[] { 0, 0 }, Powers = (int[])Powers.Clone() };
        public void Apply(int[] desired, int[] previous, Func<bool> allowed)
        {
            Check(saved(), "Hardware write preceded saved baseline"); Check(allowed(), "Inactive command accepted"); Writes++;
            for (int i = 0; i < 2; i++) if (desired[i] >= 0) Powers[i] = desired[i];
            if (FailApply) throw new IOException("Partial apply failure");
        }
        public void Restore(CoreBaseline b) { if (FailRestore) throw new IOException("Restore failed"); for (int i = 0; i < 2; i++) if (b.Selected[i]) Powers[i] = b.Powers[i]; }
        public void Dispose() { }
    }
    sealed class FakeTarget : IRemoteControlTarget
    {
        public int Calls, LastFan;
        public PcueStatusSnapshot GetStatus() => new() { app = "pCUE", protocolVersion = 2, fans = Enumerable.Range(1, 6).Select(n => new PcueFanStatus { fan = n }).ToList() };
        public string SetFanDuty(int fan, int duty) { Calls++; LastFan = fan; return null; }
        public string SetFanRpm(int fan, int rpm) => SetFanDuty(fan, rpm);
        public string StartHold(int fan, int rpm) => SetFanDuty(fan, rpm);
        public string SetFanMode(int fan, string mode) => null;
        public string ApplyFanSetpoints(int[] values) => throw new Exception("Selected control used all-fan endpoint");
        public string StopHold() => null; public string SetCommanderOpen(bool open) => null;
        public string SetCpuMonitoring(bool on) => null; public string SetTachConnected(bool on) => null;
        public string SetTachAssignment(int fan) => null; public string ResetStats() => null;
        public string SetAverageValues(bool on) => null; public string SetAutoStart(bool on) => null;
        public string SetAutoConnect(bool on) => null; public string SetTachoAdjust(bool on) => null;
        public string KillIcue() => null; public object GetHoldConfig() => new { tolerance = 20 };
        public string SetHoldConfig(Func<string, double?> get) => null;
        public byte[] CaptureScreenshot(string window) => Array.Empty<byte>();
    }
}

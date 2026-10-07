using HidSharp;
using pCUE;

namespace Pcue.Linux;

public sealed class LinuxTarget : IRemoteControlTarget, IDisposable
{
    readonly object gate = new();
    readonly CommanderProDevice commander = new();
    readonly HidTachometer tach = new();
    readonly LinuxSensors sensors = new();
    readonly RunStats[] stats = Enumerable.Range(0, 6).Select(_ => new RunStats()).ToArray();
    readonly int[] values = new int[6];
    FanRpmHoldController hold;
    FanHoldConfig config = new();
    volatile int holdFan;
    int tachFan;
    bool cpuMonitoring = true;
    public bool[] RpmUnavailable { get; } = Enumerable.Repeat(true, 6).ToArray();
    public bool IsConnected => commander.IsConnected;
    public bool Holding => hold?.IsRunning == true;
    string Command(Action action)
    {
        try { lock (gate) action(); return null; }
        catch (Exception ex) { return ex.Message; }
    }
    void Ready(int fan)
    {
        if (fan < 1 || fan > 6) throw new ArgumentException("Choose fan 1–6.");
        if (!commander.IsConnected) throw new IOException("Connect Commander PRO first.");
        if (Holding) throw new IOException("Stop RPM hold before manual control.");
    }
    public string SetCommanderOpen(bool open) => Command(() =>
    {
        if (Holding) throw new IOException("Stop RPM hold before disconnecting.");
        if (open)
        {
            if (DeviceList.Local.GetHidDevices(0x1b1c, 0x0c10).Count() != 1) throw new IOException("Exactly one Commander PRO is required. Check USB access and the udev rule.");
            commander.Connect();
            for (int i = 0; i < 6; i++) values[i] = commander.TryReadFanPower(i) ?? 50;
        }
        else commander.Disconnect();
    });
    public string SetFanDuty(int fan, int duty) => Command(() =>
    {
        Ready(fan);
        if (duty < 0 || duty > 100) throw new ArgumentException("Power must be a whole percentage from 0 to 100.");
        if (!commander.WriteFanPower(fan - 1, duty)) throw new IOException("Commander rejected the power command.");
        values[fan - 1] = duty;
    });
    public string SetFanRpm(int fan, int rpm) => Command(() =>
    {
        Ready(fan);
        if (rpm <= 100 || rpm > 10000) throw new ArgumentException("Fixed RPM must be between 101 and 10000.");
        if (commander.ReadAcquisitionDriveMode(fan - 1) != "pwm") throw new IOException("Fixed RPM requires a detected four-pin PWM fan.");
        if (!commander.WriteFanSpeed(fan - 1, rpm)) throw new IOException("Commander rejected the RPM command.");
        values[fan - 1] = rpm;
    });
    public string SetFanMode(int fan, string mode) => Command(() =>
    {
        Ready(fan);
        var type = mode switch { "auto" => FanDetectionType.Auto, "3pin" => FanDetectionType.ThreePin, "4pin" => FanDetectionType.FourPin, "disconnect" => FanDetectionType.Disconnected, _ => throw new ArgumentException("Unknown fan mode.") };
        if (!commander.WriteFanDetectionType(fan - 1, type)) throw new IOException("Commander rejected the mode command.");
    });
    public string ApplyFanSetpoints(int[] wanted) => Command(() =>
    {
        if (wanted?.Length != 6 || wanted.Any(v => v < 0 || v > 10000)) throw new ArgumentException("Six valid fan setpoints are required.");
        for (int i = 0; i < 6; i++)
        {
            Ready(i + 1);
            if (wanted[i] > 100 && commander.ReadAcquisitionDriveMode(i) != "pwm") throw new IOException("Fixed RPM requires four-pin PWM on every selected channel.");
        }
        for (int i = 0; i < 6; i++) { string error = wanted[i] <= 100 ? SetFanDuty(i + 1, wanted[i]) : SetFanRpm(i + 1, wanted[i]); if (error != null) throw new IOException(error); }
    });
    public string StartHold(int fan, int rpm) => Command(() =>
    {
        Ready(fan);
        if (rpm <= 100 || rpm > 10000) throw new ArgumentException("Choose a target from 101 to 10000 RPM.");
        if (tachFan != fan || !tach.IsConnected || !tach.ReadRpm().HasValue) throw new IOException("Connect a fresh bench tachometer and assign it to this fan before RPM hold.");
        holdFan = fan;
        hold = new FanRpmHoldController(duty =>
        {
            if (!commander.WriteFanPower(holdFan - 1, duty)) throw new IOException("Commander rejected the hold command.");
        }, tach.ReadRpm, () => commander.IsConnected && tach.IsConnected);
        var run = config.CopyValidated(); run.TargetRpm = rpm;
        int? power = commander.TryReadFanPower(fan - 1);
        if (power.HasValue) { run.StartDuty = power.Value; run.StartDutyIsCurrent = true; }
        _ = hold.StartAsync(run);
    });
    // Stop cannot hold 'gate': the shared loop waits for any in-flight actuator write.
    public string StopHold() { hold?.Stop(); return null; }
    public string SetTachConnected(bool connected) => Command(() =>
    {
        if (Holding) throw new IOException("Stop RPM hold before changing the tachometer.");
        if (connected) tach.Connect(); else tach.Disconnect();
    });
    public string SetTachAssignment(int fan) => Command(() =>
    {
        if (Holding || fan < 0 || fan > 6) throw new ArgumentException("Stop hold and choose None or fan 1–6.");
        tachFan = fan;
    });
    public string SetCpuMonitoring(bool on) { cpuMonitoring = on; return null; }
    public string ResetStats() => Command(() => { foreach (var s in stats) s.Reset(); });
    public string SetAverageValues(bool on) => "Linux displays current RPM and its separate Min/Max/Avg columns.";
    public string SetAutoStart(bool on) => "Use the Linux desktop's Startup Applications with pcue-linux; automatic hardware writes are never enabled.";
    public string SetAutoConnect(bool on) => "Linux requires an explicit Connect for USB controllers.";
    public string SetTachoAdjust(bool on) => on ? null : StopHold();
    public string KillIcue() => "Windows iCUE process management is unavailable on Linux.";
    public object GetHoldConfig() => config;
    public string SetHoldConfig(Func<string, double?> get) => Command(() =>
    {
        if (Holding) throw new IOException("Stop hold before changing its configuration.");
        var next = config.CopyValidated();
        if (get("tolerance") is double tolerance) next.RpmTolerance = tolerance;
        if (get("minDuty") is double min) next.MinDuty = CheckedWhole(min);
        if (get("maxDuty") is double max) next.MaxDuty = CheckedWhole(max);
        if (get("settle") is double settle) next.SettleDelayMs = CheckedWhole(settle);
        if (get("dither") is double dither) next.DitherEnabled = dither != 0;
        config = next.CopyValidated();
    });
    static int CheckedWhole(double value) => double.IsFinite(value) && value == Math.Truncate(value) && value >= 0 && value <= int.MaxValue ? (int)value : throw new ArgumentException("A finite whole number is required.");
    public byte[] CaptureScreenshot(string window) => throw new NotSupportedException("Use the Linux desktop screenshot tool.");
    public PcueStatusSnapshot GetStatus()
    {
        lock (gate)
        {
            var fans = new List<PcueFanStatus>();
            for (int i = 0; i < 6; i++)
            {
                double? rpm = commander.IsConnected ? commander.ReadAcquisitionRpm(i).value : null;
                if (tachFan == i + 1) rpm = tach.ReadRpm();
                RpmUnavailable[i] = !rpm.HasValue;
                if (rpm.HasValue) stats[i].Add(rpm.Value);
                var mode = commander.IsConnected ? commander.ReadAcquisitionDriveMode(i) : null;
                fans.Add(new() { fan = i + 1, rpm = (int)(rpm ?? 0), mode = mode == "pwm" ? "4pin" : mode == "dc-percent" ? "3pin" : "unknown", setpoint = values[i], min = stats[i].Min, max = stats[i].Max, average = stats[i].Average });
            }
            return new()
            {
                app = "pCUE", protocolVersion = 2, machine = Environment.MachineName, version = Program.Version,
                updatedUtc = DateTime.UtcNow.ToString("O"), commander = new() { connected = commander.IsConnected, firmware = commander.FirmwareVersion },
                fans = fans, cpu = cpuMonitoring ? sensors.Read() : new() { temperature = "N/A", mhz = "N/A", load = "N/A" },
                tachometer = new() { connected = tach.IsConnected, rpm = tach.ReadRpm(), batteryLow = tach.BatteryLow, assignedFan = tachFan == 0 ? null : tachFan },
                hold = new() { running = Holding, status = hold?.Status.ToString() ?? "Idle", fan = Holding ? holdFan : null, duty = Holding ? hold.CurrentDuty : null, dutySource = Holding ? "tracked" : "unknown" },
                settings = new()
            };
        }
    }
    public void Dispose() { StopHold(); tach.Dispose(); commander.Dispose(); }
}
public sealed class RunStats
{
    double sum; long count;
    public double Min { get; private set; }
    public double Max { get; private set; }
    public double Average => count == 0 ? 0 : sum / count;
    public void Add(double n) { if (n <= 0 || !double.IsFinite(n)) return; Min = count == 0 ? n : Math.Min(Min, n); Max = Math.Max(Max, n); sum += n; count++; }
    public void Reset() { sum = 0; count = 0; Min = Max = 0; }
}

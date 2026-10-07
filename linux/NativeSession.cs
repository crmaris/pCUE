using Pcue.Cooling;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Pcue.Linux;

public sealed class RecoveryRecord
{
    public int Format { get; set; } = 1;
    public string DeviceId { get; set; }
    public string Kind { get; set; }
    public CoreBaseline Baseline { get; set; }
}
public sealed class NativeSession : IDisposable
{
    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    readonly string recoveryPath;
    readonly string recoveryMarker;
    readonly Func<CoolingUsbInfo, INativeCoolingController> factory;
    INativeCoolingController controller;
    CoolingUsbInfo device;
    int[] previous;
    public bool RecoveryRequired => File.Exists(recoveryPath);
    public bool Connected => controller != null;
    public NativeSession(string folder, Func<CoolingUsbInfo, INativeCoolingController> factory = null, string recoveryMarker = null)
    {
        Directory.CreateDirectory(folder);
        recoveryPath = Path.Combine(folder, "cooling-recovery.json");
        this.recoveryMarker = recoveryMarker;
        this.factory = factory ?? CoolingUsbDiscovery.Open;
        if (recoveryMarker != null && File.Exists(recoveryMarker) && File.ReadAllText(recoveryMarker) != recoveryPath) throw new IOException("Recovery is pending in another state directory. Restore that session first.");
        if (RecoveryRequired) MarkRecovery();
        else if (recoveryMarker != null && File.Exists(recoveryMarker)) File.Delete(recoveryMarker);
    }
    public CoreState Connect(CoolingUsbInfo info)
    {
        if (RecoveryRequired) throw new IOException("Saved recovery is pending. Use Stop / restore first.");
        Dispose(); device = info; controller = factory(info);
        try { var state = controller.Read(); previous = Enumerable.Repeat(-1, controller.Count).ToArray(); return state; }
        catch { Dispose(); throw; }
    }
    public CoreState Read() => controller?.Read();
    public void Apply(int[] desired, bool[] pumps)
    {
        if (controller == null) throw new IOException("Connect a controller first.");
        if (desired == null || pumps == null || desired.Length != controller.Count || pumps.Length != desired.Length || !desired.Any(v => v >= 0)) throw new ArgumentException("Select outputs explicitly.");
        for (int i = 0; i < desired.Length; i++)
            if (desired[i] < -1 || desired[i] > 100 || (pumps[i] && desired[i] >= 0 && desired[i] < 25)) throw new ArgumentException("Use whole percentages 0–100; pumps require at least 25%.");
        bool[] mask = desired.Select(v => v >= 0).ToArray();
        if (!RecoveryRequired)
        {
            var record = new RecoveryRecord { DeviceId = device.Id, Kind = device.Kind, Baseline = controller.Capture(mask) };
            MarkRecovery();
            try { Save(record); }
            catch { if (!RecoveryRequired && recoveryMarker != null && File.Exists(recoveryMarker)) File.Delete(recoveryMarker); throw; }
        }
        else if (!Load().Baseline.Selected.SequenceEqual(mask)) throw new IOException("Restore before changing the selected outputs.");
        FlushDirectory();
        try { controller.Apply(desired, previous, () => Connected); previous = (int[])desired.Clone(); }
        catch { Dispose(); throw; } // Keep the recovery record; reopen the exact identity for restore.
    }
    void MarkRecovery()
    {
        if (recoveryMarker == null) return;
        if (File.Exists(recoveryMarker)) { FlushDirectory(Path.GetDirectoryName(recoveryMarker)); return; }
        using var file = new FileStream(recoveryMarker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(recoveryMarker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var bytes = System.Text.Encoding.UTF8.GetBytes(recoveryPath); file.Write(bytes); file.Flush(true);
        FlushDirectory(Path.GetDirectoryName(recoveryMarker));
    }
    void Save(RecoveryRecord record)
    {
        string temporary = recoveryPath + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(file, record, Json); file.Flush(true);
            }
            File.Move(temporary, recoveryPath, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] static extern int fsync(int fd);
    [DllImport("libc")] static extern int close(int fd);
    void FlushDirectory(string directory = null)
    {
        if (!OperatingSystem.IsLinux()) return;
        int fd = open(directory ?? Path.GetDirectoryName(recoveryPath), 0x10000); // O_DIRECTORY | O_RDONLY
        if (fd < 0) throw new IOException("Could not open the recovery directory for durable storage.");
        try { if (fsync(fd) != 0) throw new IOException("Could not commit saved recovery. No control write was started."); }
        finally { close(fd); }
    }
    RecoveryRecord Load()
    {
        var r = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(recoveryPath), Json);
        if (r?.Format != 1 || string.IsNullOrEmpty(r.DeviceId) || r.Baseline?.Selected == null) throw new IOException("Saved recovery is invalid; it has been retained.");
        return r;
    }
    public void Restore(CoolingUsbInfo[] available)
    {
        if (!RecoveryRequired) { Dispose(); return; }
        var saved = Load();
        var exact = available.SingleOrDefault(d => d.Id == saved.DeviceId && d.Kind == saved.Kind) ?? throw new IOException("The saved device is unavailable. Reconnect it to the same USB port.");
        Dispose(); controller = factory(exact);
        try { controller.Restore(saved.Baseline); File.Delete(recoveryPath); FlushDirectory(); if (recoveryMarker != null && File.Exists(recoveryMarker)) File.Delete(recoveryMarker); }
        finally { Dispose(); }
    }
    public void Dispose() { var old = controller; controller = null; try { old?.Dispose(); } catch { } }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Pcue.Cooling;

public static class BeQuietBackendTests
{
    static int checks;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    static void Reject(Action action) { try { action(); } catch (IOException) { checks++; return; } catch (InvalidDataException) { checks++; return; } catch (TimeoutException) { checks++; return; } throw new Exception("Unsafe IO operation accepted."); }
    static byte[] Report(byte[] wire) { ushort crc = BeQuietIoProtocol.Crc(wire, 62); wire[62] = (byte)crc; wire[63] = (byte)(crc >> 8); var r = new byte[65]; Array.Copy(wire, 0, r, 1, 64); return r; }
    static byte[] Sample(int channel, int value, byte session = 2, byte child = 3) { var b = new byte[64]; b[0] = 16; b[2] = session; b[5] = b[6] = 5; b[9] = child; b[12] = 0x30; b[13] = 2; b[14] = (byte)channel; b[15] = (byte)value; b[16] = (byte)(value >> 8); return Report(b); }
    public static void Run()
    {
        Check(BeQuietIoProtocol.Crc(System.Text.Encoding.ASCII.GetBytes("123456789"), 9) == 0x4b37, "CRC16 golden vector.");
        Check(CoolingUsbDiscovery.Describe(0x373f, 0x0010, "hid&mi_00", 65, 65).Outputs == 2, "IO discovery inventory.");
        foreach (var p in new[] { "hid&mi_01", "hid&mi_001", "hid" }) Check(CoolingUsbDiscovery.Describe(0x373f, 0x0010, p, 65, 65) == null, "Other IO interface enabled.");
        Check(CoolingUsbDiscovery.Describe(0x373f, 0x0016, "hid&mi_00", 65, 65) == null && CoolingUsbDiscovery.Describe(0x373f, 0x0010, "hid&mi_00", 64, 65) == null, "LCD/wrong descriptor enabled.");
        BeQuietIoReading reading; DateTime utc = DateTime.UtcNow; long ticks = Stopwatch.GetTimestamp();
        for (int i = 0; i < 4; i++) Check(BeQuietIoProtocol.TryDecode(Sample(i, i == 0 ? 325 : i == 3 ? 0 : 1000), 65, utc, out reading) && reading.Channel == i, "Raw channel lost or error channel called a fan.");
        var bad = Sample(1, 1000); bad[4] = 1; Check(!BeQuietIoProtocol.TryDecode(bad, 65, utc, out reading), "Corrupt CRC accepted.");
        var padding = Sample(1, 1000).Skip(1).ToArray(); padding[18] = 1; Check(!BeQuietIoProtocol.TryDecode(Report(padding), 65, utc, out reading), "Nonzero padding accepted.");
        var zero = Sample(2, 0); var state = new BeQuietIoState(); Check(state.Accept(zero, 65, utc, ticks) && state.Fresh(ticks)[0].RawValue == 0, "Zero RPM became unavailable.");
        Check(state.Fresh(ticks + 4 * Stopwatch.Frequency).Length == 0, "Stale RPM retained.");
        var other = Sample(1, 1000, 4); Check(!state.Accept(other, 65, utc, ticks) && state.Fresh(ticks).Length == 0 && !state.Accept(zero, 65, utc, ticks), "Session ambiguity accepted."); state.Clear(); Check(state.Fresh(ticks).Length == 0, "EOF retains feedback.");
        using (var wire = new Wire()) using (var client = new BeQuietIoDirectClient(wire, "ABCDEF123456", null))
        {
            Check(client.CoolerSerial == "ABCDEF123456" && client.FirmwareVersion == "0.0.37" && wire.SetCount == 0, "Native connect applies setpoints or invents firmware decimal.");
            client.Heartbeat(); client.Status(); Check(client.ReadDuty(1) == 100 && client.ReadDuty(2) == 48 && client.ReadCoolingMode() == 0, "Bridge/child acknowledgements or PWM readback.");
            Reject(delegate { client.SetDuty(2, 70, delegate { return false; }); }); Check(wire.SetCount == 0 && client.Error == null, "Guard cancellation wrote or poisoned restoration transport.");
            client.SetDuty(2, 70, delegate { return true; }); client.SetSoftwareControl(true); Check(wire.SetCount == 2 && wire.Mode == 1 && wire.Fan == 70, "Native acknowledged writes.");
            wire.RejectSet = true; Reject(delegate { client.SetDuty(2, 80); }); Check(wire.Fan == 70, "Rejected write accepted.");
        }
        foreach (int fault in new[] { 1, 2, 3, 4 }) Reject(delegate { using (var wire = new Wire { OpenFault = fault }) using (var client = new BeQuietIoDirectClient(wire, "ABCDEF123456", null)) { } });
        Reject(delegate { using (var wire = new Wire()) using (var client = new BeQuietIoDirectClient(wire, "000000000001", null)) { } });
        using (var wire = new Wire()) using (var client = new BeQuietIoDirectClient(wire, null, null)) { wire.MissingAck = true; Reject(delegate { client.ReadDuty(2); }); Check(client.Error != null && client.Fresh.Length == 0, "Child reply without outer ACK accepted or stale samples retained after timeout."); }
        var fake = new Policy(); using (var controller = new BeQuietIoController(fake, false))
        {
            Reject(delegate { controller.Capture(new[] { false, true }); }); Check(fake.Writes.Count == 0, "Fan-only claim accepted.");
            var baseline = controller.Capture(new[] { true, true }); Check(baseline.GlobalMode == 0 && baseline.ChildSerial == fake.CoolerSerial && fake.Writes.Count == 0, "Capture changed IO configuration.");
            controller.Apply(new[] { 50, 67 }, new[] { -1, -1 }, delegate { return true; }); Check(fake.Writes.SequenceEqual(new[] { "pump=100", "fan=67", "mode=1", "pump=50" }), "Pump-first/mode sequence missing.");
            fake.Writes.Clear(); controller.Apply(new[] { 50, 67 }, new[] { 50, 67 }, delegate { return true; }); Check(fake.Writes.Count == 0, "Unchanged profile tick kicks pump.");
            fake.Fan = 68; Reject(delegate { controller.Apply(new[] { 50, 67 }, new[] { 50, 67 }, delegate { return true; }); }); Check(fake.Writes.Count == 0, "Competing settings overwritten.");
            controller.Restore(baseline); Check(fake.Pump == 100 && fake.Fan == 48 && fake.Mode == 0, "Recovery did not restore bank/mode with full pump.");
            fake.Writes.Clear(); baseline.Modes[0] = 255; Reject(delegate { controller.Restore(baseline); }); Check(fake.Writes.Count == 0, "Corrupt baseline wrote hardware."); baseline.Modes[0] = 2;
            baseline.ChildSerial = "000000000001"; Reject(delegate { controller.Restore(baseline); }); Check(fake.Writes.Count == 0, "Replacement cooler accepted recovery."); baseline.ChildSerial = fake.CoolerSerial;
            Reject(delegate { controller.Apply(new[] { 24, 60 }, new[] { -1, -1 }, delegate { return true; }); });
            Reject(delegate { controller.Apply(new[] { 50, 60 }, new[] { -1, -1 }, delegate { return fake.Writes.Count == 0; }); }); Check(fake.Writes.SequenceEqual(new[] { "pump=100" }), "Late stop allowed subsequent writes."); controller.Restore(baseline);
        }
        foreach (int fault in new[] { 0, 1, 2 })
        {
            fake = new Policy(); using (var controller = new BeQuietIoController(fake, false))
            {
                var baseline = controller.Capture(new[] { true, true }); bool alive = true; controller.Apply(new[] { 75, 67 }, new[] { -1, -1 }, delegate { return alive; }); fake.Writes.Clear();
                if (fault == 0) alive = false; else if (fault == 1) fake.Stale = true; else fake.Condition = 1;
                controller.Tick(); Check(fake.Pump == 100 && fake.Fan == 100 && fake.Mode == 1 && fake.Beats == 1, "Lease/feedback/fault trip failed full-speed park.");
                Check(!controller.Read().Writable, "Trip did not latch read-only."); Reject(delegate { controller.Apply(new[] { 75, 67 }, new[] { -1, -1 }, delegate { return true; }); });
                controller.Restore(baseline); Check(fake.Fan == 48 && fake.Mode == 0 && fake.Pump == 100, "Recovery refused stale telemetry or left fault park owned.");
            }
        }
        fake = new Policy { Stale = true }; using (var controller = new BeQuietIoController(fake, false)) { Check(!controller.Read().Writable, "Missing sensor marked controllable."); Reject(delegate { controller.Capture(new[] { true, true }); }); }
        Console.WriteLine("PASS: " + checks + " shared be quiet protocol/policy checks (simulated; no USB hardware).");
    }
    sealed class Policy : IBeQuietControlClient
    {
        public int Pump = 100, Fan = 48, Mode, Condition, Beats; public bool Stale; public List<string> Writes = new List<string>();
        public string Error { get { return null; } } public string CoolerSerial { get { return "ABCDEF123456"; } } public string FirmwareVersion { get { return "0.0.37"; } }
        public BeQuietIoReading[] Fresh { get { return Stale ? new BeQuietIoReading[0] : new[] { new BeQuietIoReading { Channel = 0, RawValue = 325 }, new BeQuietIoReading { Channel = 1, RawValue = 2900 }, new BeQuietIoReading { Channel = 2, RawValue = 1000 }, new BeQuietIoReading { Channel = 3, RawValue = Condition } }; } }
        public void Heartbeat() { Beats++; } public void Status() { } public int ReadDuty(int channel) { return channel == 1 ? Pump : Fan; } public int ReadCoolingMode() { return Mode; }
        public void SetDuty(int channel, int value, Func<bool> permitted = null) { if (permitted != null && !permitted()) throw new BeQuietWriteCancelledException(); if (channel == 1) Pump = value; else Fan = value; Writes.Add((channel == 1 ? "pump=" : "fan=") + value); }
        public void SetSoftwareControl(bool enabled, Func<bool> permitted = null) { if (permitted != null && !permitted()) throw new BeQuietWriteCancelledException(); Mode = enabled ? 1 : 0; Writes.Add("mode=" + Mode); } public void Dispose() { }
    }
    sealed class Wire : IBeQuietReportTransport
    {
        readonly BlockingCollection<byte[]> input = new BlockingCollection<byte[]>(); public int Fan = 48, Pump = 100, Mode, SetCount, OpenFault; public bool RejectSet, MissingAck;
        public int ReadReport(byte[] buffer) { byte[] data; if (!input.TryTake(out data, -1)) return 0; Array.Copy(data, buffer, data.Length); return data.Length; }
        void Send(byte[] wire) { input.Add(Report(wire)); }
        public void WriteReport(byte[] report)
        {
            var b = report.Skip(1).ToArray(); Check(BeQuietIoProtocol.Crc(b, 62) == (ushort)(b[62] | b[63] << 8) && report[0] == 0, "OUT framing/CRC.");
            var ack = new byte[64]; ack[0] = 6; ack[2] = b[2]; ack[4] = b[4]; ack[5] = b[5]; ack[6] = 3;
            if (b[5] == 1 && b[6] == 1) { var open = (byte[])ack.Clone(); open[0] = 13; open[6] = 1; open[7] = 0x34; open[8] = 0x12; open[11] = 2; open[12] = OpenFault == 1 ? (byte)0 : (byte)1; open[13] = 5; Send(open); return; }
            if (b[5] == 1) { Send(ack); return; }
            var reply = new byte[64]; reply[0] = 13; reply[2] = b[2]; reply[5] = reply[6] = 5; reply[9] = b[9]; reply[11] = b[11]; reply[12] = b[12]; reply[13] = b[13];
            if (b[12] == 1 && b[13] == 1) { reply[0] = 20; reply[14] = 0x34; reply[15] = 0x12; reply[18] = 3; reply[19] = OpenFault == 2 ? (byte)0 : (byte)1; reply[20] = 5; }
            else if (b[12] == 3 && b[13] == 2) { reply[0] = 26; reply[14] = 12; Array.Copy(System.Text.Encoding.ASCII.GetBytes("ABCDEF123456"), 0, reply, 15, 12); }
            else if (b[12] == 3 && b[13] == 1) { reply[0] = 21; reply[14] = OpenFault == 3 ? (byte)4 : (byte)3; reply[15] = 0x80; reply[16] = reply[17] = 1; reply[18] = OpenFault == 4 ? (byte)0x38 : (byte)0x37; }
            else if (b[12] == 0x31 && b[13] == 3) { reply[0] = 14; reply[14] = (byte)(b[14] == 1 ? Pump : b[14] == 2 ? Fan : Mode); }
            else if (b[12] == 0x31 && b[13] == 4) { if (RejectSet) reply[14] = 1; else { SetCount++; if (b[14] == 1) Pump = b[15]; else if (b[14] == 2) Fan = b[15]; else Mode = b[15]; } }
            // Deliver child reply first: it cannot complete an RPC without its outer ACK.
            Send(reply); if (!MissingAck) Send(ack);
        }
        public void Dispose() { input.CompleteAdding(); }
    }
}

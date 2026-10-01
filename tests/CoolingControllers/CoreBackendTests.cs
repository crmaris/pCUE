using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Pcue.Cooling;

public static class CoreBackendTests
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static void Reject(Action action) { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, "Unsafe CORE operation was accepted."); }
    public static void Run()
    {
        foreach (int length in new[] { 64, 65, 96, 97, 1024, 1025 }) { var b = CoreProtocol.Request(length, new byte[] { 6, 0xf7 }, new byte[] { 1, 2 }); Check(b.Length == length && b[0] == 0 && b[1] == 8 && b[2] == 6 && b[3] == 0xf7 && b[4] == 1 && b[5] == 2, "CORE request framing."); }
        Reject(delegate { CoreProtocol.Request(32, new byte[] { 6 }, new byte[0]); });
        Check(CoreProtocol.HidReply(new byte[] { 0, 0, 8, 0, 4, 0 }, 6, 8).SequenceEqual(new byte[] { 0, 8, 0, 4, 0 }), "HidSharp report ID was not stripped exactly once.");
        Reject(delegate { CoreProtocol.HidReply(new byte[] { 0, 0, 6, 0 }, 4, 8); });
        Check(CoolingUsbDiscovery.Describe(0x1b1c, 0x0c1c, "hid&mi_00", 65, 65).Outputs == 7, "CORE EXT channel omitted.");
        Check(CoolingUsbDiscovery.Describe(0x1b1c, 0x0c2a, "hid&mi_00", 97, 97).Outputs == 6, "XT channels shifted by pump offset.");
        Check(CoolingUsbDiscovery.Describe(0x1b1c, 0x0c2a, "hid&mi_01", 65, 65) == null && CoolingUsbDiscovery.Describe(0x1b1c, 0x0c32, "hid", 65, 65) == null, "Unsupported interface/ST silently enabled.");
        foreach (bool ext in new[] { false, true }) foreach (bool v2 in new[] { false, true })
        {
            var fake = new Fake(ext ? 7 : 6, v2); using (var core = new CoreController(ext, fake))
            {
                var s = core.Read(); Check(s.Writable && s.Rpm[0] == 1200 && s.Temperatures[0] == 31.5 && fake.Writes == 0 && !fake.Awake, "Discovery applies defaults, loses telemetry or leaves software mode.");
                var selected = Enumerable.Range(0, core.Count).Select(i => i == core.Count - 1).ToArray(); var b = core.Capture(selected);
                var priorPowers = (int[])fake.Powers.Clone(); var priorModes = (int[])fake.Modes.Clone();
                var desired = Enumerable.Repeat(-1, core.Count).ToArray(); desired[core.Count - 1] = 80;
                core.Apply(desired, Enumerable.Repeat(-1, core.Count).ToArray(), delegate { return true; });
                Check(fake.Powers.Last() == 80 && fake.Modes.Last() == 0 && fake.Powers.Take(core.Count - 1).SequenceEqual(priorPowers.Take(core.Count - 1)) && fake.Modes.Take(core.Count - 1).SequenceEqual(priorModes.Take(core.Count - 1)), "Unselected CORE fan/EXT settings changed.");
                fake.Powers[0] = 67; core.Restore(b);
                Check(fake.Powers.Last() == priorPowers.Last() && fake.Modes.Last() == 2 && fake.Powers[0] == 67 && !fake.Awake, "Recovery overwrites unselected settings or loses curve mode.");
                fake.Connected[core.Count - 1] = 1; int writes = fake.Writes; Reject(delegate { core.Capture(selected); }); Check(fake.Writes == writes, "Absent output was written."); fake.Connected[core.Count - 1] = 7;
                Reject(delegate { core.Apply(desired, Enumerable.Repeat(-1, core.Count).ToArray(), delegate { return false; }); }); Check(fake.Writes == writes, "Cancelled session wrote CORE settings.");
                fake.Modes[core.Count - 1] = 0; fake.Powers[core.Count - 1] = 49; var previous = Enumerable.Repeat(-1, core.Count).ToArray(); previous[core.Count - 1] = 50;
                Reject(delegate { core.Apply(desired, previous, delegate { return true; }); }); Check(fake.Writes == writes, "Competing app change was overwritten.");
                fake.BrokenMap = true; Check(!core.Read().Writable && core.Read().ControlError != null && fake.Writes == writes, "Unknown firmware endpoints remain writable.");
            }
        }
        var bad = new Fake(6, true) { RejectWrite = true }; using (var core = new CoreController(false, bad)) { var target = new[] { 75, -1, -1, -1, -1, -1 }; Reject(delegate { core.Apply(target, Enumerable.Repeat(-1, 6).ToArray(), delegate { return true; }); }); Check(bad.Powers[0] == 40 && !bad.Awake, "Rejected write treated as successful."); }
        Reject(delegate { CoreProtocol.Values(new byte[] { 1, 7 }, 6, false); });
        Reject(delegate { CoreProtocol.Values(new byte[] { 6, 7, 7, 7, 7, 7, 7, 9 }, 6, false); });
        Reject(delegate { CoolingUsbDiscovery.Open(new CoolingUsbInfo { Kind = "core", Product = 0x0c1c }); });
        var late = new Fake(6, false); using (var core = new CoreController(false, late))
        {
            var baseline = core.Capture(new[] { true, false, false, false, false, false });
            Reject(delegate { core.Apply(new[] { 75, -1, -1, -1, -1, -1 }, Enumerable.Repeat(-1, 6).ToArray(), delegate { return late.Writes == 0; }); });
            Check(late.Writes == 1 && late.Powers[0] == 75 && late.Modes[0] == 2 && !late.Awake, "Late stop wrote the mode after cancelling.");
            core.Restore(baseline); Check(late.Powers[0] == 40 && late.Modes[0] == 2, "Partial write recovery failed.");
            baseline.Modes[0] = 255; int writes = late.Writes; Reject(delegate { core.Restore(baseline); }); Check(late.Writes == writes, "Corrupt recovery wrote settings.");
        }
        Console.WriteLine("PASS: " + checks + " shared CORE/XT conformance checks (simulated; no USB hardware).");
    }
    sealed class Fake : ICoreTransport
    {
        public int[] Powers, Modes, Connected;
        readonly bool v2;
        byte endpoint;
        public int Writes;
        public bool Awake, BrokenMap, RejectWrite;
        public Fake(int count, bool modern) { v2 = modern; Powers = Enumerable.Repeat(40, count).ToArray(); Modes = Enumerable.Repeat(2, count).ToArray(); Connected = Enumerable.Repeat(7, count).ToArray(); }
        public byte[] Exchange(byte[] command, byte[] data)
        {
            var r = new List<byte>(new byte[] { 0, command[0], 0 });
            if (command[0] == 1) Awake = command[3] == 2;
            else if (command[0] == 2) r.AddRange(new byte[] { (byte)(v2 ? 2 : 1), 6, 201 });
            else if (command[0] == 13) endpoint = data[0];
            else if (command[0] == 8 && command[2] == 1)
            {
                byte type = endpoint == 0x1a ? (byte)9 : endpoint == 0x17 ? (byte)6 : endpoint == 0x21 ? (byte)0x10 : endpoint == (v2 ? 0x61 : 0x60) ? (byte)3 : endpoint == (v2 ? 0x62 : 0x61) ? (byte)4 : (byte)0xff;
                if (BrokenMap && data.Length == 0 && type < 5) type = 0xff;
                r.Add(type); r.Add(0);
                if (type == 9) r.AddRange(CoreProtocol.Encode(Connected, false));
                if (type == 6) { r.Add((byte)Powers.Length); foreach (var ignored in Powers) r.AddRange(new byte[] { 176, 4 }); }
                if (type == 0x10) r.AddRange(new byte[] { 1, 0, 59, 1 });
                if (type == 3) r.AddRange(CoreProtocol.Encode(Modes, false));
                if (type == 4) r.AddRange(CoreProtocol.Encode(Powers, true));
            }
            else if (command[0] == 6)
            {
                if (RejectWrite) r[2] = 1;
                else { Writes++; if (data[4] == 3) Modes = CoreProtocol.Values(data.Skip(6).ToArray(), Modes.Length, false); else if (data[4] == 4) Powers = CoreProtocol.Values(data.Skip(6).ToArray(), Powers.Length, true); else throw new Exception("Unexpected CORE endpoint write."); }
            }
            return r.ToArray();
        }
        public void Dispose() { }
    }
}

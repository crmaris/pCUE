using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Pcue.Cooling;

// Conformance checks for the iCUE LINK System Hub driver. The simulated hub reproduces the
// layouts captured from a real one and the one behaviour that shapes the driver: it refuses
// endpoint access while in hardware mode. Device identities here are synthetic.
public static class LinkBackendTests
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static void Reject(Action action) { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } Check(rejected, "Unsafe LINK operation was accepted."); }

    sealed class Hub : ILinkTransport
    {
        public int Mode = 1, Writes;
        public bool RejectWrite, CorruptReadback, BrokenTables, Disposed;
        public int[] Modes = Enumerable.Repeat(5, 15).ToArray(), Powers = new int[15];
        public string[] Ids = new string[15], Names = new string[15];
        public int[] Rpm = new int[15]; public int[] Tenths = new int[15];
        public List<string> Log = new List<string>();
        readonly Dictionary<int, byte[]> open = new Dictionary<int, byte[]>();
        readonly Dictionary<int, int> openEndpoint = new Dictionary<int, int>();

        public void Add(int channel, string id, string name, int rpm, int tenths) { Ids[channel] = id; Names[channel] = name; Rpm[channel] = rpm; Tenths[channel] = tenths; Modes[channel] = 2; }

        byte[] Payload(byte address, int endpoint)
        {
            var b = new List<byte>();
            if (address != 0)
            {
                if (endpoint != 0x40 || address > 14 || Ids[address] == null) return null;
                var name = Encoding.ASCII.GetBytes(Names[address] + "\0");
                b.AddRange(new byte[] { 0x58, 0, 1, 2, 0, 0, 2, 1, 0x1f, 3, (byte)name.Length }); b.AddRange(name); b.AddRange(new byte[] { 4, 4, 0x30, 0x2e, 0x36, 0 });
                return b.ToArray();
            }
            switch (endpoint)
            {
                case 0x36:
                    b.AddRange(new byte[] { 0x21, 0, 14 });
                    for (int c = 1; c <= 14; c++)
                    {
                        if (Ids[c] == null) { b.AddRange(new byte[8]); continue; }
                        b.AddRange(new byte[] { 0, 0, 0x1a, (byte)c, 0, 0, 5, (byte)Ids[c].Length }); b.AddRange(Encoding.ASCII.GetBytes(Ids[c]));
                    }
                    return b.ToArray();
                case 0x17: case 0x21:
                    int slots = endpoint == 0x17 ? 26 : 52; b.AddRange(new byte[] { (byte)(endpoint == 0x17 ? 0x25 : 0x10), 0, (byte)slots });
                    for (int c = 0; c < slots; c++)
                    {
                        bool live = c >= 1 && c <= 14 && Ids[c] != null; int v = !live ? 0 : endpoint == 0x17 ? Rpm[c] : Tenths[c];
                        b.Add((byte)(live ? 0 : 1)); b.Add((byte)v); b.Add((byte)(v >> 8));
                    }
                    return b.ToArray();
                case 0x6d61:
                    b.AddRange(new byte[] { 3, 0, (byte)(BrokenTables ? 14 : 15) }); b.AddRange(Modes.Take(BrokenTables ? 14 : 15).Select(v => (byte)v)); return b.ToArray();
                case 0x6d62:
                    b.AddRange(new byte[] { 4, 0, 15 });
                    for (int c = 0; c < 15; c++) { int v = CorruptReadback && c == 1 ? Powers[c] ^ 1 : Powers[c]; b.Add((byte)v); b.Add((byte)(v >> 8)); }
                    return b.ToArray();
            }
            return null;
        }

        public byte[] Exchange(byte address, byte[] command, byte[] data)
        {
            if (Disposed) throw new ObjectDisposedException("hub");
            var a = command.Concat(data).ToArray(); var r = new byte[512]; r[0] = address; r[2] = a[0];
            switch (a[0])
            {
                case 1: if (a[1] == 3 && a[2] == 0 && (a[3] == 1 || a[3] == 2)) { Mode = a[3]; Log.Add("mode" + Mode); } else r[3] = 1; break;
                case 2: if (a[1] == 0x13) { r[4] = 4; r[5] = 1; r[6] = 0x90; r[7] = 2; } else r[3] = 5; break;
                case 5: open.Remove(address << 8 | a[2]); openEndpoint.Remove(address << 8 | a[2]); break;
                case 13:
                {
                    int key = address << 8 | a[1], endpoint = a[2] | (a.Length > 3 ? a[3] << 8 : 0);
                    if (Mode != 2) { r[3] = 3; break; }                 // measured: refused in hardware mode
                    if (open.ContainsKey(key)) { r[3] = 3; break; }
                    var p = Payload(address, endpoint); if (p == null) { r[3] = 1; break; }
                    open[key] = p; openEndpoint[key] = endpoint; Log.Add("open" + endpoint.ToString("x")); break;
                }
                case 9: { byte[] p; if (!open.TryGetValue(address << 8 | a[1], out p)) { r[3] = 3; break; } r[4] = 1; r[5] = 0x0c; r[6] = (byte)p.Length; r[7] = (byte)(p.Length >> 8); break; }
                case 8: { byte[] p; if (!open.TryGetValue(address << 8 | a[1], out p)) { r[3] = 3; break; } Array.Copy(p, 0, r, 4, p.Length); break; }
                case 6:
                {
                    int key = address << 8 | a[1], endpoint; if (!openEndpoint.TryGetValue(key, out endpoint)) { r[3] = 3; break; }
                    if (RejectWrite) { r[3] = 9; break; }
                    int length = a[2] | a[3] << 8; var body = a.Skip(8).Take(length - 2).ToArray();
                    if (a[4] != 0 || a[5] != 0 || a[7] != 0 || body.Length < 1 || body[0] != 15) { r[3] = 1; break; }
                    if (endpoint == 0x6d61 && a[6] == 3) for (int c = 0; c < 15; c++) Modes[c] = body[1 + c];
                    else if (endpoint == 0x6d62 && a[6] == 4) for (int c = 0; c < 15; c++) Powers[c] = body[1 + c * 2] | body[2 + c * 2] << 8;
                    else { r[3] = 1; break; }
                    Writes++; Log.Add("write" + endpoint.ToString("x")); break;
                }
                default: r[3] = 1; break;
            }
            return r;
        }
        public void Dispose() { Disposed = true; }
    }

    static Hub Bench()
    {
        var hub = new Hub();
        hub.Add(1, "0100AA00710472FFD90000A001", "iCUE LINK RX360 II RGB", 919, 279);
        hub.Add(13, "0100BB007104657AEB0000B013", "iCUE LINK TITAN II", 2314, 346);
        hub.Add(14, "0100CCFA120377F6160000C014", "5 inch LCD Screen Module", 1207, 441);
        return hub;
    }

    public static void Run()
    {
        // --- framing ---
        foreach (int length in new[] { 512, 513 })
        {
            var b = LinkProtocol.Request(length, 13, new byte[] { 13, 0 }, new byte[] { 0x40 }); int o = length - 512;
            Check(b.Length == length && b[o] == 13 && b[o + 1] == 1 && b[o + 2] == 13 && b[o + 3] == 0 && b[o + 4] == 0x40 && (o == 0 || b[0] == 0), "LINK request framing or report-ID slot is wrong.");
        }
        Reject(delegate { LinkProtocol.Request(65, 0, new byte[] { 8 }, new byte[0]); });
        Reject(delegate { LinkProtocol.Request(513, 0, new byte[] { 6, 1 }, new byte[511]); });
        var raw = new byte[513]; raw[1] = 14; raw[3] = 8; raw[5] = 0x58;
        Check(LinkProtocol.HidReply(raw, 513, 14, 8).Length == 512 && LinkProtocol.HidReply(raw, 513, 14, 8)[4] == 0x58, "HidSharp report ID was not stripped exactly once.");
        Reject(delegate { LinkProtocol.HidReply(raw, 513, 13, 8); });      // another device's reply
        Reject(delegate { LinkProtocol.HidReply(raw, 513, 14, 9); });      // another command's reply
        Reject(delegate { LinkProtocol.HidReply(raw, 65, 14, 8); });

        // --- discovery opens nothing and accepts only the control interface ---
        var info = CoolingUsbDiscovery.Describe(0x1b1c, 0x0c3f, "hid#vid_1b1c&pid_0c3f&mi_00#x", 513, 513);
        Check(info != null && info.Kind == "link" && info.Outputs == 14 && info.Id == CoolingUsbDiscovery.Identity("link", info.Path), "LINK hub control interface was not described.");
        Check(CoolingUsbDiscovery.Describe(0x1b1c, 0x0c3f, "hid#vid_1b1c&pid_0c3f&mi_01#x", 33, 0) == null, "LINK notification interface was offered as a cooling controller.");
        Check(CoolingUsbDiscovery.Describe(0x1b1c, 0x0c3f, "hid#vid_1b1c&pid_0c3f&mi_00#x", 65, 65) == null && CoolingUsbDiscovery.Describe(0x1b1c, 0x0c3f, "hid#vid_1b1c&pid_0c3f&mi_001#x", 513, 513) == null, "Unsupported LINK report size or interface was accepted.");
        Check(CoolingUsbDiscovery.Describe(0x1b1c, 0x0c1c, "hid&mi_00", 65, 65).Kind == "core", "Adding LINK changed CORE discovery.");

        // --- parsers reject what they do not understand ---
        var topology = new Hub(); topology.Add(1, "0100AA00710472FFD90000A001", "A", 1, 1); topology.Add(13, "0100BB007104657AEB0000B013", "B", 1, 1);
        topology.Mode = 2; topology.Exchange(0, new byte[] { 13, 1 }, new byte[] { 0x36 }); var list = topology.Exchange(0, new byte[] { 8, 1 }, new byte[0]);
        int size = LinkProtocol.Word(topology.Exchange(0, new byte[] { 9, 1 }, new byte[0]), 6); var listed = list.Skip(6).Take(size - 2).ToArray();
        var devices = LinkProtocol.Topology(listed);
        Check(devices.Length == 2 && devices[0].Channel == 1 && devices[1].Channel == 13 && devices[1].Id == "0100BB007104657AEB0000B013" && LinkProtocol.Key(devices) == "1=0100AA00710472FFD90000A001;13=0100BB007104657AEB0000B013", "LINK topology channel or identity is wrong.");
        Reject(delegate { LinkProtocol.Topology(listed.Concat(new byte[] { 0 }).ToArray()); });
        Reject(delegate { LinkProtocol.Topology(listed.Take(listed.Length - 1).ToArray()); });
        var wrongCount = (byte[])listed.Clone(); wrongCount[0] = 13; Reject(delegate { LinkProtocol.Topology(wrongCount); });
        var twin = new Hub(); twin.Add(1, "0100AA00710472FFD90000A001", "A", 1, 1); twin.Add(2, "0100AA00710472FFD90000A001", "A", 1, 1); twin.Mode = 2;
        twin.Exchange(0, new byte[] { 13, 1 }, new byte[] { 0x36 }); var twinList = twin.Exchange(0, new byte[] { 8, 1 }, new byte[0]); int twinSize = LinkProtocol.Word(twin.Exchange(0, new byte[] { 9, 1 }, new byte[0]), 6);
        Reject(delegate { LinkProtocol.Topology(twinList.Skip(6).Take(twinSize - 2).ToArray()); });
        Check(LinkProtocol.Name(new byte[] { 1, 2, 9, 0, 2, 1, 0x1f, 3, 3, 0x41, 0x42, 0 }) == "AB", "LINK device name was not read from its tag.");
        Reject(delegate { LinkProtocol.Name(new byte[] { 1, 2, 9, 0, 2, 1, 0x1f }); });
        Reject(delegate { LinkProtocol.Name(new byte[] { 3, 9, 0x41 }); });
        Reject(delegate { LinkProtocol.Readings(new byte[] { 14, 0, 0, 0 }); });
        Reject(delegate { LinkProtocol.Table(new byte[] { 14, 0 }, false); });
        Reject(delegate { LinkProtocol.Encode(Enumerable.Repeat(0x10000, 15).ToArray(), true); });
        Reject(delegate { LinkProtocol.Encode(Enumerable.Repeat(256, 15).ToArray(), false); });
        var wide = new int[15]; wide[1] = 1100; wide[13] = 100; var packed = LinkProtocol.Encode(wide, true);
        Check(packed[3] == 0x4c && packed[4] == 0x04 && LinkProtocol.Table(packed, true)[1] == 1100 && LinkProtocol.Table(packed, true)[13] == 100, "A stored RPM value lost its high byte.");
        Check(!LinkProtocol.IsRpm(100) && LinkProtocol.IsRpm(101) && LinkProtocol.FixedMode(100) == LinkProtocol.ModeFixed && LinkProtocol.FixedMode(101) == LinkProtocol.ModeRpm, "The percentage/RPM boundary is not 100/101.");

        // --- reading: telemetry, names, no writes, hub handed back in hardware mode ---
        var hub = Bench();
        using (var link = new LinkController(hub))
        {
            var s = link.Read();
            Check(link.Count == 14 && !link.HasExt && s.Writable && s.ControlError == null && s.Firmware == "4.1.656", "LINK state is incomplete.");
            Check(s.Connected[0] && s.Connected[12] && s.Connected[13] && s.Connected.Count(v => v) == 3 && s.Rpm[0] == 919 && s.Rpm[12] == 2314 && s.Rpm[13] == 1207 && s.Rpm[1] == null, "LINK RPM is on the wrong output.");
            Check(s.Temperatures[12] == 34.6 && s.Temperatures[1] == null && s.OutputNames[12] == "iCUE LINK TITAN II" && s.OutputNames[0] == "iCUE LINK RX360 II RGB" && s.OutputNames[5] == null, "LINK temperature or name is on the wrong output.");
            Check(s.Modes[0] == 2 && s.Modes[1] == 5 && s.Powers[0] == 0 && s.ChildSerial == "1=0100AA00710472FFD90000A001;13=0100BB007104657AEB0000B013;14=0100CCFA120377F6160000C014", "LINK stored settings or chain identity are wrong.");
            Check(hub.Writes == 0 && hub.Mode == 1 && hub.Log.First() == "mode2" && hub.Log.Last() == "mode1", "Reading wrote settings, skipped software mode or left the hub in it.");
            int namesRead = hub.Log.Count(l => l == "open40"); link.Read(); Check(namesRead == 3 && hub.Log.Count(l => l == "open40") == 3, "Device descriptions are re-read on every poll.");

            // --- selected-only apply: percentage before mode, unselected channels untouched ---
            var selected = Enumerable.Range(0, 14).Select(i => i == 0).ToArray(); var baseline = link.Capture(selected);
            var desired = Enumerable.Repeat(-1, 14).ToArray(); desired[0] = 60; var none = Enumerable.Repeat(-1, 14).ToArray();
            hub.Log.Clear(); link.Apply(desired, none, delegate { return true; });
            Check(hub.Powers[1] == 60 && hub.Modes[1] == 0 && hub.Modes[13] == 2 && hub.Modes[14] == 2 && hub.Powers[13] == 0 && hub.Modes[0] == 5 && hub.Mode == 1, "Unselected LINK output changed, or the hub was left in software mode.");
            Check(hub.Log.IndexOf("write6d62") >= 0 && hub.Log.IndexOf("write6d62") < hub.Log.IndexOf("write6d61"), "Mode was switched to fixed before its percentage was stored.");
            int writes = hub.Writes; link.Apply(desired, desired, delegate { return true; }); Check(hub.Writes == writes, "An unchanged LINK setting was rewritten to the hub's stored tables.");

            // --- 101 and above is an RPM the hub holds itself: mode 01 with the RPM in the value table ---
            var speed = Enumerable.Repeat(-1, 14).ToArray(); speed[0] = 1100;
            hub.Log.Clear(); link.Apply(speed, desired, delegate { return true; });
            Check(hub.Powers[1] == 1100 && hub.Modes[1] == 1 && hub.Modes[13] == 2 && hub.Powers[13] == 0 && hub.Mode == 1, "An RPM target was not stored as fixed-RPM mode, or another output changed.");
            Check(hub.Log.IndexOf("write6d62") >= 0 && hub.Log.IndexOf("write6d62") < hub.Log.IndexOf("write6d61"), "RPM mode was selected before its value was stored.");
            var held = link.Read(); Check(held.Writable && held.Modes[0] == LinkProtocol.ModeRpm && held.Powers[0] == 1100, "A stored RPM target is not reported back.");
            writes = hub.Writes; link.Apply(speed, speed, delegate { return true; }); Check(hub.Writes == writes, "An unchanged RPM target was rewritten.");
            Reject(delegate { link.Apply(speed, desired, delegate { return true; }); }); Check(hub.Writes == writes, "A percentage record was accepted for an output that holds an RPM.");
            var faster = Enumerable.Repeat(-1, 14).ToArray(); faster[0] = 1580; link.Apply(faster, speed, delegate { return true; });
            Check(hub.Powers[1] == 1580 && hub.Modes[1] == 1, "An RPM target could not be changed to another RPM.");
            link.Apply(desired, faster, delegate { return true; });
            Check(hub.Powers[1] == 60 && hub.Modes[1] == 0, "Returning from an RPM target to a percentage did not restore fixed-percentage mode.");
            writes = hub.Writes;

            // --- restore returns the captured curve, leaves other channels' later changes alone ---
            hub.Powers[13] = 77; link.Restore(baseline);
            Check(hub.Modes[1] == 2 && hub.Powers[1] == 0 && hub.Powers[13] == 77 && hub.Mode == 1, "Recovery overwrote an unselected output or lost curve mode.");

            // --- refusals ---
            writes = hub.Writes;
            Reject(delegate { link.Apply(desired, none, delegate { return false; }); }); Check(hub.Writes == writes && hub.Mode == 1, "Cancelled session wrote LINK settings.");
            var absent = Enumerable.Range(0, 14).Select(i => i == 5).ToArray(); Reject(delegate { link.Capture(absent); });
            var toAbsent = Enumerable.Repeat(-1, 14).ToArray(); toAbsent[5] = 50; Reject(delegate { link.Apply(toAbsent, none, delegate { return true; }); }); Check(hub.Writes == writes, "Absent LINK output was written.");
            var stale = Enumerable.Repeat(-1, 14).ToArray(); stale[0] = 50; Reject(delegate { link.Apply(desired, stale, delegate { return true; }); }); Check(hub.Writes == writes, "Competing app change was overwritten.");
            var over = Enumerable.Repeat(-1, 14).ToArray(); over[0] = LinkProtocol.MaxRpm + 1; Reject(delegate { link.Apply(over, none, delegate { return true; }); }); Check(hub.Writes == writes, "An RPM target above the supported range was written.");
            Reject(delegate { link.Apply(new int[6], new int[6], delegate { return true; }); });

            // --- a re-plugged chain invalidates the record instead of restoring onto other devices ---
            string moved = hub.Ids[13]; hub.Ids[13] = null; hub.Ids[12] = moved; hub.Names[12] = hub.Names[13]; hub.Modes[12] = 2;
            Reject(delegate { link.Restore(baseline); }); Check(hub.Writes == writes && hub.Mode == 1, "A recovery record was applied to a re-ordered LINK chain.");
        }
        Check(hub.Disposed, "LINK transport was not released.");

        // --- failures still hand the hub back in hardware mode ---
        var refuses = Bench(); refuses.RejectWrite = true;
        using (var link = new LinkController(refuses)) { var target = Enumerable.Repeat(-1, 14).ToArray(); target[0] = 70; Reject(delegate { link.Apply(target, Enumerable.Repeat(-1, 14).ToArray(), delegate { return true; }); }); Check(refuses.Writes == 0 && refuses.Mode == 1 && refuses.Modes[1] == 2, "A rejected LINK write left the hub in software mode or changed its mode table."); }
        var lies = Bench(); lies.CorruptReadback = true;
        using (var link = new LinkController(lies)) { var target = Enumerable.Repeat(-1, 14).ToArray(); target[0] = 70; Reject(delegate { link.Apply(target, Enumerable.Repeat(-1, 14).ToArray(), delegate { return true; }); }); Check(lies.Mode == 1, "A LINK readback mismatch left the hub in software mode."); }
        var unknown = Bench(); unknown.BrokenTables = true;
        using (var link = new LinkController(unknown)) { var s = link.Read(); Check(!s.Writable && s.ControlError != null && s.Rpm[0] == 919 && unknown.Writes == 0, "Unknown stored-table layout stayed writable, or telemetry was lost with it."); Reject(delegate { link.Capture(Enumerable.Range(0, 14).Select(i => i == 0).ToArray()); }); }
        // --- a hub the vendor application left in fixed-RPM mode is a valid starting point and is restored to it ---
        var vendorRpm = Bench(); vendorRpm.Modes[1] = 1; vendorRpm.Powers[1] = 1200;
        using (var link = new LinkController(vendorRpm))
        {
            var s = link.Read(); Check(s.Writable && s.Modes[0] == 1 && s.Powers[0] == 1200, "A vendor fixed-RPM profile disabled control.");
            var pick = Enumerable.Range(0, 14).Select(i => i == 0).ToArray(); var before = link.Capture(pick);
            var target = Enumerable.Repeat(-1, 14).ToArray(); target[0] = 50; link.Apply(target, Enumerable.Repeat(-1, 14).ToArray(), delegate { return true; });
            Check(vendorRpm.Modes[1] == 0 && vendorRpm.Powers[1] == 50, "A percentage was not applied over a vendor fixed-RPM profile.");
            link.Restore(before); Check(vendorRpm.Modes[1] == 1 && vendorRpm.Powers[1] == 1200 && vendorRpm.Mode == 1, "A captured fixed-RPM profile was not restored.");
        }
        // --- but a percentage slot holding more than 100 is not understood, so control stays off ---
        var nonsense = Bench(); nonsense.Modes[1] = 0; nonsense.Powers[1] = 300;
        using (var link = new LinkController(nonsense)) { var s = link.Read(); Check(!s.Writable && s.ControlError != null && s.Rpm[0] == 919 && nonsense.Writes == 0, "A fixed percentage above 100 was accepted as a baseline."); }
        Console.WriteLine(checks + " LINK hub checks passed; simulated hub, no hardware.");
    }
}

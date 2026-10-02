// Shared, independently implemented wire protocol. No third-party driver code copied.
// Canonical source: pCUE-Light/shared/CorsairLink.cs. Keep app copies byte-identical.
//
// Corsair iCUE LINK System Hub, 1B1C:0C3F. Every field below was read from USB captures of the
// vendor application and then confirmed by direct, bounded trials on one commissioned hub
// (see CORSAIR-LINK-PROTOCOL.md). It is a sibling of the Commander CORE protocol, not the same
// one: the report is 512 bytes, the first wire byte is a chain address, and the reply carries a
// status byte. Never reuse CORE framing or CORE channel counts here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HidSharp;

namespace Pcue.Cooling
{
    public interface ILinkTransport : IDisposable { byte[] Exchange(byte address, byte[] command, byte[] data); }

    public sealed class LinkDevice
    {
        public int Channel;      // 1..14, the position on the hub's chain
        public byte[] Header;    // eight raw bytes preceding the ID, kept verbatim: their meaning is not established
        public string Id;        // the device's own stable ASCII identity
    }

    public static class LinkProtocol
    {
        public const int Wire = 512;         // protocol bytes per report; Windows adds a leading report-ID zero
        public const int Channels = 14;      // chain positions 1..14
        public const int TableSlots = 15;    // stored tables carry slot 0 (the hub itself) plus the 14 channels
        public const int ModeFixed = 0, ModeRpm = 1, ModeCurve = 2, ModeAbsent = 5;
        // One stored value word per channel serves both fixed modes: a percentage in mode 00, an RPM the
        // hub holds by itself in mode 01. Applications use the same convention: up to 100 is a
        // percentage, 101 and above is an RPM target. Measured on fans: 940/1100/1580 gave 944/1094/1572.
        public const int MaxPercent = 100, MaxRpm = 9999;
        public static bool IsRpm(int value) { return value > MaxPercent; }
        public static int FixedMode(int value) { return IsRpm(value) ? ModeRpm : ModeFixed; }

        public static bool ReportLength(int length) { return length == Wire || length == Wire + 1; }

        // Request: [address][01][command...][data...]. Address 0 is the hub; N addresses the device on channel N.
        public static byte[] Request(int length, byte address, byte[] command, byte[] data)
        {
            if (!ReportLength(length) || command == null || command.Length == 0 || data == null) throw new InvalidDataException("Invalid LINK request or report descriptor.");
            int offset = length - Wire;      // 1 when the buffer carries the Windows report ID
            if (2 + command.Length + data.Length > Wire) throw new InvalidDataException("LINK request does not fit one report.");
            var b = new byte[length]; b[offset] = address; b[offset + 1] = 1;
            Array.Copy(command, 0, b, offset + 2, command.Length); Array.Copy(data, 0, b, offset + 2 + command.Length, data.Length);
            return b;
        }

        // Reply as HidSharp returns it on Windows: report ID zero, then [address][00][command][status][payload...].
        public static byte[] HidReply(byte[] raw, int count, byte address, byte command)
        {
            if (raw == null || count != Wire + 1 || count > raw.Length || raw[0] != 0 || raw[1] != address || raw[2] != 0 || raw[3] != command) throw new InvalidDataException("LINK returned an unrelated or truncated HID response.");
            return raw.Skip(1).Take(Wire).ToArray();
        }

        public static int Word(byte[] b, int offset) { if (b == null || offset < 0 || offset + 2 > b.Length) throw new InvalidDataException("Truncated LINK value."); return b[offset] | b[offset + 1] << 8; }

        // Endpoint 36, type 21: [count] then one entry per chain position - eight header bytes whose
        // last byte is the ID length, followed by that many ASCII characters. An empty position is
        // eight zero bytes. Entry k describes channel k + 1.
        public static LinkDevice[] Topology(byte[] d)
        {
            if (d == null || d.Length < 1 || d[0] != Channels) throw new InvalidDataException("Unsupported LINK topology size.");
            var found = new List<LinkDevice>(); int at = 1;
            for (int slot = 0; slot < Channels; slot++)
            {
                if (at + 8 > d.Length) throw new InvalidDataException("Truncated LINK topology.");
                int length = d[at + 7];
                if (length == 0) { if (d.Skip(at).Take(8).Any(v => v != 0)) throw new InvalidDataException("LINK topology has an entry without an identity."); at += 8; continue; }
                if (length < 8 || length > 64 || at + 8 + length > d.Length) throw new InvalidDataException("LINK device identity is outside the supported length.");
                var id = d.Skip(at + 8).Take(length).ToArray();
                if (id.Any(c => c < 0x30 || c > 0x7a || (c > 0x39 && c < 0x41) || (c > 0x5a && c < 0x61))) throw new InvalidDataException("LINK device identity is not plain alphanumeric text.");
                found.Add(new LinkDevice { Channel = slot + 1, Header = d.Skip(at).Take(8).ToArray(), Id = Encoding.ASCII.GetString(id) });
                at += 8 + length;
            }
            if (at != d.Length) throw new InvalidDataException("LINK topology has trailing data.");
            if (found.Select(f => f.Id).Distinct().Count() != found.Count) throw new InvalidDataException("LINK topology repeats a device identity.");
            return found.ToArray();
        }

        // Endpoint 40 on a device address, type 58: a tag/length/value list. Tag 03 is the product name.
        public static string Name(byte[] d)
        {
            if (d == null) throw new InvalidDataException("Missing LINK device description.");
            for (int at = 0; at + 2 <= d.Length; )
            {
                int tag = d[at], length = d[at + 1];
                if (at + 2 + length > d.Length) throw new InvalidDataException("Truncated LINK device description.");
                if (tag == 3)
                {
                    var text = d.Skip(at + 2).Take(length).TakeWhile(c => c != 0).ToArray();
                    if (text.Length == 0 || text.Any(c => c < 0x20 || c > 0x7e)) throw new InvalidDataException("LINK device name is not printable text.");
                    return Encoding.ASCII.GetString(text);
                }
                at += 2 + length;
            }
            throw new InvalidDataException("LINK device description has no name.");
        }

        // Endpoints 17 (type 25, RPM) and 21 (type 10, tenths of a degree): [count] then [status][lo][hi]
        // per slot, slot index = channel. Status 0 is a live reading; anything else means none.
        public static int?[] Readings(byte[] d)
        {
            if (d == null || d.Length < 1 || d[0] < TableSlots || d.Length != 1 + d[0] * 3) throw new InvalidDataException("Unsupported LINK telemetry layout.");
            return Enumerable.Range(1, Channels).Select(c => d[1 + c * 3] == 0 ? (int?)Word(d, 2 + c * 3) : null).ToArray();
        }

        // Stored tables 61 6d (type 03, one mode byte per slot) and 62 6d (type 04, one value word per slot:
        // a percentage when the slot's mode is 00, an RPM when it is 01).
        public static int[] Table(byte[] d, bool words)
        {
            int length = 1 + TableSlots * (words ? 2 : 1);
            if (d == null || d.Length != length || d[0] != TableSlots) throw new InvalidDataException("Unsupported LINK stored-table layout.");
            return Enumerable.Range(0, TableSlots).Select(i => words ? Word(d, 1 + i * 2) : (int)d[1 + i]).ToArray();
        }
        public static byte[] Encode(int[] values, bool words)
        {
            if (values == null || values.Length != TableSlots || values.Any(v => v < 0 || v > (words ? 0xffff : 255))) throw new InvalidDataException("Invalid LINK stored table.");
            var b = new byte[1 + TableSlots * (words ? 2 : 1)]; b[0] = TableSlots;
            for (int i = 0; i < TableSlots; i++) { if (words) { b[1 + i * 2] = (byte)values[i]; b[2 + i * 2] = (byte)(values[i] >> 8); } else b[1 + i] = (byte)values[i]; }
            return b;
        }

        // The identity an application remembers: which device sits on which channel. A chain that has
        // been re-plugged in a different order produces a different key, so an old mapping is refused.
        public static string Key(LinkDevice[] devices) { return string.Join(";", devices.OrderBy(d => d.Channel).Select(d => d.Channel + "=" + d.Id)); }
    }

    public sealed class LinkHidTransport : ILinkTransport
    {
        readonly HidStream stream;
        public LinkHidTransport(string path)
        {
            var matches = DeviceList.Local.GetHidDevices(0x1b1c, 0x0c3f).Where(d => d.DevicePath == path).ToArray();
            if (matches.Length != 1 || CoolingUsbDiscovery.Describe(0x1b1c, 0x0c3f, path, matches[0].GetMaxInputReportLength(), matches[0].GetMaxOutputReportLength()) == null) throw new IOException("Selected LINK hub HID interface is unavailable or unsupported.");
            stream = matches[0].Open(); stream.ReadTimeout = 1000; stream.WriteTimeout = 1000;
        }
        public byte[] Exchange(byte address, byte[] command, byte[] data)
        {
            var b = new byte[stream.Device.GetMaxInputReportLength()]; stream.ReadTimeout = 1;
            try { for (int i = 0; i < 32; i++) { try { stream.Read(b); } catch (TimeoutException) { break; } if (i == 31) throw new IOException("LINK reply queue did not drain."); } } finally { stream.ReadTimeout = 1000; }
            stream.Write(LinkProtocol.Request(stream.Device.GetMaxOutputReportLength(), address, command, data));
            var deadline = DateTime.UtcNow.AddSeconds(1.5);   // a mode switch was measured at up to 175 ms
            for (int i = 0; i < 16 && DateTime.UtcNow < deadline; i++)
            {
                int count = stream.Read(b);
                if (count == LinkProtocol.Wire + 1 && b[0] == 0 && b[1] == address && b[2] == 0 && b[3] == command[0]) return LinkProtocol.HidReply(b, count, address, command[0]);
            }
            throw new IOException("LINK did not acknowledge the current command.");
        }
        public void Dispose() { stream.Dispose(); }
    }

    // Control model, chosen because it is the one that survives this application dying: the hub
    // stores a mode and a fixed value per channel - a percentage, or an RPM it regulates to - and
    // applies them by itself in hardware mode.
    // Software mode is entered only for the duration of one transaction - telemetry and the stored
    // tables are refused in hardware mode - and the hub keeps its outputs steady while it is in it.
    // The vendor application must not be running: it holds the hub in software mode permanently.
    public sealed class LinkController : INativeCoolingController
    {
        const byte Handle = 0xf7;        // private endpoint handle; the vendor application uses 00 and 01
        const byte ChildHandle = 0;      // as captured for a device address
        readonly ILinkTransport transport;
        readonly object io = new object();
        readonly Dictionary<string, string> names = new Dictionary<string, string>();
        int[] modeTable, powerTable;     // the full 15-slot stored tables last read from the hub
        public bool HasExt { get { return false; } }
        public int Count { get { return LinkProtocol.Channels; } }
        public LinkController(ILinkTransport connection) { if (connection == null) throw new ArgumentNullException("connection"); transport = connection; }

        byte[] Send(byte address, byte[] command, params byte[] data)
        {
            byte[] r = transport.Exchange(address, command, data);
            if (r == null || r.Length != LinkProtocol.Wire || r[0] != address || r[1] != 0 || r[2] != command[0]) throw new InvalidDataException("LINK command echo is missing or mismatched.");
            if (r[3] != 0) throw new CoreRejectedException("LINK rejected command " + command[0].ToString("x2") + " (status " + r[3] + ").");
            return r;
        }
        T Operation<T>(Func<T> operation)
        {
            lock (io) using (var mutex = new Mutex(false, "Global\\CorsairLinkReadWriteGuardMutex"))
            {
                bool acquired = false;
                try { try { acquired = mutex.WaitOne(1500); } catch (AbandonedMutexException) { acquired = true; } if (!acquired) throw new IOException("Another Corsair transaction is busy."); Send(0, new byte[] { 1, 3, 0, 2 }); return operation(); }
                finally { if (acquired) { try { Send(0, new byte[] { 1, 3, 0, 1 }); } finally { mutex.ReleaseMutex(); } } }
            }
        }
        // Close, open, ask the size, read once, close. Everything this driver needs fits one report.
        byte[] ReadData(byte address, byte handle, byte[] endpoint, byte type)
        {
            try { Send(address, new byte[] { 5, 1, handle }); } catch (CoreRejectedException) { }
            Send(address, new byte[] { 13, handle }, endpoint);
            try
            {
                var size = Send(address, new byte[] { 9, handle }); int length = LinkProtocol.Word(size, 6);
                if (length < 2 || length > LinkProtocol.Wire - 4) throw new InvalidDataException("LINK endpoint does not fit one report.");
                var r = Send(address, new byte[] { 8, handle });
                if (r[4] != type || r[5] != 0) throw new CoreRejectedException("LINK endpoint returned a different data type.");
                return r.Skip(6).Take(length - 2).ToArray();
            }
            finally { Send(address, new byte[] { 5, 1, handle }); }
        }
        CoreState State()
        {
            var fw = Send(0, new byte[] { 2, 0x13 });
            var devices = LinkProtocol.Topology(ReadData(0, Handle, new byte[] { 0x36 }, 0x21));
            foreach (var d in devices) if (!names.ContainsKey(d.Id)) names[d.Id] = LinkProtocol.Name(ReadData((byte)d.Channel, ChildHandle, new byte[] { 0x40 }, 0x58));
            var speeds = LinkProtocol.Readings(ReadData(0, Handle, new byte[] { 0x17 }, 0x25));
            var temps = LinkProtocol.Readings(ReadData(0, Handle, new byte[] { 0x21 }, 0x10));
            var present = new bool[Count]; var labels = new string[Count];
            foreach (var d in devices) { present[d.Channel - 1] = true; labels[d.Channel - 1] = names[d.Id]; }
            var s = new CoreState
            {
                Firmware = fw[4] + "." + fw[5] + "." + LinkProtocol.Word(fw, 6),
                ChildSerial = LinkProtocol.Key(devices), OutputNames = labels, Connected = present,
                Rpm = speeds.Select((v, i) => present[i] ? v : null).ToArray(),
                Temperatures = temps.Select((v, i) => present[i] && v.HasValue ? (double?)(v.Value / 10.0) : null).ToArray()
            };
            modeTable = null; powerTable = null;
            try
            {
                var modes = LinkProtocol.Table(ReadData(0, Handle, new byte[] { 0x61, 0x6d }, 3), false);
                var powers = LinkProtocol.Table(ReadData(0, Handle, new byte[] { 0x62, 0x6d }, 4), true);
                for (int i = 0; i < modes.Length; i++) if (modes[i] == LinkProtocol.ModeFixed && powers[i] > LinkProtocol.MaxPercent) throw new InvalidDataException("LINK fixed percentage is outside 0-100.");
                modeTable = modes; powerTable = powers; s.Modes = modes.Skip(1).ToArray(); s.Powers = powers.Skip(1).ToArray();
            }
            catch (CoreRejectedException ex) { s.ControlError = "LINK stored settings cannot be captured; control is disabled. " + ex.Message; }
            catch (InvalidDataException ex) { s.ControlError = "LINK stored settings cannot be captured; control is disabled. " + ex.Message; }
            return s;
        }
        public CoreState Read() { return Operation(State); }
        static bool Restorable(int mode) { return mode == LinkProtocol.ModeFixed || mode == LinkProtocol.ModeRpm || mode == LinkProtocol.ModeCurve; }
        static void Selection(bool[] selected, CoreState s)
        {
            if (!s.Writable || selected == null || selected.Length != s.Connected.Length || !selected.Any(v => v)) throw new InvalidDataException("LINK has no valid capture/selection. " + s.ControlError);
            for (int i = 0; i < selected.Length; i++) if (selected[i] && (!s.Connected[i] || !Restorable(s.Modes[i]))) throw new IOException("Selected LINK output is absent or uses an unsupported stored mode.");
        }
        public CoreBaseline Capture(bool[] selected)
        {
            return Operation(delegate { var s = State(); Selection(selected, s); return new CoreBaseline { Selected = (bool[])selected.Clone(), Modes = (int[])s.Modes.Clone(), Powers = (int[])s.Powers.Clone(), ChildSerial = s.ChildSerial }; });
        }
        void WriteData(byte[] endpoint, byte type, byte[] data, Func<bool> allowed)
        {
            ReadData(0, Handle, endpoint, type);
            Send(0, new byte[] { 13, Handle }, endpoint);
            try
            {
                var payload = new byte[6 + data.Length]; payload[0] = (byte)(data.Length + 2); payload[1] = (byte)((data.Length + 2) >> 8); payload[4] = type; Array.Copy(data, 0, payload, 6, data.Length);
                if (allowed != null && !allowed()) throw new IOException("LINK control session stopped before a settings write.");
                Send(0, new byte[] { 6, Handle }, payload);
            }
            finally { Send(0, new byte[] { 5, 1, Handle }); }
        }
        void Store(int[] modes, int[] powers, Func<bool> allowed)
        {
            // Value first: a channel must never be switched to a fixed mode while it still holds a stale value.
            // Changing between percentage and RPM on a running fan leaves the old mode with the new value
            // for the ~0.1 s between the two writes; that was measured as harmless on fans. A pump is only
            // ever given percentages by the applications, so it never crosses that boundary.
            if (!powers.SequenceEqual(powerTable)) WriteData(new byte[] { 0x62, 0x6d }, 4, LinkProtocol.Encode(powers, true), allowed);
            if (!modes.SequenceEqual(modeTable)) WriteData(new byte[] { 0x61, 0x6d }, 3, LinkProtocol.Encode(modes, false), allowed);
            var actualModes = LinkProtocol.Table(ReadData(0, Handle, new byte[] { 0x61, 0x6d }, 3), false); var actualPowers = LinkProtocol.Table(ReadData(0, Handle, new byte[] { 0x62, 0x6d }, 4), true);
            if (!actualModes.SequenceEqual(modes) || !actualPowers.SequenceEqual(powers)) throw new IOException("LINK settings readback differs; recovery remains required.");
            modeTable = actualModes; powerTable = actualPowers;
        }
        public void Apply(int[] desired, int[] previous, Func<bool> allowed)
        {
            // desired: -1 leaves an output alone, 0..100 is a percentage, 101..MaxRpm is an RPM the hub regulates to.
            if (desired == null || previous == null || desired.Length != Count || previous.Length != Count || desired.Any(v => v < -1 || v > LinkProtocol.MaxRpm)) throw new InvalidDataException("Invalid LINK duties.");
            Operation(delegate
            {
                var s = State(); var selected = desired.Select(v => v >= 0).ToArray(); Selection(selected, s);
                for (int i = 0; i < Count; i++) if (selected[i] && previous[i] >= 0 && (s.Modes[i] != LinkProtocol.FixedMode(previous[i]) || s.Powers[i] != previous[i])) throw new IOException("Another app changed a managed LINK output; restore before restarting.");
                var powers = (int[])powerTable.Clone(); var modes = (int[])modeTable.Clone();
                for (int i = 0; i < Count; i++) if (selected[i]) { powers[i + 1] = desired[i]; modes[i + 1] = LinkProtocol.FixedMode(desired[i]); }
                Store(modes, powers, allowed); return true;
            });
        }
        public void Restore(CoreBaseline baseline)
        {
            if (baseline == null || baseline.Selected == null || baseline.Modes == null || baseline.Powers == null || baseline.Selected.Length != Count || baseline.Modes.Length != Count || baseline.Powers.Length != Count) throw new InvalidDataException("Invalid LINK recovery record.");
            Operation(delegate
            {
                var s = State();
                // The record names channels. If the chain has been re-plugged those channels are other devices now.
                if (baseline.ChildSerial != null && baseline.ChildSerial != s.ChildSerial) throw new IOException("The LINK device chain changed since capture; restore it from the vendor application.");
                Selection(baseline.Selected, s);
                var powers = (int[])powerTable.Clone(); var modes = (int[])modeTable.Clone();
                for (int i = 0; i < Count; i++) if (baseline.Selected[i])
                {
                    if (!Restorable(baseline.Modes[i]) || baseline.Powers[i] < 0 || baseline.Powers[i] > 0xffff || (baseline.Modes[i] == LinkProtocol.ModeFixed && baseline.Powers[i] > LinkProtocol.MaxPercent)) throw new InvalidDataException("Invalid selected LINK baseline.");
                    powers[i + 1] = baseline.Powers[i]; modes[i + 1] = baseline.Modes[i];
                }
                Store(modes, powers, null); return true;
            });
        }
        public void Dispose() { lock (io) transport.Dispose(); }
    }
}

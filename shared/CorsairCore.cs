// Shared, independently implemented wire protocol. No third-party driver code copied.
// Canonical source: pCUE-Light/shared/CorsairCore.cs. Keep app copies byte-identical.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using HidSharp;

namespace Pcue.Cooling
{
    public interface INativeCoolingController : IDisposable
    {
        int Count { get; }
        bool HasExt { get; }
        CoreState Read();
        CoreBaseline Capture(bool[] selected);
        void Apply(int[] desired, int[] previous, Func<bool> allowed);
        void Restore(CoreBaseline baseline);
    }
    public interface ICoreTransport : IDisposable { byte[] Exchange(byte[] command, byte[] data); }
    public sealed class CoreRejectedException : IOException { public CoreRejectedException(string message) : base(message) { } }
    public sealed class CoreState
    {
        public string Firmware;
        public string ChildSerial;
        public bool[] Connected;
        public int?[] Rpm;
        public double?[] Temperatures;
        public int[] Modes, Powers;
        public string ControlError;
        public bool Writable { get { return Modes != null && Powers != null; } }
    }
    public sealed class CoreBaseline
    {
        public bool[] Selected;
        public int[] Modes, Powers;
        public int? GlobalMode;
        public string ChildSerial;
    }
    public static class CoreProtocol
    {
        public static bool ReportLength(int length) { return new[] { 64, 65, 96, 97, 1024, 1025 }.Contains(length); }
        public static byte[] Request(int length, byte[] command, byte[] data)
        {
            if (!ReportLength(length) || command == null || command.Length == 0 || data == null || command.Length + data.Length + 2 > length) throw new InvalidDataException("Invalid CORE request or report descriptor.");
            var b = new byte[length]; b[1] = 8; Array.Copy(command, 0, b, 2, command.Length); Array.Copy(data, 0, b, 2 + command.Length, data.Length); return b;
        }
        // HidSharp's Windows stream includes report-ID zero. Protocol response follows it.
        public static byte[] HidReply(byte[] raw, int count, byte command)
        {
            if (raw == null || count < 4 || count > raw.Length || raw[0] != 0 || raw[1] != 0 || raw[2] != command) throw new InvalidDataException("CORE returned an unrelated or truncated HID response.");
            return raw.Skip(1).Take(count - 1).ToArray();
        }
        public static int Word(byte[] b, int offset) { if (b == null || offset < 0 || offset + 2 > b.Length) throw new InvalidDataException("Truncated CORE value."); return b[offset] | b[offset + 1] << 8; }
        public static int[] Values(byte[] b, int count, bool words)
        {
            int length = 1 + count * (words ? 2 : 1);
            if (b == null || b.Length < length || b[0] != count || b.Skip(length).Any(v => v != 0)) throw new InvalidDataException("Unsupported CORE channel count or data layout.");
            return Enumerable.Range(0, count).Select(i => words ? Word(b, 1 + i * 2) : (int)b[i + 1]).ToArray();
        }
        public static byte[] Encode(int[] values, bool words)
        {
            if (values == null || values.Length < 1 || values.Length > 7 || values.Any(v => v < 0 || v > (words ? 100 : 255))) throw new InvalidDataException("Invalid CORE settings.");
            var b = new byte[1 + values.Length * (words ? 2 : 1)]; b[0] = (byte)values.Length;
            for (int i = 0; i < values.Length; i++) { b[1 + i * (words ? 2 : 1)] = (byte)values[i]; if (words) b[2 + i * 2] = (byte)(values[i] >> 8); } return b;
        }
    }
    public sealed class CoreHidTransport : ICoreTransport
    {
        readonly HidStream stream;
        public CoreHidTransport(string path, int product)
        {
            if (product != 0x0c1c && product != 0x0c2a) throw new InvalidDataException("Unsupported CORE product.");
            var matches = DeviceList.Local.GetHidDevices(0x1b1c, product).Where(d => d.DevicePath == path).ToArray();
            if (matches.Length != 1 || CoolingUsbDiscovery.Describe(0x1b1c, product, path, matches[0].GetMaxInputReportLength(), matches[0].GetMaxOutputReportLength()) == null) throw new IOException("Selected CORE HID interface is unavailable or unsupported.");
            stream = matches[0].Open(); stream.ReadTimeout = 1000; stream.WriteTimeout = 1000;
        }
        public byte[] Exchange(byte[] command, byte[] data)
        {
            var b = new byte[stream.Device.GetMaxInputReportLength()]; stream.ReadTimeout = 1;
            try { for (int i = 0; i < 32; i++) { try { stream.Read(b); } catch (TimeoutException) { break; } if (i == 31) throw new IOException("CORE reply queue did not drain."); } } finally { stream.ReadTimeout = 1000; }
            stream.Write(CoreProtocol.Request(stream.Device.GetMaxOutputReportLength(), command, data));
            var deadline = DateTime.UtcNow.AddSeconds(1);
            for (int i = 0; i < 16 && DateTime.UtcNow < deadline; i++)
            {
                int count = stream.Read(b);
                if (count >= 4 && b[0] == 0 && b[1] == 0 && b[2] == command[0]) return CoreProtocol.HidReply(b, count, command[0]);
            }
            throw new IOException("CORE did not acknowledge the current command.");
        }
        public void Dispose() { stream.Dispose(); }
    }
    public sealed class CoreController : INativeCoolingController
    {
        const byte Handle = 0xf7;
        readonly ICoreTransport transport;
        readonly object io = new object();
        readonly int count;
        byte modeEndpoint, powerEndpoint;
        public bool HasExt { get; private set; }
        public int Count { get { return count; } }
        public CoreController(bool hasExt, ICoreTransport connection) { HasExt = hasExt; count = hasExt ? 7 : 6; transport = connection; }
        byte[] Send(byte[] command, params byte[] data)
        {
            byte[] r = transport.Exchange(command, data);
            if (r == null || r.Length < 3 || r[0] != 0 || r[1] != command[0]) throw new InvalidDataException("CORE command echo is missing or mismatched.");
            if (r[2] != 0) throw new CoreRejectedException("CORE rejected command " + command[0].ToString("x2") + " (status " + r[2] + ")."); return r;
        }
        T Operation<T>(Func<T> operation)
        {
            lock (io) using (var mutex = new Mutex(false, "Global\\CorsairLinkReadWriteGuardMutex"))
            {
                bool acquired = false;
                try { try { acquired = mutex.WaitOne(1500); } catch (AbandonedMutexException) { acquired = true; } if (!acquired) throw new IOException("Another Corsair transaction is busy."); Send(new byte[] { 1, 3, 0, 2 }); return operation(); }
                finally { if (acquired) { try { Send(new byte[] { 1, 3, 0, 1 }); } finally { mutex.ReleaseMutex(); } } }
            }
        }
        byte[] ReadData(byte endpoint, bool hardware, byte type)
        {
            Send(new byte[] { 5, 1, Handle });
            try
            {
                Send(new byte[] { 13, Handle }, hardware ? new byte[] { endpoint, 0x6d } : new byte[] { endpoint });
                var a = Send(new byte[] { 8, Handle, 1 }); var b = Send(new byte[] { 8, Handle, 2 }); var c = Send(new byte[] { 8, Handle, 3 });
                if (a.Length < 5 || a[3] != type || a[4] != 0) throw new CoreRejectedException("CORE endpoint returned a different data type.");
                return a.Skip(5).Concat(b.Skip(3)).Concat(c.Skip(3)).ToArray();
            }
            finally { Send(new byte[] { 5, 1, Handle }); }
        }
        CoreState State()
        {
            var fw = Send(new byte[] { 2, 0x13 }); if (fw.Length < 6) throw new InvalidDataException("CORE firmware response is incomplete.");
            var connected = CoreProtocol.Values(ReadData(0x1a, false, 9), count, false);
            if (connected.Any(v => v != 1 && v != 7)) throw new InvalidDataException("Unsupported CORE connection state.");
            var speedData = ReadData(0x17, false, 6); // RPM words may exceed percent range.
            var speeds = CoreProtocol.Values(speedData, count, true);
            var s = new CoreState { Firmware = fw[3] + "." + fw[4] + "." + (fw.Length >= 7 ? CoreProtocol.Word(fw, 5) : fw[5]), Connected = connected.Select(v => v == 7).ToArray(), Rpm = speeds.Select((v, i) => connected[i] == 7 ? (int?)v : null).ToArray(), Temperatures = new double?[0] };
            try
            {
                byte[] t = ReadData(0x21, false, 0x10);
                if (t.Length < 1 || t[0] > 4 || t.Length < 1 + t[0] * 3) throw new InvalidDataException("CORE temperature layout is unsupported.");
                s.Temperatures = Enumerable.Range(0, t[0]).Select(i => t[1 + i * 3] == 0 ? (double?)(CoreProtocol.Word(t, 2 + i * 3) / 10.0) : null).ToArray();
            } catch (CoreRejectedException) { }
            try { Hardware(s, fw[3]); } catch (CoreRejectedException ex) { s.ControlError = ex.Message; } catch (InvalidDataException ex) { s.ControlError = ex.Message; }
            return s;
        }
        void Hardware(CoreState s, int major)
        {
            CoreRejectedException failed = null;
            foreach (byte candidate in major >= 2 ? new byte[] { 0x61, 0x60 } : new byte[] { 0x60, 0x61 })
            {
                try
                {
                    var modes = CoreProtocol.Values(ReadData(candidate, true, 3), count, false);
                    var powers = CoreProtocol.Values(ReadData((byte)(candidate + 1), true, 4), count, true);
                    if (powers.Any(v => v > 100)) throw new InvalidDataException("CORE fixed percentage is outside 0-100.");
                    modeEndpoint = candidate; powerEndpoint = (byte)(candidate + 1); s.Modes = modes; s.Powers = powers; return;
                } catch (CoreRejectedException ex) { failed = ex; }
            }
            throw new CoreRejectedException("CORE hardware settings cannot be captured; control is disabled. " + (failed == null ? "" : failed.Message));
        }
        public CoreState Read() { return Operation(State); }
        static void Selection(bool[] selected, CoreState s)
        {
            if (!s.Writable || selected == null || selected.Length != s.Connected.Length || !selected.Any(v => v)) throw new InvalidDataException("CORE has no valid capture/selection. " + s.ControlError);
            for (int i = 0; i < selected.Length; i++) if (selected[i] && (!s.Connected[i] || (s.Modes[i] != 0 && s.Modes[i] != 2))) throw new IOException("Selected CORE output is absent or uses an unsupported hardware mode.");
        }
        public CoreBaseline Capture(bool[] selected)
        {
            return Operation(delegate { var s = State(); Selection(selected, s); return new CoreBaseline { Selected = (bool[])selected.Clone(), Modes = (int[])s.Modes.Clone(), Powers = (int[])s.Powers.Clone() }; });
        }
        void WriteData(byte endpoint, byte type, byte[] data, Func<bool> allowed)
        {
            ReadData(endpoint, true, type);
            Send(new byte[] { 13, Handle }, endpoint, 0x6d);
            try
            {
                var payload = new byte[6 + data.Length]; payload[0] = (byte)(data.Length + 2); payload[1] = (byte)((data.Length + 2) >> 8); payload[4] = type; Array.Copy(data, 0, payload, 6, data.Length);
                if (allowed != null && !allowed()) throw new IOException("CORE control session stopped before a settings write.");
                Send(new byte[] { 6, Handle }, payload);
            }
            finally { Send(new byte[] { 5, 1, Handle }); }
        }
        public void Apply(int[] desired, int[] previous, Func<bool> allowed)
        {
            if (desired == null || previous == null || desired.Length != count || previous.Length != count || desired.Any(v => v < -1 || v > 100)) throw new InvalidDataException("Invalid CORE duties.");
            Operation(delegate
            {
                var s = State(); var selected = desired.Select(v => v >= 0).ToArray(); Selection(selected, s);
                for (int i = 0; i < count; i++) if (selected[i] && previous[i] >= 0 && (s.Modes[i] != 0 || s.Powers[i] != previous[i])) throw new IOException("Another app changed a managed CORE output; restore before restarting.");
                var powers = (int[])s.Powers.Clone(); var modes = (int[])s.Modes.Clone();
                for (int i = 0; i < count; i++) if (selected[i]) { powers[i] = desired[i]; modes[i] = 0; }
                if (!powers.SequenceEqual(s.Powers)) WriteData(powerEndpoint, 4, CoreProtocol.Encode(powers, true), allowed);
                if (!modes.SequenceEqual(s.Modes)) WriteData(modeEndpoint, 3, CoreProtocol.Encode(modes, false), allowed);
                Verify(selected, modes, powers); return true;
            });
        }
        void Verify(bool[] selected, int[] modes, int[] powers)
        {
            var actualModes = CoreProtocol.Values(ReadData(modeEndpoint, true, 3), count, false); var actualPowers = CoreProtocol.Values(ReadData(powerEndpoint, true, 4), count, true);
            for (int i = 0; i < count; i++) if (actualModes[i] != modes[i] || actualPowers[i] != powers[i]) throw new IOException("CORE settings readback differs; recovery remains required.");
        }
        public void Restore(CoreBaseline baseline)
        {
            if (baseline == null || baseline.Modes == null || baseline.Powers == null || baseline.Modes.Length != count || baseline.Powers.Length != count) throw new InvalidDataException("Invalid CORE recovery record.");
            Operation(delegate
            {
                var s = State(); Selection(baseline.Selected, s);
                var powers = (int[])s.Powers.Clone(); var modes = (int[])s.Modes.Clone();
                for (int i = 0; i < count; i++) if (baseline.Selected[i]) { if ((baseline.Modes[i] != 0 && baseline.Modes[i] != 2) || baseline.Powers[i] < 0 || baseline.Powers[i] > 100) throw new InvalidDataException("Invalid selected CORE baseline."); powers[i] = baseline.Powers[i]; modes[i] = baseline.Modes[i]; }
                WriteData(powerEndpoint, 4, CoreProtocol.Encode(powers, true), null); WriteData(modeEndpoint, 3, CoreProtocol.Encode(modes, false), null); Verify(baseline.Selected, modes, powers); return true;
            });
        }
        public void Dispose() { lock (io) transport.Dispose(); }
    }
}

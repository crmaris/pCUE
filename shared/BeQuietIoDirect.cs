using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Pcue.Cooling
{
    internal interface IBeQuietReportTransport : IDisposable
    {
        int ReadReport(byte[] buffer);
        void WriteReport(byte[] report);
    }
    internal interface IBeQuietControlClient : IDisposable
    {
        string Error { get; }
        string CoolerSerial { get; }
        string FirmwareVersion { get; }
        BeQuietIoReading[] Fresh { get; }
        void Heartbeat();
        void Status();
        int ReadDuty(int channel);
        int ReadCoolingMode();
        void SetDuty(int channel, int duty, Func<bool> permitted = null);
        void SetSoftwareControl(bool enabled, Func<bool> permitted = null);
    }
    internal sealed class BeQuietWriteCancelledException : IOException { internal BeQuietWriteCancelledException() : base("Cooling session stopped before a native settings write.") { } }
    // Only the observed MI00 protocol families: heartbeat, status, fixed PWM.
    // Roles are supplied by a commissioned mapping, never guessed from RPM or order.
    internal static class BeQuietIoCommands
    {
        internal static byte[] Probe(byte node, byte sequence)
        {
            var b = Header(6, node, sequence, 1); return Finish(b);
        }
        internal static byte[] Open(byte sequence)
        {
            var b = new byte[64]; b[0] = 11; b[4] = sequence; b[5] = b[6] = 1;
            b[7] = 0x34; b[8] = 0x12; b[11] = 1; return Finish(b);
        }
        internal static byte[] OpenCooler(byte session, byte sequence, byte inner)
        {
            var b = Command(18, session, sequence, inner, 1, 1);
            b[9] = 0; b[14] = 0x34; b[15] = 0x12; b[18] = 1; return Finish(b);
        }
        internal static byte[] Status(byte node, byte sequence, byte inner, byte coolerSession = 1)
        {
            var b = Command(13, node, sequence, inner, 1, 3); b[9] = coolerSession; return Finish(b);
        }
        internal static byte[] Duty(byte node, byte sequence, byte inner, int channel, int duty, byte coolerSession = 1)
        {
            if (channel < 1 || channel > 3) throw new ArgumentOutOfRangeException("channel");
            if (duty < 0 || duty > 100) throw new ArgumentOutOfRangeException("duty");
            var b = Command(15, node, sequence, inner, 0x31, 4);
            b[9] = coolerSession; b[14] = (byte)channel; b[15] = (byte)duty; return Finish(b);
        }
        internal static byte[] Read(byte session, byte sequence, byte inner, byte coolerSession, byte opcode, byte kind, int? channel = null)
        {
            var b = Command((byte)(channel.HasValue ? 14 : 13), session, sequence, inner, opcode, kind);
            b[9] = coolerSession; if (channel.HasValue) b[14] = (byte)channel.Value; return Finish(b);
        }
        private static byte[] Header(byte length, byte node, byte sequence, byte type)
        {
            if (node == 0) throw new ArgumentOutOfRangeException("node");
            if (sequence == 0) throw new ArgumentOutOfRangeException("sequence");
            var b = new byte[64]; b[0] = length; b[2] = node; b[4] = sequence; b[5] = type; b[6] = 3; return b;
        }
        private static byte[] Command(byte length, byte node, byte sequence, byte inner, byte opcode, byte kind)
        {
            if (inner == 0) throw new ArgumentOutOfRangeException("inner");
            var b = Header(length, node, sequence, 5); b[9] = 1; b[11] = inner; b[12] = opcode; b[13] = kind; return b;
        }
        private static byte[] Finish(byte[] b)
        {
            ushort crc = BeQuietIoProtocol.Crc(b, 62); b[62] = (byte)crc; b[63] = (byte)(crc >> 8);
            var report = new byte[65]; Array.Copy(b, 0, report, 1, 64); return report;
        }
        internal static bool Wire(byte[] report, int count, byte node, out byte[] wire)
        {
            if (!RawWire(report, count, out wire) || wire[2] != node || wire[3] != 0) { wire = null; return false; }
            return true;
        }
        internal static bool RawWire(byte[] report, int count, out byte[] wire)
        {
            wire = null;
            if (report == null || count != 65 || report.Length != 65 || report[0] != 0) return false;
            var b = new byte[64]; Array.Copy(report, 1, b, 0, 64);
            if (BeQuietIoProtocol.Crc(b, 62) != (ushort)(b[62] | b[63] << 8) || b[1] != 0 || b[0] < 6 || b[0] > 60) return false;
            wire = b; return true;
        }
        internal static bool Ack(byte[] b, byte outer, byte type)
        {
            if (b == null || b.Length != 64 || b[0] != 6 || b[4] != outer || b[5] != type || b[6] != 3) return false;
            for (int i = 7; i < 62; i++) if (b[i] != 0) return false;
            return true;
        }
        internal static bool Reply(byte[] b, byte inner, byte opcode, byte kind, byte coolerSession, out int status)
        {
            status = -1;
            if (b == null || b.Length != 64 || b[0] != 13 || b[4] != 0 || b[5] != 5 || b[6] != 5
                || b[7] != 0 || b[8] != 0 || b[9] != coolerSession || b[10] != 0 || b[11] != inner || b[12] != opcode || b[13] != kind) return false;
            for (int i = 15; i < 62; i++) if (b[i] != 0) return false;
            status = b[14]; return true;
        }
    }

    internal sealed class BeQuietIoNativeTransport : IBeQuietReportTransport
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [StructLayout(LayoutKind.Sequential)]
        private struct IoOverlapped { internal IntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle handle, IntPtr data, uint length, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint count, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
        private readonly SafeFileHandle handle;
        private readonly FileStream stream;
        internal BeQuietIoNativeTransport(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.Text.RegularExpressions.Regex.IsMatch(path, "vid_373f&pid_0010&mi_00(?:[&#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new ArgumentException("Expected IO Controller interface 00.");
            handle = CreateFile(path, 0xc0000000, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
            try { stream = new FileStream(handle, FileAccess.Read, 65, true); } catch { handle.Dispose(); throw; }
        }
        public int ReadReport(byte[] buffer) { return stream.ReadAsync(buffer, 0, buffer.Length).GetAwaiter().GetResult(); }
        public void WriteReport(byte[] output)
        {
            bool added = false; GCHandle pin = default(GCHandle); IntPtr operation = IntPtr.Zero;
            using (var completion = new ManualResetEvent(false)) {
                try {
                    handle.DangerousAddRef(ref added); pin = GCHandle.Alloc(output, GCHandleType.Pinned);
                    operation = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(IoOverlapped)));
                    Marshal.StructureToPtr(new IoOverlapped { Event = completion.SafeWaitHandle.DangerousGetHandle() }, operation, false);
                    uint count;
                    if (!WriteFile(handle, pin.AddrOfPinnedObject(), (uint)output.Length, out count, operation)) {
                        int code = Marshal.GetLastWin32Error(); if (code != 997) throw new Win32Exception(code);
                        if (!completion.WaitOne(2000)) {
                            CancelIoEx(handle, operation); GetOverlappedResult(handle, operation, out count, true);
                            throw new TimeoutException("IO output report timed out and was cancelled.");
                        }
                        if (!GetOverlappedResult(handle, operation, out count, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    if (count != output.Length) throw new IOException("Incomplete HID output report.");
                } finally { if (operation != IntPtr.Zero) Marshal.FreeHGlobal(operation); if (pin.IsAllocated) pin.Free(); if (added) handle.DangerousRelease(); }
            }
        }
        public void Dispose() { stream.Dispose(); }
    }
    internal sealed class BeQuietIoDirectClient : IBeQuietControlClient
    {
        private byte session, coolerSession;
        private readonly IBeQuietReportTransport transport;
        private readonly BeQuietIoState state = new BeQuietIoState();
        private readonly object commandGate = new object(), inputGate = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Task reader;
        private readonly Action<string, byte[], DateTime> evidence;
        private Pending pending;
        private byte outer = 1, inner = 1;
        private int disposed;
        private volatile string error;
        public string Error { get { return error; } }
        public string CoolerSerial { get; private set; }
        public string FirmwareVersion { get; private set; }
        public BeQuietIoReading[] Fresh { get { return error != null || Volatile.Read(ref disposed) != 0 ? new BeQuietIoReading[0] : state.Fresh(Stopwatch.GetTimestamp()); } }
        private sealed class Pending
        {
            internal byte Outer, Inner, Type, Opcode, Kind, Session, CoolerSession, Length;
            internal bool OpeningOuter;
            internal bool Ack, Reply;
            internal byte[] Response;
            internal string Error;
            internal readonly ManualResetEventSlim Ready = new ManualResetEventSlim();
        }
        internal BeQuietIoDirectClient(string path, string expectedCoolerSerial, Action<string, byte[], DateTime> evidence)
            : this(new BeQuietIoNativeTransport(path), expectedCoolerSerial, evidence) { }
        internal BeQuietIoDirectClient(IBeQuietReportTransport connection, string expectedCoolerSerial, Action<string, byte[], DateTime> evidence)
        {
            this.evidence = evidence;
            transport = connection;
            reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try {
                // Open our own bridge and child sessions. An OEM session cannot be reused.
                var response = Request(1, 1, 1, 13, null, true);
                if (response[12] == 0) throw new IOException("The new IO session is inactive. Another cooling session may still be expiring; wait at least six seconds after the OEM service stops, then reconnect.");
                if (response[7] != 0x34 || response[8] != 0x12 || response[9] != 0 || response[10] != 0
                    || response[11] == 0 || response[12] != 1 || response[13] != 5 || response[14] != 0)
                    throw new IOException("Unrecognized IO bridge session response.");
                session = response[11];
                response = Request(5, 1, 1, 20, null);
                if (response[14] != 0x34 || response[15] != 0x12 || response[16] != 0 || response[17] != 0
                    || response[18] == 0 || response[19] != 1 || response[20] != 5 || response[21] != 0)
                    throw new IOException("Unrecognized IO cooler session response.");
                coolerSession = response[18];
                response = Request(5, 3, 2, 26, null);
                if (response[14] != 12) throw new IOException("Unrecognized cooler serial length.");
                CoolerSerial = System.Text.Encoding.ASCII.GetString(response, 15, 12);
                if (!System.Text.RegularExpressions.Regex.IsMatch(CoolerSerial, "^[0-9A-Fa-f]{12}$")) throw new IOException("Unrecognized cooler serial.");
                if (expectedCoolerSerial != null && !string.Equals(CoolerSerial, expectedCoolerSerial, StringComparison.Ordinal))
                    throw new IOException("Connected cooler serial differs from the commissioned cooler.");
                response = Request(5, 3, 1, 21, null);
                if (response[14] != 3 || response[15] != 0x80 || response[16] != 1)
                    throw new IOException("Uncommissioned cooler model or hardware revision.");
                if (response[17] != 1 || response[18] != 0x37 || response[19] != 0 || response[20] != 0 || response[21] != 0)
                    throw new IOException("This cooler firmware has not been validated for native control.");
                FirmwareVersion = "0.0." + response[18].ToString("X2");
            } catch { Dispose(); throw; }
        }
        private static byte Next(ref byte value) { value++; if (value == 0) value = 1; return value; }
        public void Heartbeat() { Request(1, 1, 3, 6, null); }
        public void Status() { var response = Request(5, 1, 3, 13, null); if (response[14] != 0) throw new IOException("Cooler heartbeat was rejected."); }
        public int ReadDuty(int channel)
        {
            if (channel < 1 || channel > 2) throw new ArgumentOutOfRangeException("channel");
            var response = Request(5, 0x31, 3, 14, new[] { channel });
            if (response[15] != 0 || response[14] > 100) throw new IOException("Invalid PWM readback.");
            return response[14];
        }
        public void SetDuty(int channel, int duty, Func<bool> permitted = null)
        {
            if (channel < 1 || channel > 2 || duty < 0 || duty > 100) throw new ArgumentOutOfRangeException("duty");
            var response = Request(5, 0x31, 4, 13, new[] { channel, duty }, false, permitted);
            if (response[14] != 0) throw new IOException("PWM command was rejected: " + response[14]);
            if (ReadDuty(channel) != duty) throw new IOException("PWM readback differs from the requested setting.");
        }
        public int ReadCoolingMode()
        {
            var response = Request(5, 0x31, 3, 14, new[] { 3 });
            if (response[15] != 0 || response[14] > 1) throw new IOException("Unrecognized cooling control mode.");
            return response[14];
        }
        public void SetSoftwareControl(bool enabled, Func<bool> permitted = null)
        {
            int value = enabled ? 1 : 0;
            var response = Request(5, 0x31, 4, 13, new[] { 3, value }, false, permitted);
            if (response[14] != 0 || ReadCoolingMode() != value) throw new IOException("Cooling control mode was not applied.");
        }
        private byte[] Request(byte type, byte opcode, byte kind, byte length, int[] values, bool openingOuter = false, Func<bool> permitted = null)
        {
            lock (commandGate) {
                if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("IO Controller");
                if (error != null) throw new IOException(error);
                var p = new Pending { Outer = Next(ref outer), Inner = type == 1 ? (byte)0 : Next(ref inner), Type = type, Opcode = opcode,
                    Kind = kind, Session = session, CoolerSession = coolerSession, Length = length, OpeningOuter = openingOuter };
                byte[] output;
                if (openingOuter) output = BeQuietIoCommands.Open(p.Outer);
                else if (type == 1) output = BeQuietIoCommands.Probe(session, p.Outer);
                else if (opcode == 1 && kind == 1) output = BeQuietIoCommands.OpenCooler(session, p.Outer, p.Inner);
                else if (opcode == 0x31 && kind == 4) output = BeQuietIoCommands.Duty(session, p.Outer, p.Inner, values[0], values[1], coolerSession);
                else output = BeQuietIoCommands.Read(session, p.Outer, p.Inner, coolerSession, opcode, kind, values == null ? (int?)null : values[0]);
                lock (inputGate) pending = p;
                try {
                    if (permitted != null && !permitted()) throw new BeQuietWriteCancelledException();
                    transport.WriteReport(output);
                    if (evidence != null) evidence("OUT", output, DateTime.UtcNow);
                    if (!p.Ready.Wait(2000)) throw new TimeoutException("IO Controller acknowledgement timed out.");
                    if (p.Error != null) throw new IOException(p.Error);
                    return p.Response;
                } catch (BeQuietWriteCancelledException) { throw; } catch (Exception ex) { Fail(ex); throw; }
                finally { lock (inputGate) { if (pending == p) pending = null; p.Ready.Dispose(); } }
            }
        }
        private void ReadLoop()
        {
            try {
                while (!lifetime.IsCancellationRequested) {
                    var data = new byte[65]; int count = transport.ReadReport(data);
                    if (count == 0) throw new EndOfStreamException("IO input ended.");
                    DateTime utc = DateTime.UtcNow; if (evidence != null) evidence("IN", data, utc);
                    BeQuietIoReading sample;
                    if (BeQuietIoProtocol.TryDecode(data, count, utc, out sample) && sample.Session == session && sample.CoolerSession == coolerSession)
                        state.Accept(data, count, utc, Stopwatch.GetTimestamp());
                    byte[] wire; if (!BeQuietIoCommands.RawWire(data, count, out wire)) continue;
                    lock (inputGate) {
                        var p = pending; if (p == null) continue;
                        if (wire[2] != p.Session) continue;
                        if (wire[3] != 0 && wire[4] == p.Outer && wire[5] == p.Type) {
                            p.Error = "IO Controller rejected the communication session: " + wire[3]; p.Ready.Set(); continue;
                        }
                        if (wire[3] != 0) continue;
                        if (p.Type == 5 && wire[4] == 0 && wire[5] == 5 && wire[6] == 5 && wire[7] == 0 && wire[8] == 0
                            && wire[9] == p.CoolerSession && wire[10] != 0 && wire[11] == p.Inner && wire[12] == p.Opcode && wire[13] == p.Kind) {
                            p.Error = "Cooler rejected the command: " + wire[10]; p.Ready.Set(); continue;
                        }
                        if (p.OpeningOuter && wire[0] == p.Length && wire[4] == p.Outer && wire[5] == 1 && wire[6] == 1 && Padding(wire, 15)) {
                            p.Response = wire; p.Ready.Set(); continue;
                        }
                        if (BeQuietIoCommands.Ack(wire, p.Outer, p.Type)) p.Ack = true;
                        if (p.Type == 5 && wire[0] == p.Length && wire[4] == 0 && wire[5] == 5 && wire[6] == 5
                            && wire[7] == 0 && wire[8] == 0 && wire[9] == p.CoolerSession && wire[10] == 0
                            && wire[11] == p.Inner && wire[12] == p.Opcode && wire[13] == p.Kind && Padding(wire, p.Length + 2)) {
                            p.Reply = true; p.Response = wire;
                        }
                        if (p.Ack && (p.Type == 1 || p.Reply)) { if (p.Type == 1) p.Response = wire; p.Ready.Set(); }
                    }
                }
            } catch (Exception ex) { if (!lifetime.IsCancellationRequested) Fail(ex); }
            finally { state.Clear(); }
        }
        private static bool Padding(byte[] wire, int start) { for (int i = start; i < 62; i++) if (wire[i] != 0) return false; return true; }
        private void Fail(Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message; state.Clear();
            lock (inputGate) if (pending != null) { pending.Error = error; pending.Ready.Set(); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel(); state.Clear(); transport.Dispose();
            // The single input worker owns its cancelled read until the OS finishes it.
            reader.ContinueWith(t => lifetime.Dispose(), TaskScheduler.Default);
        }
    }
}

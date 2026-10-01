using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Pcue.Cooling
{
    // Read-only protocol subset observed on IO Controller 373F:0010, interface 00.
    // Channel identities are raw protocol identities; fan/pump roles require commissioning.
    public sealed class BeQuietIoReading
    {
        public int Channel { get; internal set; }
        public byte Session { get; internal set; }
        public byte CoolerSession { get; internal set; }
        public int RawValue { get; internal set; }
        public DateTime ReceivedUtc { get; internal set; }
        public double Value { get { return Channel == 0 ? RawValue / 10.0 : RawValue; } }
        public string Unit { get { return Channel == 0 ? "C" : Channel == 3 ? "Condition" : "RPM"; } }
    }

    internal static class BeQuietIoProtocol
    {
        internal static ushort Crc(byte[] data, int length)
        {
            ushort crc = 0xffff;
            for (int i = 0; i < length; i++) {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0xa001 : 0));
            }
            return crc;
        }

        internal static bool TryDecode(byte[] report, int count, DateTime receivedUtc, out BeQuietIoReading reading)
        {
            reading = null;
            if (report == null || count != report.Length || (count != 64 && count != 65)) return false;
            int offset = count == 65 ? 1 : 0;
            if (offset == 1 && report[0] != 0) return false;
            var wire = new byte[64]; Array.Copy(report, offset, wire, 0, 64);
            if (Crc(wire, 62) != (ushort)(wire[62] | wire[63] << 8)) return false;
            if ((wire[0] != 0x0f && wire[0] != 0x10) || wire[1] != 0) return false;
            // IO Center allocates both communication sessions when it connects.
            // These are not firmware constants or physical fan/pump identifiers.
            if (wire[2] == 0 || wire[9] == 0 || wire[3] != 0 || wire[4] != 0
                || wire[5] != 5 || wire[6] != 5 || wire[7] != 0 || wire[8] != 0
                || wire[10] != 0 || wire[11] != 0 || wire[12] != 0x30 || wire[13] != 2) return false;
            for (int i = 17; i < 62; i++) if (wire[i] != 0) return false;
            if (wire[14] > 3 || receivedUtc.Kind != DateTimeKind.Utc) return false;
            int raw = wire[15] | wire[16] << 8;
            // Bounds reject unusable sensor data; they do not infer settings or PWM duty.
            if ((wire[14] == 0 && raw > 1500) || (wire[14] == 3 && raw > 255) || (wire[14] > 0 && wire[14] < 3 && raw > 20000)) return false;
            reading = new BeQuietIoReading { Session = wire[2], CoolerSession = wire[9], Channel = wire[14], RawValue = raw, ReceivedUtc = receivedUtc };
            return true;
        }
    }

    internal sealed class BeQuietIoState
    {
        private readonly object gate = new object();
        private readonly BeQuietIoReading[] readings = new BeQuietIoReading[4];
        private readonly long[] timestamps = new long[4];
        private byte session, coolerSession;
        private bool ambiguous;
        internal bool Accept(byte[] data, int count, DateTime utc, long monotonicTicks)
        {
            BeQuietIoReading reading;
            if (!BeQuietIoProtocol.TryDecode(data, count, utc, out reading)) return false;
            lock (gate) {
                if (ambiguous) return false;
                if (session != 0 && (session != reading.Session || coolerSession != reading.CoolerSession)) {
                    ambiguous = true; Array.Clear(readings, 0, 4); Array.Clear(timestamps, 0, 4); return false;
                }
                session = reading.Session; coolerSession = reading.CoolerSession; readings[reading.Channel] = reading; timestamps[reading.Channel] = monotonicTicks;
            }
            return true;
        }
        internal BeQuietIoReading[] Fresh(long nowTicks, double maxAgeSeconds = 3)
        {
            var fresh = new List<BeQuietIoReading>();
            lock (gate) for (int channel = 0; !ambiguous && channel < 4; channel++) {
                if (readings[channel] == null) continue;
                double age = (nowTicks - timestamps[channel]) / (double)Stopwatch.Frequency;
                if (age >= 0 && age <= maxAgeSeconds) fresh.Add(new BeQuietIoReading {
                    Session = readings[channel].Session, CoolerSession = readings[channel].CoolerSession, Channel = channel, RawValue = readings[channel].RawValue, ReceivedUtc = readings[channel].ReceivedUtc });
            }
            return fresh.ToArray();
        }
        internal void Clear() { lock (gate) { Array.Clear(readings, 0, 4); Array.Clear(timestamps, 0, 4); session = coolerSession = 0; ambiguous = false; } }
    }

}

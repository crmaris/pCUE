// Independent implementation of documented OCTO report fields and HID operations.
// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HidSharp;

namespace Pcue.Cooling
{
    public interface IOctoTransport : IDisposable
    {
        byte[] Settings();
        byte[] Status();
        void Send(byte[] report, Func<bool> allowed);
    }
    public static class OctoReports
    {
        public static bool SettingsLength(int n) { return n == 1613 || n == 1631; }
        public static bool StatusLength(int n) { return n == 263 || n == 327; }
        public static int Word(byte[] data, int at) { return (data[at] << 8) | data[at + 1]; }
        public static void Word(byte[] data, int at, int value) { data[at] = (byte)(value >> 8); data[at + 1] = (byte)value; }
        public static int Offset(byte[] report, int channel) { return (report.Length == 1613 ? 72 : 90) + 85 * channel; }
        public static ushort Checksum(byte[] report)
        {
            int accumulator = 65535;
            for (int at = 1; at < report.Length - 2; at++)
            {
                accumulator ^= report[at];
                for (int bit = 0; bit != 8; bit++) accumulator = (accumulator >> 1) ^ ((accumulator & 1) == 0 ? 0 : 40961);
            }
            return (ushort)(accumulator ^ 65535);
        }
        public static void Validate(byte[] report)
        {
            if (report == null || !SettingsLength(report.Length) || report[0] != 3 || Word(report, 1) != 1 || Word(report, report.Length - 2) != Checksum(report)) throw new InvalidDataException("Unsupported or corrupt OCTO settings report.");
        }
        public static void Mask(bool[] selected) { if (selected == null || selected.Length != 8 || !selected.Any(v => v)) throw new InvalidDataException("Choose OCTO outputs explicitly."); }
        public static byte[] Merge(byte[] latest, byte[] wanted, bool[] selected)
        {
            Validate(latest); Validate(wanted); Mask(selected);
            if (latest.Length != wanted.Length) throw new InvalidDataException("OCTO firmware layout changed; retain recovery.");
            var command = (byte[])latest.Clone();
            for (int channel = 0; channel < 8; channel++) if (selected[channel]) Buffer.BlockCopy(wanted, Offset(wanted, channel), command, Offset(command, channel), 3);
            Word(command, command.Length - 2, Checksum(command)); return command;
        }
        public static CoreState Decode(byte[] telemetry, byte[] settings)
        {
            if (telemetry == null || !StatusLength(telemetry.Length) || telemetry[0] != 1) throw new InvalidDataException("Unsupported OCTO telemetry report.");
            Validate(settings);
            var state = new CoreState { Firmware = Word(telemetry, 13).ToString(), ChildSerial = Word(telemetry,3).ToString("D5") + "-" + Word(telemetry,5).ToString("D5"), Connected = Enumerable.Repeat(true, 8).ToArray(), Rpm = new int?[8], Temperatures = new double?[4], Modes = new int[8], Powers = new int[8], OutputNames = new string[8] };
            for (int i = 0; i < 8; i++) { int offset = Offset(settings, i); state.Rpm[i] = Word(telemetry, 133 + 13 * i); state.Modes[i] = settings[offset] == 0 ? 0 : 2; state.Powers[i] = Word(settings, offset + 1) / 100; state.OutputNames[i] = "OCTO fan " + (i + 1); }
            for (int i = 0; i < 4; i++) { int raw = Word(telemetry, 61 + 2 * i); state.Temperatures[i] = raw == 32767 ? null : (double?)(raw / 100.0); }
            return state;
        }
    }
    public sealed class OctoHidTransport : IOctoTransport
    {
        readonly HidStream stream;
        readonly Stopwatch clock = Stopwatch.StartNew();
        long operation = -1000;
        public OctoHidTransport(string path)
        {
            var matches = DeviceList.Local.GetHidDevices(0x0c70, 0xf011).Where(d => d.DevicePath == path && OctoReports.SettingsLength(d.GetMaxFeatureReportLength()) && OctoReports.StatusLength(d.GetMaxInputReportLength())).ToArray();
            if (matches.Length != 1) throw new IOException("The exact saved OCTO interface is unavailable.");
            stream = matches[0].Open(); stream.ReadTimeout = stream.WriteTimeout = 1800;
        }
        void Pace() { int delay = 350 - (int)(clock.ElapsedMilliseconds - operation); if (delay > 0) Thread.Sleep(delay); }
        public byte[] Settings()
        {
            var report = new byte[stream.Device.GetMaxFeatureReportLength()]; report[0] = 3;
            Pace(); stream.GetFeature(report); operation = clock.ElapsedMilliseconds; OctoReports.Validate(report); return report;
        }
        public byte[] Status()
        {
            byte[] latest = null, report = new byte[stream.Device.GetMaxInputReportLength()]; long deadline = clock.ElapsedMilliseconds + 1900;
            try {
                stream.ReadTimeout = 1800;
                for (int i = 0; i < 64 && clock.ElapsedMilliseconds < deadline; i++) {
                    try { int n = stream.Read(report, 0, report.Length); if (OctoReports.StatusLength(n) && report[0] == 1) latest = report.Take(n).ToArray(); stream.ReadTimeout = 10; }
                    catch (TimeoutException) { if (latest == null) throw; break; }
                }
            } finally { stream.ReadTimeout = 1800; }
            if (latest == null) throw new IOException("Fresh OCTO telemetry is unavailable."); return latest;
        }
        public void Send(byte[] report, Func<bool> allowed)
        {
            OctoReports.Validate(report); Pace();
            if (allowed != null && !allowed()) throw new IOException("OCTO control was stopped before this write.");
            stream.SetFeature(report); operation = clock.ElapsedMilliseconds;
        }
        public void Dispose() { stream.Dispose(); }
    }
    public sealed class OctoCoolingController : INativeCoolingController
    {
        readonly IOctoTransport transport;
        bool[] owned;
        string serial;
        public int Count { get { return 8; } }
        public bool HasExt { get { return false; } }
        public OctoCoolingController(IOctoTransport transport, string expectedSerial = null) { this.transport = transport ?? throwNull(); serial = expectedSerial; }
        static IOctoTransport throwNull() { throw new ArgumentNullException("transport"); }
        public CoreState Read() { var state = OctoReports.Decode(transport.Status(), transport.Settings()); if (serial != null && serial != state.ChildSerial) throw new IOException("The saved OCTO serial does not match this controller."); serial = state.ChildSerial; return state; }
        public CoreBaseline Capture(bool[] selected)
        {
            OctoReports.Mask(selected); Read(); byte[] report = transport.Settings(); OctoReports.Validate(report); owned = (bool[])selected.Clone();
            return new CoreBaseline { Selected = (bool[])selected.Clone(), Report = (byte[])report.Clone(), ChildSerial = serial };
        }
        public byte[] InspectSettings() { byte[] report = transport.Settings(); OctoReports.Validate(report); return report; }
        void Write(byte[] wanted, bool[] selected, Func<bool> allowed)
        {
            for (int attempt = 0; attempt < 3; attempt++) {
                transport.Send(OctoReports.Merge(transport.Settings(), wanted, selected), allowed);
                for (int poll = 0; poll < 3; poll++) {
                    byte[] actual = transport.Settings(); OctoReports.Validate(actual);
                    if (actual.Length != wanted.Length) throw new IOException("OCTO layout changed during verification.");
                    bool match = true;
                    for (int i = 0; i < 8; i++) if (selected[i]) for (int n = 0; n < 3; n++) if (actual[OctoReports.Offset(actual, i) + n] != wanted[OctoReports.Offset(wanted, i) + n]) match = false;
                    if (match) return;
                }
            }
            throw new IOException("OCTO ignored the bounded write/readback attempts. Retain recovery.");
        }
        public void Apply(int[] desired, int[] previous, Func<bool> allowed)
        {
            if (desired == null || previous == null || desired.Length != 8 || previous.Length != 8 || desired.Any(v => v < -1 || v > 100) || previous.Any(v => v < -1 || v > 100)) throw new InvalidDataException("Invalid OCTO percentages.");
            bool[] selected = desired.Select(v => v >= 0).ToArray(); OctoReports.Mask(selected);
            if (owned == null || !selected.SequenceEqual(owned)) throw new IOException("Capture the exact OCTO output selection before writing.");
            byte[] current = transport.Settings(); OctoReports.Validate(current);
            var command = (byte[])current.Clone();
            for (int i = 0; i < 8; i++) if (selected[i]) {
                int at = OctoReports.Offset(current, i);
                if (previous[i] >= 0 && (current[at] != 0 || OctoReports.Word(current, at + 1) != previous[i] * 100)) throw new IOException("OCTO output was changed by another controller application.");
                command[at] = 0; OctoReports.Word(command, at + 1, desired[i] * 100);
            }
            if (desired.SequenceEqual(previous)) return;
            OctoReports.Word(command, command.Length - 2, OctoReports.Checksum(command)); Write(command, selected, allowed);
        }
        public void Restore(CoreBaseline baseline)
        {
            if (baseline == null) throw new InvalidDataException("Missing OCTO recovery.");
            OctoReports.Mask(baseline.Selected); OctoReports.Validate(baseline.Report);
            if (baseline.ChildSerial != null && baseline.ChildSerial != Read().ChildSerial) throw new IOException("OCTO recovery serial mismatch. No settings restored.");
            Write(baseline.Report, baseline.Selected, null); owned = null;
        }
        public void Dispose() { transport.Dispose(); }
    }
}

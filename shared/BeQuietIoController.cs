// Owner-authored, commissioned IO transport with a shared application policy.
// No serial numbers, capture bytes or private lab evidence are embedded here.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Pcue.Cooling
{
    public sealed class BeQuietIoController : INativeCoolingController
    {
        readonly object gate = new object();
        IBeQuietControlClient client;
        readonly Func<IBeQuietControlClient> reconnect;
        readonly Timer heartbeat;
        readonly int minimumPumpDuty;
        Func<bool> lease;
        bool claimed, tripped, disposed, emergencyParked, verifiedPumpStop;
        string controlError;
        public int Count { get { return 2; } }
        public bool HasExt { get { return false; } }
        // Already received samples only: do not poll USB or manufacture a fresh timestamp.
        public BeQuietIoReading[] Fresh
        {
            get { lock (gate) return disposed ? new BeQuietIoReading[0] : client.Fresh.Select(x => new BeQuietIoReading { Channel = x.Channel, Session = x.Session, CoolerSession = x.CoolerSession, RawValue = x.RawValue, ReceivedUtc = x.ReceivedUtc }).ToArray(); }
        }
        internal BeQuietIoController(IBeQuietControlClient connection, bool startHeartbeat, int minimumPumpDuty = 25)
        {
            ValidateMinimum(minimumPumpDuty);
            this.minimumPumpDuty = minimumPumpDuty;
            client = connection;
            if (startHeartbeat) heartbeat = new Timer(delegate { try { Tick(); } catch (Exception ex) { lock (gate) { controlError = ex.Message; tripped = true; } } }, null, 0, 1000);
        }
        static void ValidateMinimum(int value)
        {
            if (value < 0 || value > 25) throw new InvalidDataException("IO minimum pump duty must be between 0 and 25 percent.");
        }
        static IBeQuietControlClient Connect(string path, string bridgeSerial, string childSerial, int minimumPumpDuty, Action<string, byte[], DateTime> reportObserver)
        {
            ValidateMinimum(minimumPumpDuty); // Reject policy before opening any interface.
            var matches = HidSharp.DeviceList.Local.GetHidDevices(0x373f, 0x0010).Where(d => string.Equals(d.DevicePath, path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (string.IsNullOrWhiteSpace(bridgeSerial) || matches.Length != 1 || matches[0].GetSerialNumber() != bridgeSerial || matches[0].GetMaxInputReportLength() != 65 || matches[0].GetMaxOutputReportLength() != 65) throw new IOException("Commissioned IO bridge identity/interface is unavailable.");
            foreach (string name in new[] { "IO_Center", "bequietIOCenterService" }) { var processes = Process.GetProcessesByName(name); try { if (processes.Length != 0) throw new IOException("Release IO Center and its service, wait six seconds, then reconnect native cooling."); } finally { foreach (var p in processes) p.Dispose(); } }
            return new BeQuietIoDirectClient(path, childSerial, reportObserver);
        }
        public BeQuietIoController(string path, string bridgeSerial, string expectedChildSerial) : this(path, bridgeSerial, expectedChildSerial, 25, null) { }
        // Case's owner explicitly permits manual 0-100. Other apps retain the default 25 floor.
        public BeQuietIoController(string path, string bridgeSerial, string expectedChildSerial, int minimumPumpDuty, Action<string, byte[], DateTime> reportObserver = null)
            : this(Connect(path, bridgeSerial, expectedChildSerial, minimumPumpDuty, reportObserver), true, minimumPumpDuty)
        {
            string learned = client.CoolerSerial;
            reconnect = delegate { Thread.Sleep(6000); return Connect(path, bridgeSerial, learned, minimumPumpDuty, reportObserver); };
        }
        void Live() { if (disposed) throw new ObjectDisposedException("be quiet IO"); if (client.Error != null) throw new IOException(client.Error); }
        static bool Usable(BeQuietIoReading[] samples, bool permitStoppedPump)
        {
            var pump = samples.FirstOrDefault(x => x.Channel == 1);
            var fan = samples.FirstOrDefault(x => x.Channel == 2);
            var fault = samples.FirstOrDefault(x => x.Channel == 3);
            return pump != null && fan != null && (pump.RawValue > 0 || (permitStoppedPump && pump.RawValue == 0)) && fault != null && fault.RawValue == 0;
        }
        internal void Tick()
        {
            lock (gate)
            {
                if (disposed) return;
                Live();
                // Zero RPM is valid only for our verified, explicit zero-duty software command.
                bool stopped = verifiedPumpStop && claimed && minimumPumpDuty == 0 && client.ReadDuty(1) == 0 && client.ReadCoolingMode() == 1;
                bool expired = claimed && !Usable(client.Fresh, stopped);
                if (claimed && lease != null) { try { expired |= !lease(); } catch { expired = true; } }
                if (expired && !tripped)
                {
                    tripped = true; controlError = "Cooling session expired. Full-speed fallback attempted; restore before restarting.";
                    verifiedPumpStop = false;
                    // A cooperative safety worker uses the existing exclusive handle.
                    // A separate process cannot open MI00 while this owner holds it.
                    client.SetDuty(1, 100); client.SetDuty(2, 100); client.SetSoftwareControl(true);
                    emergencyParked = true;
                }
                if (!tripped || emergencyParked) { client.Heartbeat(); client.Status(); }
                // Only a verified full-speed park retains heartbeat after a trip.
            }
        }
        CoreState State(bool permitRecovery)
        {
            Live();
            int pump = client.ReadDuty(1), fan = client.ReadDuty(2), mode = client.ReadCoolingMode();
            if (pump < 0 || pump > 100 || fan < 0 || fan > 100 || mode < 0 || mode > 1) throw new InvalidDataException("Unsupported IO control values.");
            var samples = client.Fresh;
            var pumpSample = samples.FirstOrDefault(x => x.Channel == 1); var fanSample = samples.FirstOrDefault(x => x.Channel == 2);
            var liquid = samples.FirstOrDefault(x => x.Channel == 0); var fault = samples.FirstOrDefault(x => x.Channel == 3);
            bool stopped = verifiedPumpStop && claimed && minimumPumpDuty == 0 && pump == 0 && mode == 1;
            bool usable = !tripped && Usable(samples, stopped);
            string error = tripped ? controlError : !usable ? "Fresh pump/fan-bank/error telemetry is required before normal control." : null;
            return new CoreState { Firmware = client.FirmwareVersion, ChildSerial = client.CoolerSerial,
                Connected = new[] { pumpSample != null, fanSample != null }, Rpm = new[] { pumpSample == null ? (int?)null : pumpSample.RawValue, fanSample == null ? (int?)null : fanSample.RawValue },
                Temperatures = new[] { liquid == null ? (double?)null : liquid.Value },
                Modes = usable || permitRecovery ? new[] { mode == 1 ? 0 : 2, mode == 1 ? 0 : 2 } : null,
                Powers = usable || permitRecovery ? new[] { pump, fan } : null, ControlError = error };
        }
        public CoreState Read() { lock (gate) return State(false); }
        static void Pair(bool[] selected)
        {
            if (selected == null || selected.Length != 2 || !selected.All(v => v)) throw new InvalidDataException("IO software mode controls the pump and fan bank together. Identify and enable both outputs before claiming the pair.");
        }
        public CoreBaseline Capture(bool[] selected)
        {
            lock (gate)
            {
                Pair(selected); var state = State(false);
                if (!state.Writable) throw new IOException(state.ControlError);
                return new CoreBaseline { Selected = (bool[])selected.Clone(), Modes = state.Modes, Powers = state.Powers, GlobalMode = state.Modes[0] == 0 ? 1 : 0, ChildSerial = state.ChildSerial };
            }
        }
        public void Apply(int[] desired, int[] previous, Func<bool> allowed)
        {
            if (desired == null || previous == null || desired.Length != 2 || previous.Length != 2 || desired[0] < minimumPumpDuty || desired[0] > 100 || desired[1] < 0 || desired[1] > 100) throw new InvalidDataException("IO requires its whole pair: pump " + minimumPumpDuty + "–100%, fan bank 0–100%. Tests must retain pump 100%.");
            lock (gate)
            {
                bool emergency = desired.All(v => v == 100) && previous.All(v => v == -1) && allowed == null;
                var state = State(emergency);
                if (tripped && !emergency) throw new IOException(controlError);
                if (!state.Writable) throw new IOException(state.ControlError);
                for (int i = 0; i < 2; i++) if (previous[i] >= 0 && (state.Modes[i] != 0 || state.Powers[i] != previous[i])) throw new IOException("Another owner changed IO settings; restore before restarting.");
                if (allowed != null && !allowed()) throw new BeQuietWriteCancelledException();
                claimed = true; lease = allowed;
                // Repeated profile ticks must not kick the pump to 100% and back.
                if (!emergency && state.Modes[0] == 0 && state.Powers.SequenceEqual(desired) && (desired[0] != 0 || verifiedPumpStop)) return;
                verifiedPumpStop = false; // A partial/failed sequence must never authorize zero feedback.
                client.SetDuty(1, 100, allowed); // Established full pump before changing output mode/fans.
                client.SetDuty(2, desired[1], allowed); client.SetSoftwareControl(true, allowed);
                if (desired[0] != 100) client.SetDuty(1, desired[0], allowed);
                if (client.ReadDuty(1) != desired[0] || client.ReadDuty(2) != desired[1] || client.ReadCoolingMode() != 1) throw new IOException("IO settings readback differs; recovery is retained.");
                verifiedPumpStop = minimumPumpDuty == 0 && desired[0] == 0;
                if (emergency) { tripped = emergencyParked = true; controlError = "Cooling is parked at full speed. Restore before restarting."; }
            }
        }
        public void Restore(CoreBaseline baseline)
        {
            Pair(baseline == null ? null : baseline.Selected);
            if (baseline.Powers == null || baseline.Powers.Length != 2 || baseline.Powers.Any(v => v < 0 || v > 100) || baseline.Modes == null || baseline.Modes.Length != 2 || !baseline.GlobalMode.HasValue || baseline.GlobalMode.Value < 0 || baseline.GlobalMode.Value > 1 || baseline.Modes.Any(v => v != (baseline.GlobalMode.Value == 1 ? 0 : 2)) || string.IsNullOrWhiteSpace(baseline.ChildSerial) || baseline.ChildSerial != client.CoolerSerial) throw new InvalidDataException("IO recovery identity/mode/percentages are invalid.");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("be quiet IO");
                verifiedPumpStop = false;
                try { RestoreValues(baseline); }
                catch
                {
                    if (reconnect == null) throw;
                    client.Dispose(); client = reconnect(); RestoreValues(baseline);
                }
                claimed = tripped = emergencyParked = false; lease = null; controlError = null;
            }
        }
        void RestoreValues(CoreBaseline baseline)
        {
            Live(); if (baseline.ChildSerial != client.CoolerSerial) throw new IOException("Recovery cooler serial changed.");
            client.SetDuty(1, 100); client.SetDuty(2, baseline.Powers[1]); client.SetSoftwareControl(baseline.GlobalMode.Value == 1);
            if (client.ReadDuty(1) != 100 || client.ReadDuty(2) != baseline.Powers[1] || client.ReadCoolingMode() != baseline.GlobalMode.Value) throw new IOException("IO restoration readback differs.");
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; if (heartbeat != null) heartbeat.Dispose(); client.Dispose(); } }
    }
}

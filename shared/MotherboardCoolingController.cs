// Shared IControl adapter; compile against each application's pinned LHM release.
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace Pcue.Cooling
{
    public class BoardControlBaseline
    {
        public string Id { get; set; }
        public string Mode { get; set; }
        public float Value { get; set; }
        public string ControllerId { get; set; }
    }
    public interface IBoardControlPort
    {
        string Id { get; }
        string Name { get; }
        string Mode { get; }
        float SoftwareValue { get; }
        float Minimum { get; }
        float Maximum { get; }
        float? Duty { get; }
        int? Rpm { get; }
        void Update();
        void Software(float value);
        void Default();
    }
    public static class BoardControlPolicy
    {
        public static bool Valid(float value) { return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= 100; }
        public static BoardControlBaseline Capture(IBoardControlPort port, string controller)
        {
            if (port == null || string.IsNullOrWhiteSpace(port.Id) || !Valid(port.Minimum) || !Valid(port.Maximum) || port.Minimum > port.Maximum || (port.Mode != "Software" && port.Mode != "Default" && port.Mode != "Undefined") || (port.Mode == "Software" && (!Valid(port.SoftwareValue) || port.SoftwareValue < port.Minimum || port.SoftwareValue > port.Maximum))) throw new InvalidDataException("Motherboard control baseline cannot be verified.");
            return new BoardControlBaseline { Id = port.Id, ControllerId = controller, Mode = port.Mode, Value = port.Mode == "Software" ? port.SoftwareValue : 0 };
        }
        public static void Set(IBoardControlPort port, float value, Func<bool> allowed)
        {
            if (port == null || !Valid(value) || !Valid(port.Minimum) || !Valid(port.Maximum) || port.Minimum > port.Maximum || value < port.Minimum || value > port.Maximum) throw new InvalidDataException("Motherboard percentage is outside the exposed control range.");
            if (allowed != null && !allowed()) throw new IOException("Motherboard control stopped before this write.");
            port.Software(value); port.Update();
            if (port.Mode != "Software" || !port.Duty.HasValue || !Valid(port.Duty.Value) || Math.Abs(port.Duty.Value - value) > 2) throw new IOException("Motherboard PWM readback does not match. Retain recovery and check BIOS fan mode.");
        }
        public static void Restore(IBoardControlPort port, BoardControlBaseline baseline)
        {
            if (baseline == null || port == null || port.Id != baseline.Id || (baseline.Mode != "Software" && baseline.Mode != "Default" && baseline.Mode != "Undefined")) throw new InvalidDataException("Motherboard recovery identity/mode mismatch.");
            if (baseline.Mode == "Software") Set(port, baseline.Value, null);
            else { port.Default(); port.Update(); if (port.Mode != "Default") throw new IOException("Motherboard BIOS/default handback failed. Retain recovery."); }
        }
    }
    public sealed class LhmBoardPort : IBoardControlPort
    {
        readonly ISensor sensor;
        public LhmBoardPort(ISensor sensor, string id) { this.sensor = sensor; Id = id; }
        public string Id { get; private set; }
        public string Name { get { return sensor.Name; } }
        public string Mode { get { return sensor.Control.ControlMode.ToString(); } }
        public float SoftwareValue { get { return sensor.Control.SoftwareValue; } }
        public float Minimum { get { return sensor.Control.MinSoftwareValue; } }
        public float Maximum { get { return sensor.Control.MaxSoftwareValue; } }
        public float? Duty { get { return sensor.Value; } }
        public int? Rpm { get { var rpm = sensor.Hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Index == sensor.Index); return rpm != null && rpm.Value.HasValue && rpm.Value.Value >= 0 && !float.IsNaN(rpm.Value.Value) && !float.IsInfinity(rpm.Value.Value) ? (int?)rpm.Value.Value : null; } }
        public void Update() { sensor.Hardware.Update(); }
        public void Software(float value) { sensor.Control.SetSoftware(value); }
        public void Default() { sensor.Control.SetDefault(); }
    }
    public sealed class MotherboardCoolingController : INativeCoolingController
    {
        readonly string identity;
        readonly IBoardControlPort[] ports;
        readonly Action release;
        bool[] owned;
        public int Count { get { return ports.Length; } }
        public bool HasExt { get { return false; } }
        public MotherboardCoolingController(string identity, IBoardControlPort[] ports, Action release)
        {
            if (string.IsNullOrWhiteSpace(identity) || ports == null || ports.Length == 0 || ports.Length > 32 || ports.Where(p => p != null).Select(p => p.Id).Distinct().Count() != ports.Count(p => p != null)) throw new InvalidDataException("Unsupported motherboard output topology.");
            this.identity = identity; this.ports = ports; this.release = release ?? delegate { };
        }
        static IEnumerable<IHardware> Tree(IHardware root) { yield return root; foreach (var child in root.SubHardware) foreach (var h in Tree(child)) yield return h; }
        public static CoolingUsbInfo[] Describe(IEnumerable<IHardware> roots)
        {
            var found = new List<CoolingUsbInfo>();
            foreach (var root in roots.Where(h => h.HardwareType == HardwareType.Motherboard)) foreach (var h in Tree(root)) {
                var controls = h.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null && s.Index >= 0 && s.Index < 32).ToArray();
                if (controls.Length == 0 || controls.GroupBy(s => s.Index).Any(g => g.Count() != 1)) continue;
                string path = root.Name + ":" + h.Identifier;
                found.Add(new CoolingUsbInfo { Id = CoolingUsbDiscovery.Identity("board", path), Kind = "board", Path = path, Name = root.Name + " · " + h.Name, Outputs = controls.Max(s => s.Index) + 1 });
            }
            return found.OrderBy(d => d.Id).ToArray();
        }
        public static CoolingUsbInfo[] Find(Computer computer)
        {
            if (computer == null) return new CoolingUsbInfo[0];
            // Reuse the host's single Computer. Closing a second Computer would
            // close LHM's process-global Ring0 handle used by its CPU sensors.
            computer.IsMotherboardEnabled = true;
            foreach (var root in computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard)) foreach (var h in Tree(root)) h.Update();
            return Describe(computer.Hardware);
        }
        public static MotherboardCoolingController Open(CoolingUsbInfo saved, IEnumerable<IHardware> roots)
        {
                var hardware = roots.ToArray();
                var info = Describe(hardware).SingleOrDefault(d => d.Id == saved.Id && d.Path == saved.Path);
                if (info == null || (saved.Outputs != 0 && info.Outputs != saved.Outputs)) throw new IOException("The exact saved motherboard controller topology is unavailable.");
                var ports = new IBoardControlPort[info.Outputs];
                foreach (var root in hardware.Where(h => h.HardwareType == HardwareType.Motherboard)) foreach (var h in Tree(root)) if (root.Name + ":" + h.Identifier == saved.Path)
                    foreach (var sensor in h.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null && s.Index >= 0 && s.Index < ports.Length)) ports[sensor.Index] = new LhmBoardPort(sensor, info.Id + "/" + sensor.Identifier);
                return new MotherboardCoolingController(info.Id, ports, null);
        }
        public CoreState Read()
        {
            var result = new CoreState { Firmware = "Library-backed mainboard", Connected = new bool[Count], Rpm = new int?[Count], OutputNames = new string[Count], Modes = new int[Count], Powers = new int[Count] };
            for (int i = 0; i < Count; i++) {
                var port = ports[i]; result.OutputNames[i] = port == null ? "Unavailable output " + (i + 1) : port.Name;
                if (port == null) continue; port.Update(); BoardControlPolicy.Capture(port, identity);
                result.Connected[i] = true; result.Rpm[i] = port.Rpm; result.Modes[i] = port.Mode == "Software" ? 0 : 2; result.Powers[i] = port.Duty.HasValue && BoardControlPolicy.Valid(port.Duty.Value) ? (int)Math.Round(port.Duty.Value) : 0;
            }
            return result;
        }
        void Mask(bool[] selection) { if (selection == null || selection.Length != Count || !selection.Any(v => v) || selection.Where((v, i) => v && ports[i] == null).Any()) throw new InvalidDataException("Choose only available motherboard outputs."); }
        public CoreBaseline Capture(bool[] selected)
        {
            Mask(selected); var baseline = new BoardControlBaseline[Count];
            for (int i = 0; i < Count; i++) if (selected[i]) { ports[i].Update(); baseline[i] = BoardControlPolicy.Capture(ports[i], identity); }
            owned = (bool[])selected.Clone(); return new CoreBaseline { Selected = (bool[])selected.Clone(), Board = baseline };
        }
        public void Apply(int[] desired, int[] previous, Func<bool> allowed)
        {
            if (desired == null || previous == null || desired.Length != Count || previous.Length != Count || desired.Any(v => v < -1 || v > 100) || previous.Any(v => v < -1 || v > 100)) throw new InvalidDataException("Invalid motherboard percentages.");
            bool[] selection = desired.Select(v => v >= 0).ToArray(); Mask(selection);
            if (owned == null || !selection.SequenceEqual(owned)) throw new IOException("Capture the exact motherboard selection before writing.");
            // Validate every output before the first write; a changed sibling cannot be ignored.
            for (int i = 0; i < Count; i++) if (selection[i]) {
                ports[i].Update();
                BoardControlPolicy.Capture(ports[i], identity);
                if (desired[i] < ports[i].Minimum || desired[i] > ports[i].Maximum || (previous[i] >= 0 && (ports[i].Mode != "Software" || !ports[i].Duty.HasValue || !BoardControlPolicy.Valid(ports[i].Duty.Value) || Math.Abs(ports[i].Duty.Value - previous[i]) > 2))) throw new IOException("Motherboard output range or previous PWM changed. Retain recovery.");
            }
            for (int i = 0; i < Count; i++) if (selection[i] && desired[i] != previous[i]) BoardControlPolicy.Set(ports[i], desired[i], allowed);
        }
        public void Restore(CoreBaseline baseline)
        {
            if (baseline == null || baseline.Board == null || baseline.Board.Length != Count) throw new InvalidDataException("Missing motherboard recovery."); Mask(baseline.Selected);
            for (int i = 0; i < Count; i++) if (baseline.Selected[i]) { var saved = baseline.Board[i]; if (saved == null || saved.ControllerId != identity || saved.Id != ports[i].Id || (saved.Mode != "Software" && saved.Mode != "Default" && saved.Mode != "Undefined") || (saved.Mode == "Software" && (!BoardControlPolicy.Valid(saved.Value) || saved.Value < ports[i].Minimum || saved.Value > ports[i].Maximum))) throw new InvalidDataException("Motherboard recovery identity, range or mode changed."); }
            for (int i = 0; i < Count; i++) if (baseline.Selected[i]) BoardControlPolicy.Restore(ports[i], baseline.Board[i]); owned = null;
        }
        public void Dispose() { release(); }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Pcue.Cooling;
using pCUE;

static class CoolingRecoveryTests
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    static object Field(CoolingControllersWindow panel, string name) { return typeof(CoolingControllersWindow).GetField(name, Private).GetValue(panel); }
    static void RunOperation(CoolingControllersWindow panel, string name)
    {
        var task = (Task)typeof(CoolingControllersWindow).GetMethod(name, Private).Invoke(panel, null);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += delegate { if (task.IsCompleted) frame.Continue = false; };
        try { timer.Start(); if (!task.IsCompleted) Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult(); }
        finally { timer.Stop(); }
    }
    public static void Run()
    {
        var root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "pCUE.sln"))) root = root.Parent;
        if (root == null) throw new Exception("Recovery tests require a repository checkout.");
        string scratch = Path.Combine(root.FullName, ".codex-tmp", "recovery-test-" + Guid.NewGuid().ToString("N"));
        var oldContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var state = new DeviceState();
        var identity = CoolingUsbDiscovery.Describe(0x1b1c, 0x0c2a, "simulated-hid&mi_00", 65, 65);
        var connections = new List<Transport>(); bool failOpen = false; int opens = 0;
        var panel = new CoolingControllersWindow(false, scratch, delegate(CoolingUsbInfo requested)
        {
            Check(requested.Id == identity.Id && requested.Path == identity.Path, "Recovery substituted another device.");
            opens++;
            if (failOpen) throw new IOException("Simulated device still unplugged.");
            var transport = new Transport(state); connections.Add(transport); return new CoreController(false, transport);
        });
        try
        {
            var devices = (ComboBox)Field(panel, "devices"); devices.Items.Add(identity); devices.SelectedIndex = 0;
            RunOperation(panel, "Connect");
            var rows = (ObservableCollection<CoolingOutputRow>)Field(panel, "rows"); rows[0].Enabled = true; rows[0].Percent = 75;
            RunOperation(panel, "Apply");
            string recoveryPath = Path.Combine(scratch, "recovery.json");
            Check(panel.PendingRecovery && state.Powers[0] == 75 && state.Modes[0] == 0, "Initial managed session did not capture/apply.");
            string originalRecovery = File.ReadAllText(recoveryPath);
            connections[0].Failed = connections[0].FailDispose = true;
            RunOperation(panel, "Poll");
            Check(connections[0].Disposed && Field(panel, "controller") == null, "Failed polling retained the dead handle.");
            Check(panel.PendingRecovery && rows.All(r => !r.CanControl) && ((Button)Field(panel, "restore")).IsEnabled, "Lost feedback did not expose recovery while blocking writes.");
            Check(File.ReadAllText(recoveryPath) == originalRecovery, "Polling loss modified the saved baseline.");
            failOpen = true; RunOperation(panel, "Restore");
            Check(opens == 2 && Field(panel, "controller") == null && File.ReadAllText(recoveryPath) == originalRecovery, "Unplugged recovery did not preserve the record and retry opening.");
            failOpen = false; state.RejectWrites = true; RunOperation(panel, "Restore");
            Check(opens == 3 && connections[1].Disposed && Field(panel, "controller") == null && panel.PendingRecovery, "Failed restore retained its connection or discarded recovery.");
            Check(File.ReadAllText(recoveryPath) == originalRecovery, "Rejected restore overwrote the original baseline.");
            // An unrelated output changed while disconnected; recovery must preserve it.
            state.RejectWrites = false; state.Powers[1] = 67; RunOperation(panel, "Restore");
            Check(opens == 4 && connections[2].Disposed && !panel.PendingRecovery && !File.Exists(recoveryPath), "Reconnected restore did not finish/release recovery.");
            Check(state.Powers[0] == 40 && state.Modes[0] == 2 && state.Powers[1] == 67, "Reconnected restore lost selected baseline or changed an unselected output.");
            Check(!panel.IsVisible, "Recovery tests displayed a window.");
            Console.WriteLine("PASS: cooling recovery retries after USB loss, failed open/restore, disposal error, exact identity and unselected preservation (simulated; no USB).");
            RunIoRecovery(scratch + "-io");
        }
        finally
        {
            typeof(CoolingControllersWindow).GetMethod("ReleaseController", Private).Invoke(panel, null);
            typeof(CoolingControllersWindow).GetField("recovery", Private).SetValue(panel, null);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            panel.Close(); SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
    static void RunIoRecovery(string scratch)
    {
        var fake = new IoClient();
        var identity = CoolingUsbDiscovery.Describe(0x373f, 0x0010, "simulated-hid&mi_00", 65, 65); identity.BridgeSerial = "FAKEBRIDGE";
        int opens = 0;
        var panel = new CoolingControllersWindow(false, scratch, delegate(CoolingUsbInfo requested) { Check(requested.Id == identity.Id, "IO UI selected a replacement bridge."); opens++; return new BeQuietIoController(fake, false); });
        try
        {
            var devices = (ComboBox)Field(panel, "devices"); devices.Items.Add(identity); devices.SelectedIndex = 0; RunOperation(panel, "Connect");
            var rows = (ObservableCollection<CoolingOutputRow>)Field(panel, "rows"); rows[0].Role = "Pump"; rows[0].Enabled = rows[1].Enabled = true; rows[1].Percent = 67;
            RunOperation(panel, "Apply"); string record = Path.Combine(scratch, "recovery.json"); string original = File.ReadAllText(record);
            Check(panel.PendingRecovery && fake.Fan == 67 && fake.Mode == 1 && original.Contains("ABCDEF123456"), "IO did not save exact child baseline before control.");
            fake.Reject = true; rows[1].Percent = 75; RunOperation(panel, "Apply");
            Check(Field(panel, "controller") != null && !fake.Disposed && opens == 1 && File.ReadAllText(record) == original, "Failed IO command released the live owner or overwrote baseline.");
            RunOperation(panel, "Restore"); Check(panel.PendingRecovery && Field(panel, "controller") != null && !fake.Disposed && File.ReadAllText(record) == original, "Failed IO restore lost ownership/recovery.");
            fake.Reject = false; RunOperation(panel, "Restore");
            Check(!panel.PendingRecovery && fake.Disposed && fake.Pump == 100 && fake.Fan == 48 && fake.Mode == 0 && !File.Exists(record), "IO restore failed to return bank/mode/full pump and release owner.");
            Check(!panel.IsVisible, "IO recovery test displayed a window.");
            Console.WriteLine("PASS: actual IO panel saves child recovery, retains owner after failed Apply/Restore, and clears it only after verified restoration (simulated; no USB).");
        }
        finally { typeof(CoolingControllersWindow).GetMethod("ReleaseController", Private).Invoke(panel, null); typeof(CoolingControllersWindow).GetField("recovery", Private).SetValue(panel, null); if (Directory.Exists(scratch)) Directory.Delete(scratch, true); panel.Close(); }
    }
    sealed class IoClient : IBeQuietControlClient
    {
        public int Pump = 100, Fan = 48, Mode; public bool Reject, Disposed;
        public string Error { get { return null; } } public string CoolerSerial { get { return "ABCDEF123456"; } } public string FirmwareVersion { get { return "0.0.37"; } }
        public BeQuietIoReading[] Fresh { get { return new[] { new BeQuietIoReading { Channel = 1, RawValue = 2900 }, new BeQuietIoReading { Channel = 2, RawValue = 1000 }, new BeQuietIoReading { Channel = 3, RawValue = 0 } }; } }
        public int ReadDuty(int channel) { return channel == 1 ? Pump : Fan; } public int ReadCoolingMode() { return Mode; }
        public void SetDuty(int channel, int value, Func<bool> allowed = null) { if (Reject || (allowed != null && !allowed())) throw new IOException("Simulated IO write refused."); if (channel == 1) Pump = value; else Fan = value; }
        public void SetSoftwareControl(bool value, Func<bool> allowed = null) { if (Reject || (allowed != null && !allowed())) throw new IOException("Simulated mode refused."); Mode = value ? 1 : 0; }
        public void Heartbeat() { } public void Status() { } public void Dispose() { Disposed = true; }
    }
    sealed class DeviceState
    {
        public int[] Powers = Enumerable.Repeat(40, 6).ToArray(), Modes = Enumerable.Repeat(2, 6).ToArray();
        public bool RejectWrites;
    }
    sealed class Transport : ICoreTransport
    {
        readonly DeviceState state; byte endpoint;
        public bool Failed, FailDispose, Disposed;
        public Transport(DeviceState state) { this.state = state; }
        public byte[] Exchange(byte[] command, byte[] data)
        {
            if (Failed || Disposed) throw new IOException("Simulated dead HID handle.");
            var reply = new List<byte>(new byte[] { 0, command[0], 0 });
            if (command[0] == 2) reply.AddRange(new byte[] { 1, 6, 201 });
            else if (command[0] == 13) endpoint = data[0];
            else if (command[0] == 8 && command[2] == 1)
            {
                byte type = endpoint == 0x1a ? (byte)9 : endpoint == 0x17 ? (byte)6 : endpoint == 0x21 ? (byte)0x10 : endpoint == 0x60 ? (byte)3 : endpoint == 0x61 ? (byte)4 : (byte)0xff;
                reply.Add(type); reply.Add(0);
                if (type == 9) reply.AddRange(CoreProtocol.Encode(Enumerable.Repeat(7, 6).ToArray(), false));
                if (type == 6) { reply.Add(6); for (int i = 0; i < 6; i++) reply.AddRange(new byte[] { 176, 4 }); }
                if (type == 0x10) reply.Add(0);
                if (type == 3) reply.AddRange(CoreProtocol.Encode(state.Modes, false));
                if (type == 4) reply.AddRange(CoreProtocol.Encode(state.Powers, true));
            }
            else if (command[0] == 6)
            {
                if (state.RejectWrites) reply[2] = 1;
                else if (data[4] == 3) state.Modes = CoreProtocol.Values(data.Skip(6).ToArray(), 6, false);
                else if (data[4] == 4) state.Powers = CoreProtocol.Values(data.Skip(6).ToArray(), 6, true);
                else throw new Exception("Unexpected simulated write.");
            }
            return reply.ToArray();
        }
        public void Dispose() { Disposed = true; if (FailDispose) throw new IOException("Simulated close error."); }
    }
}

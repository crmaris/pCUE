using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LibreHardwareMonitor.Hardware;
using System.Diagnostics;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;

public enum FanMask : byte
{
    /** No fan connected */
    Auto_Disconnected = 0x00,
    /** A three pin fan is connected */
    ThreePin = 0x01,
    /** A four pin fan is connected */
    FourPin = 0x02
}

public enum FanDetectionType : byte
{
    /** Auto detect the type of fan which is connected */
    Auto = 0x00,
    /** A three pin fan is connected */
    ThreePin = 0x01,
    /** A four pin fan is connected */
    FourPin = 0x02,
    /** No fan connected */
    Disconnected = 0x03
}

namespace pCUE
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, IRemoteControlTarget
    {
        //The Commander PRO HID session. All protocol knowledge lives in the device class; this
        //window only orchestrates UI, polling and the hold loop on top of it.
        readonly CommanderProDevice commander = new CommanderProDevice();
        bool Corsair_Commander_Connected = false;

        //Background fan-RPM polling (replaces the old UI-thread WinForms timer).
        CancellationTokenSource fanPollCts;
        Task fanPollTask;
        volatile bool fanPollErrorLogged = false;
        //Auto-disconnect once this many poll passes fail back-to-back (a few seconds of dead I/O).
        const int MaxConsecutivePollFailures = 3;

        //fan constants
        public const int FAN_FORCE_THREE_PIN_MODE_ON = 0x01;
        public const int FAN_FORCE_THREE_PIN_MODE_OFF = 0x00;
        public const int FAN_CURVE_POINTS_NUM = 6;
        public const int FAN_CURVE_TEMP_GROUP_EXTERNAL = 255;

        //CPU monitoring via LibreHardwareMonitor (replaces Core Temp). Opened once at startup;
        //the CPU-data timer polls its sensors for temperature, clock and load.
        Computer thisComputer;
        readonly UpdateVisitor lhmUpdateVisitor = new UpdateVisitor();

        //Timer for CPU Data
        static System.Windows.Forms.Timer CpuDataTimer = new System.Windows.Forms.Timer();

        //Periodic UI refresh: keeps the tachometer panel honest about stale/lost signal. The
        //Min/Max/Avg statistics no longer need a timer - they are computed where each value is
        //produced (the poll loop and the CPU timer) instead of being parsed back out of TextBoxes.
        static System.Windows.Forms.Timer Set_Min_Max_AVG_timer = new System.Windows.Forms.Timer();

        //Min/Max/Avg statistics, computed from real values rather than read back out of the UI.
        //9 series: CPU temp/clock/load + six fan channels.
        readonly RunStatSet stats = new RunStatSet(9);
        //Series indices inside the stats bank.
        const int StatCpuTemp = 0, StatCpuClock = 1, StatCpuLoad = 2;
        const int StatFanBase = 3;   // fans 0..5 -> StatFanBase+0..+5

        //Control arrays. Built explicitly from the named fields in the constructor - the old
        //FindLogicalChildren walk depended on XAML declaration order, so reordering the XAML
        //silently scrambled which box was Current and which was Min.
        TextBox[] CPU_array;
        TextBox[] Fan_array;
        NumericUpDownLib.UIntegerUpDown[] Fan_Numeric_Boxes;
        Slider[] Fan_Slider;
        ComboBox[] Fan_Mode_Controls;

        //External bench tachometer (USB-HID, VID 0x1A86 / PID 0xE008). Connected on demand from the
        //Tachometer panel. When assigned to a fan channel it overrides that fan's displayed RPM with
        //the tach reading; when the tach signal is stale/lost it falls back to the Commander PRO value.
        HidTachometer bench_tach;
        volatile int tachAssignedChannel = -1;   // -1 = None; 0..5 = Fan #1..#6
        //So the low-battery dialog appears once per connection instead of every 500 ms tick.
        bool tachBatteryWarned = false;
        //Latched copy of the meter's low-battery flag. The flag itself flickers - on the bench it
        //reported LOW at 14:18 and clear again minutes later on the same cell - so following it
        //directly makes the label blink on and off, which is how the first real low battery went
        //unnoticed. Cleared when the meter disconnects, which is what changing the cell does.
        bool tachBatteryLowSeen = false;

        //Latest RPM per channel as shown in the Current column, i.e. AFTER the bench-tachometer
        //override. The closed-loop RPM hold feeds on this, so it automatically uses the external
        //tachometer when one is assigned and the Commander's own reading otherwise.
        readonly int[] latestFanRpm = new int[6];
        DateTime latestFanRpmUtc = DateTime.MinValue;
        readonly object fanRpmLock = new object();

        //Closed-loop RPM hold. The Commander PRO only regulates by RPM on 4-pin/PWM channels;
        //on a 3-pin (DC) channel it offers fixed percent only, so pCUE closes that loop itself.
        FanRpmHoldController rpmHold;
        volatile int holdChannel = -1;   // -1 = None; 0..5 = Fan #1..#6

        //What to open at when the fan is stopped: there is nothing to measure, and nothing to
        //step away from, until it breaks away.
        const int HoldKickStartDuty = 40;
        //Live tunables for the hold loop, editable over the remote API so the controller can be
        //tuned against real hardware without rebuilding and redeploying the app.
        readonly FanHoldConfig holdConfig = new FanHoldConfig();

        //Optional HTTP remote-control server. Off unless enabled on the command line.
        RemoteControlServer remoteServer;
        DiscoveryBeacon discoveryBeacon;

        //In-app updater (checks a signed-manifest URL; never installs on its own).
        AppUpdateService updateService;
        //Set while an update installer is being launched, so Window_Closing skips its
        //"Really close?" prompt - the user has already confirmed the update.
        bool suppressCloseConfirm = false;

        public MainWindow()
        {
            InitializeComponent();

            //Control arrays, explicitly ordered. Index maps (see the XAML):
            //  CPU_array  ed1..ed9   - rows Temp/MHz/Load, columns Current/Min/Max -> [0],[3],[6] are Current
            //  Fan_array  ed10..ed27 - six fan rows x Current/Min/Max             -> [ch*3] is Current
            CPU_array = new[] { ed1, ed2, ed3, ed4, ed5, ed6, ed7, ed8, ed9 };
            Fan_array = new[]
            {
                ed10, ed11, ed12, ed13, ed14, ed15,
                ed16, ed17, ed18, ed19, ed20, ed21,
                ed22, ed23, ed24, ed25, ed26, ed27,
            };
            Fan_Numeric_Boxes = new[] { Fan1_Numeric, Fan2_Numeric, Fan3_Numeric, Fan4_Numeric, Fan5_Numeric, Fan6_Numeric };
            Fan_Slider = new[] { Fan1_Slider, Fan2_Slider, Fan3_Slider, Fan4_Slider, Fan5_Slider, Fan6_Slider };
            Fan_Mode_Controls = new[] { Combo1, Combo2, Combo3, Combo4, Combo5, Combo6 };

            //Read CPU Data
            CpuDataTimer.Tick += new EventHandler(CpuDataTimer_Tick);
            CpuDataTimer.Interval = 500; // specify interval time

            //Periodic tachometer-panel refresh
            Set_Min_Max_AVG_timer.Tick += new EventHandler(Set_Min_Max_AVG_timer_Tick);
            Set_Min_Max_AVG_timer.Interval = 500; // specify interval time as you want
            Set_Min_Max_AVG_timer.Start();

            // CPU sensors via LibreHardwareMonitor (CPU only - that is all pCUE displays).
            // Requires admin rights, which the app manifest already requests.
            thisComputer = new Computer() { IsCpuEnabled = true };
            try { thisComputer.Open(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: LibreHardwareMonitor open failed: " + ex.Message); }

            //External bench tachometer - created here, opened only when the user clicks Connect.
            bench_tach = new HidTachometer();
            bench_tach.ConnectionChanged += Bench_Tach_ConnectionChanged;
            //The live RPM readout is refreshed by Update_Tach_Panel() on the 500 ms UI timer, so
            //the panel and the fan column agree on what "fresh" means.

            updateService = new AppUpdateService();
        }

        #region Main Window Functions
        //Window Functions
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //Show the build-stamped file version (its revision bumps on every build) in the title
            try
            {
                string fileVersion = System.Diagnostics.FileVersionInfo
                    .GetVersionInfo(System.Reflection.Assembly.GetExecutingAssembly().Location)
                    .FileVersion;
                this.Title = "pCUE - Cybenetics LTD - v." + fileVersion;
            }
            catch (Exception ex) { Debug.WriteLine("pCUE: could not read file version: " + ex.Message); }

            if (Properties.Settings.Default.AutoStart1)
            { autostartCheckBox.IsChecked = true; }

            if (Properties.Settings.Default.AVG_Values)
            { AVG_values.IsChecked = true; }

            StartRemoteControlIfRequested();

            //Opt-in auto-connect. Deliberately NOT the default: opening the Commander also kills
            //the iCUE services, which would be a rude thing to do unasked on every launch. It earns
            //its place on a test bench, where an auto-update restart otherwise leaves the hardware
            //disconnected until somebody walks over and clicks two buttons.
            Tacho_Adjust_CheckBox.IsChecked = Properties.Settings.Default.Tacho_Adjust;
            Auto_Connect_CheckBox.IsChecked = Properties.Settings.Default.Auto_Connect;
            if (Properties.Settings.Default.Auto_Connect)
            {
                //After the window is up, so a failure dialog cannot block loading.
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        AppLog.Info("Auto-connect: opening Commander PRO and tachometer.");
                        if (!Corsair_Commander_Connected) Open_Corsair_Commander_Click(this, null);
                        if (bench_tach != null && !bench_tach.IsConnected) bench_tach.Connect();
                    }
                    catch (Exception ex)
                    {
                        //A missing tachometer is normal, so log it rather than nagging on start-up.
                        AppLog.Warn("Auto-connect: " + ex.Message);
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            //Optional start-up update check. It only reports into the status line - it never
            //pops a dialog and never installs anything by itself.
            Update_On_Start_CheckBox.IsChecked = Properties.Settings.Default.Update_Check_On_Start;
            if (Properties.Settings.Default.Update_Check_On_Start)
            {
                _ = RunUpdateCheck(false);
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            CpuDataTimer.Stop();

            //stop the closed-loop hold before the HID stream goes away
            StopRpmHold("application closing");

            //stop background fan polling and release the HID stream
            Corsair_Commander_Connected = false;
            StopFanPolling();
            commander.Disconnect();

            //release the LibreHardwareMonitor session (unloads its kernel driver)
            try { thisComputer?.Close(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: LibreHardwareMonitor close failed: " + ex.Message); }

            //release the external bench tachometer (stops its read thread, closes the HID stream)
            try { bench_tach?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: tach dispose failed: " + ex.Message); }

            try { updateService?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: update service dispose failed: " + ex.Message); }

            try { remoteServer?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: remote server dispose failed: " + ex.Message); }

            try { discoveryBeacon?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: discovery beacon dispose failed: " + ex.Message); }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            //An update install already asked for confirmation - do not ask a second time.
            if (suppressCloseConfirm)
            {
                CpuDataTimer.Stop();
                return;
            }

            MessageBoxResult result = MessageBox.Show("Really close?", "Warning", MessageBoxButton.YesNo);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }

            else
            {
                CpuDataTimer.Stop();
            }
        }
        #endregion

        private void Set_Min_Max_AVG_timer_Tick(object sender, EventArgs e)
        {
            //The Min/Max/Avg figures are now maintained where each value is produced; this timer
            //only keeps the bench-tachometer readout honest about stale/lost signal.
            Update_Tach_Panel();
        }

        //Shows the running average of each fan in its own column (ed28..ed33)
        private void Set_Fan_Average_Column()
        {
            TextBox[] avg = { ed28, ed29, ed30, ed31, ed32, ed33 };
            for (int ch = 0; ch < 6; ch++)
                avg[ch].Text = Math.Round(stats.Average(StatFanBase + ch)).ToString();
        }

        //Generic Functions

        #region Commander Pro Functions
        // ---- Background fan-RPM polling --------------------------------------------------
        // Fan speeds used to be read on a WinForms (UI-thread) timer, so a slow or stalled HID
        // transfer froze the whole window. We now poll on a background task; every HID access is
        // serialized inside CommanderProDevice, and only the final RPM values are marshalled
        // onto the UI thread.

        //Start the background poll loop. Safe to call repeatedly (it stops any previous loop).
        private void StartFanPolling()
        {
            StopFanPolling();
            fanPollErrorLogged = false;
            fanPollCts = new CancellationTokenSource();
            CancellationToken token = fanPollCts.Token;
            fanPollTask = Task.Run(() => FanPollLoop(token));
        }

        //Stop the background poll loop cleanly. Cancellation only - never blocks the UI thread.
        private void StopFanPolling()
        {
            CancellationTokenSource cts = fanPollCts;
            Task task = fanPollTask;
            fanPollCts = null;
            fanPollTask = null;

            if (cts == null) return;

            try { cts.Cancel(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: StopFanPolling cancel failed: " + ex.Message); }

            //Dispose the token source only once the loop has actually finished using it.
            if (task != null) { task.ContinueWith(_ => { try { cts.Dispose(); } catch { } }); }
            else { try { cts.Dispose(); } catch { } }
        }

        //The background loop. One pass runs at a time, so overlapping poll cycles are impossible.
        private async Task FanPollLoop(CancellationToken token)
        {
            int consecutivePollFailures = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (Corsair_Commander_Connected)
                    {
                        int[] rpms = new int[6];

                        try
                        {
                            string fan_mask = commander.ReadFanMask();   //e.g. "011000"

                            for (int ch = 0; (ch < 6) && (ch < fan_mask.Length); ch++)
                            {
                                token.ThrowIfCancellationRequested();

                                char y = fan_mask[ch];
                                //'1' = 3-pin, '2' = 4-pin => active; anything else => inactive
                                rpms[ch] = ((y == '1') || (y == '2')) ? commander.ReadFanRpm(ch) : 0;
                            }

                            fanPollErrorLogged = false;
                            consecutivePollFailures = 0;

                            //External bench tachometer override: for the assigned fan, replace the
                            //Commander PRO reading with the tach's RPM when it is fresh; if the tach
                            //signal is stale/lost (ReadRpm() == null) keep the Commander value.
                            int tachCh = tachAssignedChannel;
                            if (tachCh >= 0 && tachCh < 6 && bench_tach != null && bench_tach.IsConnected)
                            {
                                double? tachRpm = bench_tach.ReadRpm();
                                if (tachRpm.HasValue) rpms[tachCh] = (int)Math.Round(tachRpm.Value);
                            }

                            //Publish for the closed-loop RPM hold (post-tach-override values).
                            lock (fanRpmLock)
                            {
                                Array.Copy(rpms, latestFanRpm, 6);
                                latestFanRpmUtc = DateTime.UtcNow;
                            }

                            if (!token.IsCancellationRequested)
                            {
                                int[] snapshot = rpms;
                                //fire-and-forget UI marshal; the discard documents we don't await it
                                try { _ = Dispatcher.BeginInvoke(new Action(() => UpdateFanRpmUi(snapshot))); }
                                catch (Exception ex) { Debug.WriteLine("pCUE: fan UI dispatch failed: " + ex.Message); }
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            //Log once per failure streak so a disconnected/stalled device does
                            //not spam the debug output every 500 ms.
                            if (!fanPollErrorLogged)
                            {
                                Debug.WriteLine("pCUE: HID fan poll failed: " + ex.Message);
                                fanPollErrorLogged = true;
                            }

                            consecutivePollFailures++;
                            if (consecutivePollFailures >= MaxConsecutivePollFailures)
                            {
                                Debug.WriteLine("pCUE: " + consecutivePollFailures +
                                    " consecutive HID poll failures - auto-disconnecting Commander Pro.");

                                //Hand the teardown to the UI thread; never wait on this task from here.
                                if (Corsair_Commander_Connected)
                                {
                                    try
                                    {
                                        _ = Dispatcher.BeginInvoke(new Action(() =>
                                            DisconnectCommanderPro("● Connection lost (HID errors)", System.Windows.Media.Brushes.OrangeRed)));
                                    }
                                    catch (Exception ex2) { Debug.WriteLine("pCUE: auto-disconnect dispatch failed: " + ex2.Message); }
                                }
                                break;   //stop polling now; cleanup finishes on the UI thread
                            }
                        }
                    }

                    try { await Task.Delay(500, token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            catch (Exception ex) { Debug.WriteLine("pCUE: fan poll loop crashed: " + ex.Message); }
        }

        //Push the freshly polled RPMs onto the read-out text boxes. Runs on the UI thread.
        //Inactive/disconnected channels are cleared to "0" so stale RPMs never linger, and each
        //non-zero reading feeds that fan's Min/Max/Avg statistics at the moment it is produced.
        private void UpdateFanRpmUi(int[] rpms)
        {
            if (rpms == null) return;

            for (int ch = 0; ch < 6; ch++)
            {
                int idx = ch * 3;                     // channel -> "Current" index in Fan_array (0,3,6,9,12,15)
                if (idx >= Fan_array.Length) return;
                Fan_array[idx].Text = rpms[ch].ToString();
                stats.Add(StatFanBase + ch, rpms[ch]);
            }

            Set_Fan_Average_Column();
            RenderFanMinMaxColumns();
        }

        //Single safe teardown for the Commander Pro connection + UI reset. MUST run on the UI
        //thread. Shared by manual disconnect, connect-failure cleanup and the automatic
        //disconnect that fires after repeated poll failures. Idempotent and null-safe.
        private void DisconnectCommanderPro(string statusText, System.Windows.Media.Brush statusBrush)
        {
            StopRpmHold("Commander disconnected");   //the loop has no actuator without the device
            Corsair_Commander_Connected = false;
            StopFanPolling();   //cancellation only - never waits on the poll task
            commander.Disconnect();   //nulls + closes the stream, interrupting any blocked read

            //reset the UI to the disconnected state
            Open_Corsair_Commander.Content = "Open";
            foreach (TextBox box in Fan_array) { box.Text = "0000"; }
            SetStatus(statusText, statusBrush);
        }

        //Updates the connection status label. Must be called on the UI thread.
        private void SetStatus(string text, System.Windows.Media.Brush brush)
        {
            Status_Label.Text = text;
            Status_Label.Foreground = brush;
        }

        // ---- Commander PRO wrappers -------------------------------------------------------
        // The protocol itself lives in CommanderProDevice. What remains here is orchestration:
        // keeping the UI in step and surfacing what the device says - including a REJECTION,
        // which the status byte reports and which used to be silently dropped.

        private void Commander_Pro_READ_FAN_MODEs()
        {
            string fan_mask = commander.ReadFanMask(); //px. 011000

            for (int j = 0; j < fan_mask.Length; j++)
            {

                char y = fan_mask[j];

                switch (y)
                {
                    case '0':
                        Fan_Mode_Controls[j].SelectedIndex = (int)FanMask.Auto_Disconnected;
                        break;
                    case '1':
                        Fan_Mode_Controls[j].SelectedIndex = (int)FanMask.ThreePin;
                        break;
                    case '2':
                        Fan_Mode_Controls[j].SelectedIndex = (int)FanMask.FourPin;
                        break;
                }
            }
        }

        //Set the fan mode (the drop-down's SelectionChanged handler).
        //Records each channel's last mode-write outcome so the remote API can report a rejection
        //to ITS caller too (the write itself happens inside the SelectionChanged event).
        readonly bool[] fanModeWriteOk = new bool[6];

        private void Commander_Pro_Set_Fan_Connection_Mode(object sender, SelectionChangedEventArgs e)
        {
            if (Corsair_Commander_Connected != true) return;

            String nam = ((ComboBox)sender).Name;
            int selected_fan = 0;

            for (int i = 0; i < 6; ++i)
            {
                if (Fan_Mode_Controls[i].Name == nam)
                {
                    selected_fan = i;
                    break;
                }
            }

            FanDetectionType type;
            switch (Fan_Mode_Controls[selected_fan].SelectedIndex)
            {
                case 1: type = FanDetectionType.ThreePin; break;
                case 2: type = FanDetectionType.FourPin; break;
                case 3: type = FanDetectionType.Disconnected; break;
                default: type = FanDetectionType.Auto; break;
            }

            fanModeWriteOk[selected_fan] = commander.WriteFanDetectionType(selected_fan, type);
            if (!fanModeWriteOk[selected_fan])
            {
                SetStatus("● Fan " + (selected_fan + 1) + ": mode change rejected by device",
                          System.Windows.Media.Brushes.Orange);
            }
        }

        //Set The Fan Speed (fixed RPM target). Returns false when the DEVICE rejected it -
        //which is what happens when an RPM target is sent to a 3-pin/DC channel.
        private bool Commander_Pro_Set_Fan_Speed(int fan_channel, int fan_speed)
        {
            if (Corsair_Commander_Connected != true) return false;
            return commander.WriteFanSpeed(fan_channel, fan_speed);
        }

        //Set The Fan Power (duty %). Returns false when the DEVICE rejected it.
        private bool Commander_Pro_Set_Fan_Power(int fan_channel, int fan_power)
        {
            if (Corsair_Commander_Connected != true) return false;
            return commander.WriteFanPower(fan_channel, fan_power);
        }

        private void Open_Corsair_Commander_Click(object sender, RoutedEventArgs e)
        {
             if (Open_Corsair_Commander.Content.ToString() == "Open")
            {
                try
                {
                    //kill iCUE services because it messes with the readings
                    Kill_iCUE_Function();

                    //open the Commander PRO session (throws with an operator-readable reason)
                    commander.Connect();

                    Open_Corsair_Commander.Content = "Close";
                    Corsair_Commander_Connected = true;

                    Commander_SN.Text = commander.FirmwareVersion;

                    Commander_Pro_READ_FAN_MODEs();

                    //show speed at first
                    for (int i = 0; i < 6; ++i)
                    {
                        uint rpm = (uint)commander.ReadFanRpm(i);
                        Fan_Numeric_Boxes[i].Value = rpm;
                        Fan_Slider[i].Value = rpm;
                    }

                    //start polling the fans on a background task
                    StartFanPolling();
                    SetStatus("● Connected", System.Windows.Media.Brushes.Lime);
                }
                 catch (CommanderProOpenException ex)
                {
                    //Same two outcomes the Open button has always reported.
                    MessageBox.Show(ex.DeviceFound
                        ? "Cannot open Commander Pro!"
                        : "Cannot open Commander Pro! Is it connected?");
                    DisconnectCommanderPro(ex.DeviceFound ? "● Wrong device" : "● Device not found",
                                           System.Windows.Media.Brushes.Orange);
                }
                catch
                {
                    MessageBox.Show("Cannot open Commander Pro! Is it connected?");
                    DisconnectCommanderPro("● Device not found", System.Windows.Media.Brushes.Orange);   //shared teardown + UI reset
                }
            }
             else if (Open_Corsair_Commander.Content.ToString() == "Close")
                    {
                        DisconnectCommanderPro("● Disconnected", System.Windows.Media.Brushes.Gainsboro);   //shared teardown + UI reset
                    }
                }

        #endregion

        #region CPU data (LibreHardwareMonitor)
        //Walks the LibreHardwareMonitor tree and refreshes every hardware/sensor reading in one pass.
        private class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) { computer.Traverse(this); }
            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (IHardware sub in hardware.SubHardware) sub.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }

        private void CpuDataTimer_Tick(object sender, EventArgs e)
        {
            if (thisComputer == null) return;

            try
            {
                //Refresh all CPU sensor values for this pass.
                thisComputer.Accept(lhmUpdateVisitor);

                double tempSum = 0; int tempCount = 0;   // per-core temps, used only if no package/average sensor exists
                double clockSum = 0; int clockCount = 0; // per-core clocks, averaged into a single figure
                double? coreAvgClk = null;               // "Cores (Average)" package-level clock, if exposed (preferred)
                double? coreAvgTemp = null;              // "Core Average", if the CPU exposes it
                double? packageTemp = null;              // "CPU Package" / "Core (Tctl/Tdie)"
                double? totalLoad = null;                // "CPU Total"

                foreach (IHardware hw in thisComputer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.Cpu) continue;

                    foreach (ISensor s in hw.Sensors)
                    {
                        if (!s.Value.HasValue) continue;
                        double v = s.Value.Value;

                        switch (s.SensorType)
                        {
                            case SensorType.Temperature:
                                if (s.Name == "Core Average") coreAvgTemp = v;
                                else if (s.Name == "CPU Package" || s.Name == "Core (Tctl/Tdie)") packageTemp = v;
                                else if (s.Name.StartsWith("CPU Core #") || s.Name.StartsWith("Core #"))
                                { tempSum += v; tempCount++; }
                                break;

                            case SensorType.Clock:
                                // Prefer the chip's package-level "Cores (Average)"; otherwise average
                                // the real per-core clocks. "Core #N (Effective)" also starts with
                                // "Core #"; excluding it keeps the average to the nominal per-core
                                // clocks (effective variants read low at partial load and would drag
                                // the reported speed down).
                                if (s.Name == "Cores (Average)")
                                    coreAvgClk = v;
                                else if ((s.Name.StartsWith("CPU Core #") || s.Name.StartsWith("Core #"))
                                    && !s.Name.Contains("Effective"))
                                { clockSum += v; clockCount++; }
                                break;

                            case SensorType.Load:
                                if (s.Name == "CPU Total") totalLoad = v;
                                break;
                        }
                    }
                }

                //Prefer the chip's own average/package temperature; otherwise average the per-core readings.
                double temperature = coreAvgTemp ?? packageTemp ?? (tempCount > 0 ? tempSum / tempCount : 0.0);
                double clock = coreAvgClk ?? (clockCount > 0 ? clockSum / clockCount : 0.0);
                double load = totalLoad ?? 0.0;

                CPU_array[0].Text = temperature.ToString("0.0");
                CPU_array[3].Text = clock.ToString("N1");
                CPU_array[6].Text = load.ToString("N1");

                //Feed the statistics where the values are produced, then render Min/Max (or AVG).
                stats.Add(StatCpuTemp, temperature);
                stats.Add(StatCpuClock, clock);
                stats.Add(StatCpuLoad, load);
                RenderCpuMinMaxColumns();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: CPU sensor read failed: " + ex.Message);
            }
        }
        #endregion

        #region App Kill functions
        private void Kill_iCUE_services_Click(object sender, RoutedEventArgs e)
        {
            Kill_iCUE_Function();
        }

        private void Kill_iCUE_Function()
        {
            try
            {
                foreach (System.Diagnostics.Process pr in System.Diagnostics.Process.GetProcesses()) //GETS PROCESSES
                {
                    if ((pr.ProcessName == "CueLLAccessService") || (pr.ProcessName == "Corsair.Service.CpuIdRemote64") || (pr.ProcessName == "Corsair.Service.CpuIdRemote")
                        || (pr.ProcessName == "Corsair.Service.DisplayAdapter") || (pr.ProcessName == "Corsair.Service"))
                    {
                        pr.Kill(); //KILLS THE PROCESSES
                    }
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString());
            }
        }

        #endregion

        #region Min/Max/Avg statistics
        //Statistics are computed where each value is produced - the fan poll loop and the CPU
        //timer call stats.Add(...) and then render - instead of the old scheme of parsing the
        //Current column's TextBox text back into numbers every 500 ms. Semantics preserved from
        //the original Set_min_max: only readings > 0 count; Min never regresses to 0; the shared
        //rollover counter resets everything after ~27.8 hours at 500 ms sampling.

        /// <summary>One series' running min/max/average over its non-zero samples.</summary>
        private sealed class RunStat
        {
            private double _sum;
            private long _count;
            private double? _min;
            private double? _max;

            public double? Min { get { return _min; } }
            public double? Max { get { return _max; } }
            public double Average { get { return _count > 0 ? _sum / _count : 0.0; } }

            public void Add(double value)
            {
                if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return;
                _sum += value;
                _count++;
                if (_min == null || value < _min.Value) _min = value;
                if (_max == null || value > _max.Value) _max = value;
            }

            public void Reset()
            {
                _sum = 0; _count = 0; _min = null; _max = null;
            }
        }

        /// <summary>
        /// All series plus the shared rollover counter of the original implementation: after
        /// 100,000 counted samples (about 27.8 h at the 500 ms cadence) every figure resets.
        /// </summary>
        private sealed class RunStatSet
        {
            private const int RolloverSamples = 100000;
            private readonly RunStat[] _items;
            private int _counted;

            public RunStatSet(int seriesCount)
            {
                _items = new RunStat[seriesCount];
                for (int i = 0; i < seriesCount; i++) _items[i] = new RunStat();
            }

            public void Add(int series, double value)
            {
                if (series < 0 || series >= _items.Length) return;
                if (_counted >= RolloverSamples) Reset();
                if (value > 0) _counted++;
                _items[series].Add(value);
            }

            public double Min(int series)
            {
                return InRange(series) && _items[series].Min.HasValue ? _items[series].Min.Value : 0.0;
            }

            public double Max(int series)
            {
                return InRange(series) && _items[series].Max.HasValue ? _items[series].Max.Value : 0.0;
            }

            public double Average(int series)
            {
                return InRange(series) ? _items[series].Average : 0.0;
            }

            private bool InRange(int series) { return series >= 0 && series < _items.Length; }

            public void Reset()
            {
                _counted = 0;
                foreach (RunStat s in _items) s.Reset();
            }
        }

        /// <summary>Formats a stat the way the Current column formats it, so Min/Max match.</summary>
        private string FormatStat(int series, double value)
        {
            switch (series)
            {
                case StatCpuTemp: return value.ToString("0.0");
                case StatCpuClock: return value.ToString("N1");
                case StatCpuLoad: return value.ToString("N1");
                default: return Math.Round(value).ToString();   // fan RPMs are integers
            }
        }

        //CPU row: middle box shows the real Min, or the running average when "Average Values" is
        //ticked (that checkbox only ever affected the CPU row - fans have their own Avg column).
        private void RenderCpuMinMaxColumns()
        {
            bool showAvg = AVG_values.IsChecked == true;
            CPU_array[1].Text = showAvg
                ? stats.Average(StatCpuTemp).ToString("0.#")
                : FormatStat(StatCpuTemp, stats.Min(StatCpuTemp));
            CPU_array[2].Text = FormatStat(StatCpuTemp, stats.Max(StatCpuTemp));

            CPU_array[4].Text = showAvg
                ? stats.Average(StatCpuClock).ToString("0.#")
                : FormatStat(StatCpuClock, stats.Min(StatCpuClock));
            CPU_array[5].Text = FormatStat(StatCpuClock, stats.Max(StatCpuClock));

            CPU_array[7].Text = showAvg
                ? stats.Average(StatCpuLoad).ToString("0.#")
                : FormatStat(StatCpuLoad, stats.Min(StatCpuLoad));
            CPU_array[8].Text = FormatStat(StatCpuLoad, stats.Max(StatCpuLoad));
        }

        //Fan rows: Min/Max always hold the real extremes (a 0 means "no sample" and never moves
        //them - RunStat.Add enforces that), and the dedicated Avg column is refreshed alongside.
        private void RenderFanMinMaxColumns()
        {
            for (int ch = 0; ch < 6; ch++)
            {
                Fan_array[ch * 3 + 1].Text = FormatStat(StatFanBase + ch, stats.Min(StatFanBase + ch));
                Fan_array[ch * 3 + 2].Text = FormatStat(StatFanBase + ch, stats.Max(StatFanBase + ch));
            }
            Set_Fan_Average_Column();
        }

        //initialize all counters and AVG/Overall values
        public void Initialize_all_values()
        {
            stats.Reset();
        }
        #endregion

        private void Fan_Numeric_ValueChanged(object sender, RoutedPropertyChangedEventArgs<uint> e)
        {
            if (Sync_Fans_CheckBox.IsChecked == true)
            {
                Fan1_Slider.Value = Decimal.ToInt32(Fan1_Numeric.Value);
                Fan2_Slider.Value = Decimal.ToInt32(Fan1_Numeric.Value);
                Fan3_Slider.Value = Decimal.ToInt32(Fan1_Numeric.Value);
                Fan4_Slider.Value = Decimal.ToInt32(Fan1_Numeric.Value);
                Fan5_Slider.Value = Decimal.ToInt32(Fan1_Numeric.Value);
                Fan6_Slider.Value = Decimal.ToInt32(Fan1_Numeric.Value);
            }
            else
            {
                Fan1_Slider.Value = Decimal.ToInt32(Fan1_Numeric.Value);
                Fan2_Slider.Value = Decimal.ToInt32(Fan2_Numeric.Value);
                Fan3_Slider.Value = Decimal.ToInt32(Fan3_Numeric.Value);
                Fan4_Slider.Value = Decimal.ToInt32(Fan4_Numeric.Value);
                Fan5_Slider.Value = Decimal.ToInt32(Fan5_Numeric.Value);
                Fan6_Slider.Value = Decimal.ToInt32(Fan6_Numeric.Value);
            }
        }

        private void Fan_Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Sync_Fans_CheckBox.IsChecked == true)
            {
                Fan1_Numeric.Value = Convert.ToUInt32(Fan1_Slider.Value);
                Fan2_Numeric.Value = Convert.ToUInt32(Fan1_Slider.Value);
                Fan3_Numeric.Value = Convert.ToUInt32(Fan1_Slider.Value);
                Fan4_Numeric.Value = Convert.ToUInt32(Fan1_Slider.Value);
                Fan5_Numeric.Value = Convert.ToUInt32(Fan1_Slider.Value);
                Fan6_Numeric.Value = Convert.ToUInt32(Fan1_Slider.Value);
            }
            else
            {
                Fan1_Numeric.Value = Convert.ToUInt32(Fan1_Slider.Value);
                Fan2_Numeric.Value = Convert.ToUInt32(Fan2_Slider.Value);
                Fan3_Numeric.Value = Convert.ToUInt32(Fan3_Slider.Value);
                Fan4_Numeric.Value = Convert.ToUInt32(Fan4_Slider.Value);
                Fan5_Numeric.Value = Convert.ToUInt32(Fan5_Slider.Value);
                Fan6_Numeric.Value = Convert.ToUInt32(Fan6_Slider.Value);
            }
        }

        //for the Average Values CheckBox
        private void Average_Values(object sender, RoutedEventArgs e)
        {
            if (AVG_values.IsChecked == true)
            {
                CPU_box.Header = "CPU Current/AVG/Max";
                Properties.Settings.Default.AVG_Values = true;
                Properties.Settings.Default.Save();
            }
            else
            {
                CPU_box.Header = "CPU Current/Min/Max";
                Properties.Settings.Default.AVG_Values = false;
                Properties.Settings.Default.Save();
            }
            RenderCpuMinMaxColumns();   //swap the middle column immediately, not on the next sample
        }

        private void Reset_Button_Click(object sender, RoutedEventArgs e)
        {
            Reset_function();
        }

        private void Reset_function()
        {

        foreach (TextBox box in Fan_array)
            {
               box.Text = "0000";
            }

        for (int i = 0; i < 9; i++)
        {
            if ((i >= 3) && (i < 6))
            { CPU_array[i].Text = "0000"; }
            else { CPU_array[i].Text = "00.00"; }
        }
            //for the AVG values
            Initialize_all_values();
            Set_Fan_Average_Column();   //the Avg column is not part of Fan_array - clear it too
        }

        private void Set_Fan_Speed_Click(object sender, RoutedEventArgs e)
        {
            //Checked here, not per-fan: the wrappers' false means "the DEVICE refused", and a
            //pre-connect click would otherwise report all six fans as rejected.
            if (!Corsair_Commander_Connected)
            {
                SetStatus("● Commander PRO not connected", System.Windows.Media.Brushes.Orange);
                return;
            }

            //Any running loop is superseded by this press - either it is restarted below with the
            //newly typed target, or the user has switched that fan back to plain duty.
            StopRpmHold("Set Speed pressed");

            var rejected = new List<string>();
            for (int i = 0; i <= 5; i++)
            {
                if (!Set_Fan_Speed_Function_Commander_Pro(i)) rejected.Add("fan " + (i + 1));
            }

            //The status byte now matters: a rejection means the DEVICE said no - typically a
            //fixed-RPM target on a 3-pin/DC channel, which its firmware does not support. That
            //used to look identical to success and cost bench time.
            if (rejected.Count > 0)
            {
                SetStatus("● Rejected by device: " + string.Join(", ", rejected) +
                          " (RPM targets need a 4-pin/PWM channel)",
                          System.Windows.Media.Brushes.Orange);
            }
        }

        //With this function I am able to set the fans separately, either with speed or power.
        //Returns false when the device rejected the command.
        private bool Set_Fan_Speed_Function_Commander_Pro(int fan)
        {

            int fan_speed = 0;

           fan_speed = (int)Fan_Numeric_Boxes[fan].Value;

            if (fan_speed <= 100) //Gia to Power
                {
                    //0 is a real command here: it drives the channel to 0% duty (fan stop).
                    return Commander_Pro_Set_Fan_Power(fan, fan_speed);
                }

                else if (fan_speed > 100) //Gia to Speed
                {
                    //"Adjust fan speed from Tacho" ticked, and this is the fan the tachometer is on:
                    //hold the typed RPM with the software loop instead of asking the Commander,
                    //which cannot regulate by RPM without a tach signal of its own.
                    //True either way - StartRpmHold reports its own refusal reasons in the hold
                    //status line, and they must not be mislabelled as device rejections.
                    if (Tacho_Adjust_CheckBox.IsChecked == true && fan == tachAssignedChannel)
                    {
                        StartRpmHold(fan, fan_speed);
                        return true;
                    }
                    else
                    {
                        return Commander_Pro_Set_Fan_Speed(fan, fan_speed);
                    }
                }

            return true;   // unreachable: UIntegerUpDown cannot go below 0
        }

        #region Remote control API (IRemoteControlTarget)
        //Every member here can be called from an HTTP worker thread, so anything that touches WPF
        //or shared UI state is marshalled onto the UI thread with Dispatcher.Invoke. The HID calls
        //themselves are serialized inside CommanderProDevice and are safe from any thread.

        //Fan numbers are 1-6 on the wire (matching the UI labels); channels are 0-5 internally.
        private static bool TryChannel(int fan, out int channel, out string error)
        {
            channel = fan - 1;
            if (fan < 1 || fan > 6)
            {
                error = "fan must be 1-6 (got " + fan + ").";
                return false;
            }
            error = null;
            return true;
        }

        public object GetStatus()
        {
            return Dispatcher.Invoke(new Func<object>(delegate
            {
                var fans = new List<object>();
                for (int ch = 0; ch < 6; ch++)
                {
                    int rpm;
                    lock (fanRpmLock) rpm = latestFanRpm[ch];

                    string mode = "unknown";
                    if (ch < Fan_Mode_Controls.Length)
                    {
                        switch (Fan_Mode_Controls[ch].SelectedIndex)
                        {
                            case 0: mode = "auto"; break;
                            case 1: mode = "3pin"; break;
                            case 2: mode = "4pin"; break;
                            case 3: mode = "disconnect"; break;
                        }
                    }

                    fans.Add(new
                    {
                        fan = ch + 1,
                        rpm,
                        mode,
                        setpoint = ch < Fan_Numeric_Boxes.Length ? (int)Fan_Numeric_Boxes[ch].Value : 0,
                    });
                }

                double? tachRpm = bench_tach != null && bench_tach.IsConnected ? bench_tach.ReadRpm() : null;

                //The duty reported here must be honest about its source. The old behaviour - always
                //reporting the hold controller's last value even after it stopped - once showed 32%
                //while the fan really ran at ~50%, and cost bench time. While a hold runs the
                //controller's value IS live; otherwise report what pCUE last commanded on that
                //channel, or nothing at all when this session never has.
                int? holdDuty;
                string dutySource;
                if (rpmHold != null && rpmHold.IsRunning)
                {
                    holdDuty = rpmHold.CurrentDuty;
                    dutySource = "loop";
                }
                else
                {
                    int tracked = commander.LastCommandedDuty(holdChannel);
                    holdDuty = tracked >= 0 ? (int?)tracked : null;
                    dutySource = tracked >= 0 ? "tracked" : "unknown";
                }

                return new
                {
                    app = "pCUE",
                    version = AppUpdateService.InstalledVersion,
                    commander = new
                    {
                        connected = Corsair_Commander_Connected,
                        firmware = Commander_SN.Text,
                    },
                    cpu = new
                    {
                        temperature = CPU_array.Length > 0 ? CPU_array[0].Text : null,
                        mhz = CPU_array.Length > 3 ? CPU_array[3].Text : null,
                        load = CPU_array.Length > 6 ? CPU_array[6].Text : null,
                        monitoring = CpuDataTimer.Enabled,
                    },
                    fans,
                    tachometer = new
                    {
                        connected = bench_tach != null && bench_tach.IsConnected,
                        rpm = tachRpm,                      // null = stale or no signal
                        batteryLow = bench_tach != null && bench_tach.BatteryLow,
                        assignedFan = tachAssignedChannel >= 0 ? (int?)(tachAssignedChannel + 1) : null,
                    },
                    hold = new
                    {
                        running = rpmHold != null && rpmHold.IsRunning,
                        status = rpmHold != null ? rpmHold.Status.ToString() : FanHoldStatus.Idle.ToString(),
                        fan = holdChannel >= 0 ? (int?)(holdChannel + 1) : null,
                        duty = holdDuty,                    // null when this session never set one
                        dutySource,
                        target = (int)holdConfig.TargetRpm,
                        tachoAdjust = Tacho_Adjust_CheckBox.IsChecked == true,
                    },
                };
            }));
        }

        public string SetFanDuty(int fan, int duty)
        {
            if (!TryChannel(fan, out int channel, out string error)) return error;
            if (duty < 0 || duty > 100) return "value must be 0-100 (percent).";
            if (!Corsair_Commander_Connected) return "Commander PRO is not connected.";

            //A remote duty command is the caller taking over from the hold loop.
            Dispatcher.Invoke(new Action(delegate { StopRpmHold("remote duty command"); }));
            if (!Commander_Pro_Set_Fan_Power(channel, duty))
                return "device rejected WRITE_FAN_POWER for fan " + fan + ".";
            return null;
        }

        public string SetFanRpm(int fan, int rpm)
        {
            if (!TryChannel(fan, out int channel, out string error)) return error;
            if (rpm <= 100 || rpm > 3500) return "value must be 101-3500 RPM (<=100 would be read as a percent).";
            if (!Corsair_Commander_Connected) return "Commander PRO is not connected.";

            Dispatcher.Invoke(new Action(delegate { StopRpmHold("remote rpm command"); }));
            if (!Commander_Pro_Set_Fan_Speed(channel, rpm))
                return "device rejected WRITE_FAN_SPEED for fan " + fan +
                       " - fixed RPM needs a 4-pin/PWM channel.";
            return null;
        }

        public string SetFanMode(int fan, string mode)
        {
            if (!TryChannel(fan, out int channel, out string error)) return error;
            if (!Corsair_Commander_Connected) return "Commander PRO is not connected.";

            int index;
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "auto": index = 0; break;
                case "3pin": case "3-pin": case "dc": index = 1; break;
                case "4pin": case "4-pin": case "pwm": index = 2; break;
                case "disconnect": case "off": index = 3; break;
                default: return "value must be auto | 3pin | 4pin | disconnect.";
            }

            //Setting SelectedIndex raises SelectionChanged, which is what writes to the device.
            Dispatcher.Invoke(new Action(delegate { Fan_Mode_Controls[channel].SelectedIndex = index; }));
            if (!fanModeWriteOk[channel])
                return "device rejected the mode change for fan " + fan + ".";
            return null;
        }

        public string StartHold(int fan, int rpm)
        {
            if (!TryChannel(fan, out int channel, out string error)) return error;
            if (rpm <= 0 || rpm > 3500) return "rpm must be 1-3500.";

            string result = null;
            Dispatcher.Invoke(new Action(delegate
            {
                if (rpmHold != null && rpmHold.IsRunning) { result = "A hold is already running; stop it first."; return; }
                Tach_Fan_Assign.SelectedIndex = fan;          // index 0 is "None"
                Tacho_Adjust_CheckBox.IsChecked = true;       // keep the UI honest about what is driving
                Fan_Numeric_Boxes[fan - 1].Value = (uint)rpm; // the fan's own box is the setpoint now
                StartRpmHold(fan - 1, rpm);
                //StartRpmHold reports its own reason (no feedback, not connected, ...).
                if (rpmHold == null || !rpmHold.IsRunning) result = Hold_Status_Label.Text;
            }));
            return result;
        }

        public string StopHold()
        {
            Dispatcher.Invoke(new Action(delegate { StopRpmHold("remote stop"); }));
            return null;
        }

        public object GetHoldConfig()
        {
            return new
            {
                holdConfig.TargetRpm,
                holdConfig.RpmTolerance,
                holdConfig.MinDuty,
                holdConfig.MaxDuty,
                holdConfig.StartDuty,
                holdConfig.CoarseDutyStep,
                holdConfig.FineDutyStep,
                holdConfig.CoarseErrorThreshold,
                holdConfig.SampleIntervalMs,
                holdConfig.SettleDelayMs,
                holdConfig.StabilizationTimeMs,
                holdConfig.TimeoutMs,
                holdConfig.RpmFilterWindow,
                holdConfig.MaxInvalidRpmSamples,
                holdConfig.DitherEnabled,
            };
        }

        /// <summary>
        /// Live-tune the hold loop. Only the supplied keys change. Takes effect on the next Start;
        /// the target can also be retargeted live while running.
        /// </summary>
        public string SetHoldConfig(Func<string, double?> get)
        {
            double? v;
            if ((v = get("tolerance")).HasValue) holdConfig.RpmTolerance = v.Value;
            if ((v = get("minDuty")).HasValue) holdConfig.MinDuty = (int)v.Value;
            if ((v = get("maxDuty")).HasValue) holdConfig.MaxDuty = (int)v.Value;
            if ((v = get("startDuty")).HasValue) holdConfig.StartDuty = (int)v.Value;
            if ((v = get("coarseStep")).HasValue) holdConfig.CoarseDutyStep = (int)v.Value;
            if ((v = get("fineStep")).HasValue) holdConfig.FineDutyStep = (int)v.Value;
            if ((v = get("coarseThreshold")).HasValue) holdConfig.CoarseErrorThreshold = v.Value;
            if ((v = get("sampleInterval")).HasValue) holdConfig.SampleIntervalMs = (int)v.Value;
            if ((v = get("settleDelay")).HasValue) holdConfig.SettleDelayMs = (int)v.Value;
            if ((v = get("stabilizeTime")).HasValue) holdConfig.StabilizationTimeMs = (int)v.Value;
            if ((v = get("timeout")).HasValue) holdConfig.TimeoutMs = (int)v.Value;
            if ((v = get("filterWindow")).HasValue) holdConfig.RpmFilterWindow = Math.Max(1, (int)v.Value);
            if ((v = get("maxInvalid")).HasValue) holdConfig.MaxInvalidRpmSamples = Math.Max(1, (int)v.Value);
            if ((v = get("dither")).HasValue)
            {
                bool want = v.Value != 0;
                AppLog.Info("Hold dither " + (want ? "enabled" : "disabled") + " via remote API.");
                holdConfig.DitherEnabled = want;
            }

            if ((v = get("target")).HasValue)
            {
                holdConfig.TargetRpm = v.Value;
                if (holdChannel >= 0 && holdChannel < Fan_Numeric_Boxes.Length)
                    Dispatcher.Invoke(new Action(delegate { Fan_Numeric_Boxes[holdChannel].Value = (uint)v.Value; }));
                if (rpmHold != null && rpmHold.IsRunning) rpmHold.UpdateTarget(v.Value);
            }

            if (holdConfig.MinDuty < 0) holdConfig.MinDuty = 0;
            if (holdConfig.MaxDuty > 100) holdConfig.MaxDuty = 100;
            if (holdConfig.RpmTolerance <= 0) holdConfig.RpmTolerance = 25;

            AppLog.Info("Hold config updated via remote API.");
            return null;
        }

        /// <summary>
        /// Renders a window to a PNG. Uses RenderTargetBitmap over the live visual tree rather than
        /// a desktop grab, so it works regardless of what is on top, whether the window is minimised,
        /// or whether anyone is logged in at the console. It captures the client area only - window
        /// chrome is drawn by Windows, not by WPF.
        /// </summary>
        public byte[] CaptureScreenshot(string window)
        {
            return Dispatcher.Invoke(new Func<byte[]>(delegate
            {
                try
                {
                    Window target = this;
                    if (!string.IsNullOrEmpty(window) &&
                        window.Trim().Equals("help", StringComparison.OrdinalIgnoreCase))
                    {
                        //Measure/Arrange alone is NOT enough: a window that has never been shown has
                        //no built visual tree, and RenderTargetBitmap then produces a blank image.
                        //Show it far off-screen so WPF applies templates and lays it out for real.
                        var help = new HelpWindow
                        {
                            WindowStartupLocation = WindowStartupLocation.Manual,
                            Left = -32000,
                            Top = -32000,
                            ShowInTaskbar = false,
                        };
                        try
                        {
                            help.Show();
                            help.UpdateLayout();
                            return RenderToPng(help, (int)Math.Ceiling(help.ActualWidth),
                                                     (int)Math.Ceiling(help.ActualHeight));
                        }
                        finally { try { help.Close(); } catch { } }
                    }

                    int w = (int)Math.Ceiling(target.ActualWidth);
                    int h = (int)Math.Ceiling(target.ActualHeight);
                    if (w <= 0 || h <= 0) { w = (int)target.Width; h = (int)target.Height; }
                    return RenderToPng(target, w, h);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Screenshot failed: " + ex.Message);
                    return null;
                }
            }));
        }

        private static byte[] RenderToPng(System.Windows.Media.Visual visual, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(visual);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var ms = new System.IO.MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        public string SetCommanderOpen(bool open)
        {
            string result = null;
            Dispatcher.Invoke(new Action(delegate
            {
                bool isOpen = Open_Corsair_Commander.Content.ToString() == "Close";
                if (open == isOpen) { result = null; return; }      // already in the wanted state
                Open_Corsair_Commander_Click(this, null);
                if (open && !Corsair_Commander_Connected) result = "Could not open the Commander PRO.";
            }));
            return result;
        }

        public string SetCpuMonitoring(bool on)
        {
            Dispatcher.Invoke(new Action(delegate
            {
                bool running = Start_CPU_data.Content.ToString() == "Stop";
                if (on != running) Start_CPU_data_Click(this, null);
            }));
            return null;
        }

        public string SetTachConnected(bool connected)
        {
            string result = null;
            Dispatcher.Invoke(new Action(delegate
            {
                if (bench_tach == null) { result = "Tachometer driver not available."; return; }
                if (connected == bench_tach.IsConnected) return;
                try
                {
                    if (connected) bench_tach.Connect(); else bench_tach.Disconnect();
                }
                catch (Exception ex) { result = ex.Message; }
            }));
            return result;
        }

        public string SetTachAssignment(int fan)
        {
            if (fan < 0 || fan > 6) return "fan must be 0 (none) or 1-6.";
            Dispatcher.Invoke(new Action(delegate { Tach_Fan_Assign.SelectedIndex = fan; }));
            return null;
        }

        public string ResetStats()
        {
            Dispatcher.Invoke(new Action(delegate { Reset_function(); }));
            return null;
        }

        //Command-line driven so nothing is persistently exposed and no token is ever written to disk:
        //   pCUE.exe --remote
        //   pCUE.exe --remote --remote-prefix=http://+:5056/ --remote-token=SECRET
        //   pCUE.exe --debug            (verbose log + log file)
        private void StartRemoteControlIfRequested()
        {
            bool debug = false;

            //Command-line flags are still honoured (handy for a one-off run), but the normal way to
            //turn remote control on is the Remote checkbox in the UI, which persists.
            foreach (string raw in Environment.GetCommandLineArgs())
            {
                string a = raw.Trim();
                if (a.Equals("--debug", StringComparison.OrdinalIgnoreCase)) debug = true;
                else if (a.Equals("--remote", StringComparison.OrdinalIgnoreCase))
                    Properties.Settings.Default.Remote_Enabled = true;
                else if (a.StartsWith("--remote-port=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(a.Substring("--remote-port=".Length), out int p))
                        Properties.Settings.Default.Remote_Port = p;
                    Properties.Settings.Default.Remote_Enabled = true;
                }
                else if (a.StartsWith("--remote-token=", StringComparison.OrdinalIgnoreCase))
                {
                    Properties.Settings.Default.Remote_Token = a.Substring("--remote-token=".Length);
                    Properties.Settings.Default.Remote_Enabled = true;
                }
            }

            //Debug logging is also a persisted UI option; the flag just forces it on.
            if (debug || Properties.Settings.Default.Debug_Logging)
            {
                AppLog.Level = LogLevel.Debug;
                AppLog.EnableFile();
            }
            AppLog.Info("pCUE " + AppUpdateService.InstalledVersion + " starting" +
                        (AppLog.Level == LogLevel.Debug ? " (debug logging on)" : ""));

            //Restore the UI to the saved state, then start the server if it was left enabled.
            Remote_Port_Box.Text = Properties.Settings.Default.Remote_Port.ToString();
            Remote_Token_Box.Password = Properties.Settings.Default.Remote_Token ?? "";
            Debug_Log_CheckBox.IsChecked = AppLog.Level == LogLevel.Debug;
            Remote_Enable_CheckBox.IsChecked = Properties.Settings.Default.Remote_Enabled;   //fires the handler
        }

        //Start/stop the remote API + discovery beacon to match the checkbox, and report the URL the
        //user should connect to. Called from the checkbox handler and at start-up.
        private void ApplyRemoteControlState()
        {
            bool wanted = Remote_Enable_CheckBox.IsChecked == true;

            //Always tear down first so a port/token edit takes effect on the next tick.
            try { remoteServer?.Dispose(); } catch (Exception ex) { AppLog.Warn("Remote stop failed: " + ex.Message); }
            try { discoveryBeacon?.Dispose(); } catch (Exception ex) { AppLog.Warn("Beacon stop failed: " + ex.Message); }
            remoteServer = null;
            discoveryBeacon = null;

            if (!wanted)
            {
                SetRemoteStatus("● Off", System.Windows.Media.Brushes.Gainsboro);
                return;
            }

            if (!int.TryParse(Remote_Port_Box.Text.Trim(), out int port) || port < 1 || port > 65535)
            {
                SetRemoteStatus("● Bad port", UpdateAlertBrush);
                return;
            }

            string token = Remote_Token_Box.Password ?? "";

            //With no token pCUE refuses every non-loopback request, so binding to all interfaces
            //would be pointless as well as risky - stay on loopback until a token is set.
            string prefix = string.IsNullOrEmpty(token)
                ? "http://127.0.0.1:" + port + "/"
                : "http://+:" + port + "/";

            try
            {
                remoteServer = new RemoteControlServer(this, prefix, token);
                remoteServer.Start();

                discoveryBeacon = new DiscoveryBeacon(port, !string.IsNullOrEmpty(token));
                discoveryBeacon.Start();

                string where = string.IsNullOrEmpty(token)
                    ? "● Local only :" + port
                    : "● LAN " + LocalIPv4() + ":" + port;
                SetRemoteStatus(where, System.Windows.Media.Brushes.Lime);
                AppLog.Info("Remote control enabled on " + prefix +
                            (string.IsNullOrEmpty(token) ? " (loopback only - set a token for LAN access)" : " (token required)"));
            }
            catch (Exception ex)
            {
                remoteServer = null;
                discoveryBeacon = null;
                Remote_Enable_CheckBox.IsChecked = false;
                SetRemoteStatus("● Failed", UpdateAlertBrush);
                AppLog.Error("Remote control could not start: " + ex.Message);
                MessageBox.Show("Remote control could not start:\n\n" + ex.Message +
                    "\n\nA LAN port may need to be allowed through the firewall.",
                    "pCUE", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Remote_Settings_Changed(object sender, RoutedEventArgs e)
        {
            if (Remote_Port_Box == null || Remote_Token_Box == null) return;   //still loading XAML

            Properties.Settings.Default.Remote_Enabled = Remote_Enable_CheckBox.IsChecked == true;
            if (int.TryParse(Remote_Port_Box.Text.Trim(), out int port) && port > 0 && port < 65536)
                Properties.Settings.Default.Remote_Port = port;
            Properties.Settings.Default.Remote_Token = Remote_Token_Box.Password ?? "";
            Properties.Settings.Default.Save();

            ApplyRemoteControlState();
        }

        private void Help_Button_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var help = new HelpWindow { Owner = this };
                help.ShowDialog();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not open the help window: " + ex.Message);
            }
        }

        private void Auto_Connect_Changed(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.Auto_Connect = Auto_Connect_CheckBox.IsChecked == true;
            Properties.Settings.Default.Save();
        }

        private void Debug_Log_Changed(object sender, RoutedEventArgs e)
        {
            bool on = Debug_Log_CheckBox.IsChecked == true;
            Properties.Settings.Default.Debug_Logging = on;
            Properties.Settings.Default.Save();

            AppLog.Level = on ? LogLevel.Debug : LogLevel.Info;
            if (on) AppLog.EnableFile();
            AppLog.Info("Debug logging " + (on ? "ON" : "OFF") +
                        (AppLog.FileEnabled ? " (file: " + AppLog.FilePath + ")" : ""));
        }

        private void SetRemoteStatus(string text, System.Windows.Media.Brush brush)
        {
            Remote_Status_Label.Text = text;
            Remote_Status_Label.Foreground = brush;
            Remote_Status_Label.ToolTip = remoteServer != null
                ? "API: " + remoteServer.Prefix + "   Discovery: UDP 5057"
                : text;
        }

        /// <summary>Best-effort primary IPv4, just for display.</summary>
        private static string LocalIPv4()
        {
            try
            {
                using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
                s.Connect("8.8.8.8", 65530);   //no traffic is sent; this just selects a route
                return ((System.Net.IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
            catch { return Environment.MachineName; }
        }
        #endregion

        #region Closed-loop RPM hold
        //Feedback for the control loop: the RPM most recently shown for the held channel. That value
        //is already the bench tachometer's reading when one is assigned to this fan and fresh, and
        //the Commander's own tach reading otherwise - so the loop works for a fan with no usable
        //tach wire AND for a 3-pin fan the Commander can read but refuses to regulate.
        //Returns null (an invalid sample, from the loop's point of view) when there is no
        //trustworthy reading: nothing polled recently, or a zero, which means "no signal".
        private double? ReadHeldFanRpm()
        {
            int ch = holdChannel;
            if (ch < 0 || ch > 5) return null;

            lock (fanRpmLock)
            {
                if (latestFanRpmUtc == DateTime.MinValue) return null;
                //The poll loop runs every 500 ms; anything older than 2 s means it has stalled.
                if ((DateTime.UtcNow - latestFanRpmUtc).TotalMilliseconds > 2000) return null;
                int rpm = latestFanRpm[ch];
                return rpm > 0 ? (double?)rpm : null;
            }
        }

        //True while the RPM poll loop is delivering fresh samples, whatever their VALUE. This is the
        //"can this fan be measured at all" test, as opposed to ReadHeldFanRpm's "is it turning right
        //now" - a stopped fan is a perfectly normal starting point for the hold, which spins it up.
        private bool HasFreshRpmSource()
        {
            lock (fanRpmLock)
            {
                if (latestFanRpmUtc == DateTime.MinValue) return false;
                return (DateTime.UtcNow - latestFanRpmUtc).TotalMilliseconds <= 2000;
            }
        }

        //Start the closed loop on a channel. Reports why it cannot start rather than failing quietly.
        //Called from Set Speed (the normal route) and from the remote API.
        private void StartRpmHold(int channel, double targetRpm)
        {
            if (channel < 0 || channel > 5) { SetHoldStatus("Pick a fan first.", UpdateAlertBrush); return; }
            if (!Corsair_Commander_Connected) { SetHoldStatus("Open the Commander PRO first.", UpdateAlertBrush); return; }

            holdChannel = channel;

            //Refuse only when nothing is measuring at all. A STOPPED fan reads 0, which
            //ReadHeldFanRpm reports as "no reading" - and refusing on that deadlocked the app: no
            //duty was written, so the fan could not start, so the reading stayed 0, so every later
            //Set Speed was refused too. The fan became unrecoverable from the UI until the user
            //unticked the checkbox, and the message blamed the tachometer, which was fine.
            //The loop applies its start duty and waits SettleDelayMs before its first sample, which
            //is ample for a fan to spin up; if the channel genuinely cannot be measured it stops
            //itself on consecutive bad samples and leaves the fan running rather than stopped.
            if (!HasFreshRpmSource())
            {
                SetHoldStatus("No RPM readings for Fan #" + (channel + 1) +
                              " - connect the tachometer and point it at this fan.", UpdateAlertBrush);
                holdChannel = -1;
                return;
            }

            //Start from the duty the fan is ALREADY running at. With a fixed 40% start, asking for
            //400 RPM while the fan sat at 350 first threw it up past 1100 RPM and then needed about
            //seven coarse steps to walk back down - roughly half a minute of travel for a 50 RPM
            //change. Starting where the fan is makes a small change a couple of 1% steps.
            int knownDuty = commander.LastCommandedDuty(channel);
            int reportedDuty = commander.ReadFanPower(channel);

            //A 0 read-back while pCUE has no tracked duty AND the fan is turning is suspicious -
            //a spinning fan at a real 0% duty is not physically plausible - so retry once before
            //degrading to the 40% kick (a failed READ_FAN_POWER used to look identical to a
            //genuine 0% and kicked a perfectly good fan).
            bool suspiciousRead = knownDuty < 0 && reportedDuty == 0 && ReadHeldFanRpm() != null;
            if (suspiciousRead)
            {
                reportedDuty = commander.ReadFanPower(channel);
                AppLog.Warn("HOLD start duty: Commander reported 0% with the fan turning - re-read gave " +
                            reportedDuty + "%");
            }

            //Both sources are logged every time, not just the one used: it is the only way to see
            //the device read-back agreeing (or not) with what pCUE believes it commanded.
            AppLog.Info("HOLD start duty: pCUE tracked=" + (knownDuty < 0 ? "none" : knownDuty + "%") +
                        ", Commander reports=" + reportedDuty + "%");

            //Prefer what pCUE commanded; fall back to what the Commander reports. The device keeps
            //its duty across an app restart but pCUE's memory does not, so without the read-back the
            //FIRST hold of every session ignored a perfectly good running fan and kicked it to 40%.
            //A failed read returns 0 and simply degrades to that same kick.
            int startFrom = knownDuty >= 0 ? knownDuty : reportedDuty;

            if (ReadHeldFanRpm() != null && startFrom > 0)
            {
                holdConfig.StartDuty = startFrom;
                holdConfig.StartDutyIsCurrent = true;
            }
            else
            {
                if (suspiciousRead && reportedDuty == 0)
                    AppLog.Warn("HOLD starting from " + HoldKickStartDuty + "% kick: both duty reads returned 0 " +
                                "while the fan was turning - check the channel.");
                holdConfig.StartDuty = HoldKickStartDuty;
                holdConfig.StartDutyIsCurrent = false;
            }

            holdConfig.TargetRpm = targetRpm;

            rpmHold = new FanRpmHoldController(
                duty =>
                {
                    //A rejected write must stop the loop rather than let it steer blindly:
                    //the controller catches this and faults out with the reason.
                    if (!Commander_Pro_Set_Fan_Power(holdChannel, duty))
                        throw new InvalidOperationException(
                            "Commander PRO rejected the duty write for fan " + (holdChannel + 1) + ".");
                },
                ReadHeldFanRpm,
                () => Corsair_Commander_Connected);

            rpmHold.SnapshotUpdated += Rpm_Hold_SnapshotUpdated;
            rpmHold.StatusChanged += Rpm_Hold_StatusChanged;

            try
            {
                rpmHold.StartAsync(holdConfig);
                SetHoldStatus("Holding Fan #" + (channel + 1) + " at " + targetRpm.ToString("0") + " RPM...", UpdateInfoBrush);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not start RPM hold: " + ex.Message);
                SetHoldStatus("Could not start: " + ex.Message, UpdateAlertBrush);
            }
        }

        //Unticking hands the fan back to plain duty control; the fan keeps its current duty until
        //the next Set Speed, rather than jumping.
        private void Tacho_Adjust_Changed(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.Tacho_Adjust = Tacho_Adjust_CheckBox.IsChecked == true;
            Properties.Settings.Default.Save();

            if (Tacho_Adjust_CheckBox.IsChecked != true)
            {
                StopRpmHold("tacho adjust switched off");
                SetHoldStatus("", UpdateInfoBrush);
            }
        }

        private void Rpm_Hold_SnapshotUpdated(object sender, FanHoldSnapshot s)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    string text = s.Status + "  " + Math.Round(s.FilteredRpm) + " RPM @ " + s.Duty + "%";
                    if (!string.IsNullOrEmpty(s.Note)) text += "  (" + s.Note + ")";
                    SetHoldStatus(text, s.Status == FanHoldStatus.Fault ? UpdateAlertBrush
                                      : s.Status == FanHoldStatus.Stable ? System.Windows.Media.Brushes.Lime
                                      : UpdateInfoBrush);
                }));
            }
            catch (Exception ex) { Debug.WriteLine("pCUE: hold snapshot dispatch failed: " + ex.Message); }
        }

        private void Rpm_Hold_StatusChanged(object sender, FanHoldStatus status)
        {
            //The snapshot handler already paints the status line; nothing else to switch now that
            //the loop is driven by the normal Set Speed button rather than its own toggle.
            AppLog.Debug("HOLD status -> " + status);
        }

        private void SetHoldStatus(string text, System.Windows.Media.Brush brush)
        {
            Hold_Status_Label.Text = text;
            Hold_Status_Label.Foreground = brush;
            Hold_Status_Label.ToolTip = text;
        }

        //Stop the loop and forget it. Used on manual Set Speed (the user is taking over), on
        //Commander disconnect, and on shutdown.
        private void StopRpmHold(string why)
        {
            if (rpmHold == null) return;
            if (rpmHold.IsRunning)
            {
                rpmHold.Stop();
                Debug.WriteLine("pCUE: RPM hold stopped (" + why + ").");
            }
        }
        #endregion

        #region Updates
        //The bottom strip sits on the bright green end of the window gradient, where orange and
        //lime wash out. Everything down there is plain white, with yellow reserved for "look at
        //this" (update available, failure, lost signal).
        static readonly System.Windows.Media.Brush UpdateInfoBrush = System.Windows.Media.Brushes.White;
        static readonly System.Windows.Media.Brush UpdateAlertBrush = System.Windows.Media.Brushes.Yellow;

        //Manual check. Reports the outcome inline and, when a newer build exists, offers to
        //download it. Nothing is ever downloaded or launched without the user saying so.
        private async void Update_Check_Button_Click(object sender, RoutedEventArgs e)
        {
            await RunUpdateCheck(true);
        }

        private void Update_On_Start_Changed(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.Update_Check_On_Start = Update_On_Start_CheckBox.IsChecked == true;
            Properties.Settings.Default.Save();
        }

        //Shared by the button and the optional start-up check. When interactive is false this only
        //reports (start-up must never pop dialogs); when true it may offer the download.
        private async Task RunUpdateCheck(bool interactive)
        {
            if (updateService == null) return;

            if (interactive) Update_Check_Button.IsEnabled = false;
            SetUpdateStatus("Checking for updates...", UpdateInfoBrush);

            try
            {
                AppUpdateInfo info = await updateService
                    .CheckAsync(Properties.Settings.Default.Update_Manifest_Url);

                switch (info.State)
                {
                    case UpdateCheckState.UpToDate:
                        SetUpdateStatus(info.Message, UpdateInfoBrush);
                        break;

                    case UpdateCheckState.UpdateAvailable:
                        SetUpdateStatus(info.Message, UpdateAlertBrush);
                        if (interactive) await OfferUpdate(info);
                        break;

                    default:
                        SetUpdateStatus(info.Message, UpdateAlertBrush);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: update check failed: " + ex.Message);
                SetUpdateStatus("Update check failed: " + ex.Message, UpdateAlertBrush);
            }
            finally
            {
                if (interactive) Update_Check_Button.IsEnabled = true;
            }
        }

        //Download (verified) then, after a second explicit confirmation, launch the installer and
        //close pCUE - a running app cannot overwrite its own files.
        private async Task OfferUpdate(AppUpdateInfo info)
        {
            MessageBoxResult wants = MessageBox.Show(
                info.Message + "\n\nDownload it now?",
                "pCUE update available", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (wants != MessageBoxResult.Yes) return;

            string installer;
            try
            {
                var progress = new Progress<string>(text => SetUpdateStatus(text, UpdateInfoBrush));
                installer = await updateService.DownloadVerifiedInstallerAsync(info, progress);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: update download failed: " + ex.Message);
                SetUpdateStatus("Download failed: " + ex.Message, UpdateAlertBrush);
                MessageBox.Show(ex.Message, "pCUE update", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetUpdateStatus("Downloaded and verified pCUE " + info.AvailableVersion + ".", UpdateInfoBrush);

            MessageBoxResult install = MessageBox.Show(
                "pCUE " + info.AvailableVersion + " was downloaded and its checksum verified.\n\n" +
                "pCUE must close so the installer can replace its files. Run the installer now?",
                "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (install != MessageBoxResult.Yes)
            {
                SetUpdateStatus("Installer saved to " + installer, UpdateAlertBrush);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                suppressCloseConfirm = true;    //the user already confirmed; skip "Really close?"
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: could not launch installer: " + ex.Message);
                SetUpdateStatus("Could not launch the installer: " + ex.Message, UpdateAlertBrush);
            }
        }

        private void SetUpdateStatus(string text, System.Windows.Media.Brush brush)
        {
            Update_Status_Label.Text = text;
            Update_Status_Label.Foreground = brush;
            Update_Status_Label.ToolTip = text;   //full text on hover; the label trims with ellipsis
        }
        #endregion

        #region Bench Tachometer (external USB-HID)
        //Connect / disconnect the external bench tachometer.
        private void Tach_Connect_Button_Click(object sender, RoutedEventArgs e)
        {
            if (bench_tach == null) return;
            try
            {
                if (!bench_tach.IsConnected) bench_tach.Connect();   //ConnectionChanged updates the UI
                else bench_tach.Disconnect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: tach connect/disconnect failed: " + ex.Message);
                Tach_Status_Label.Text = "● Not found";
                Tach_Status_Label.Foreground = UpdateAlertBrush;   //yellow, not orange - orange is unreadable down here
                MessageBox.Show(ex.Message, "Bench Tachometer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        //Choose which fan the tachometer feeds. Index 0 = None; 1..6 = Fan #1..#6.
        //Read from the sender so an early SelectionChanged (during XAML init) can't NRE on the field.
        private void Tach_Fan_Assign_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int sel = ((ComboBox)sender).SelectedIndex;
            tachAssignedChannel = (sel >= 1 && sel <= 6) ? (sel - 1) : -1;
        }

        //Refresh the tach panel from the SAME freshness rule the fan column uses (ReadRpm() returns
        //null once a reading is older than StalenessMs). Driven by the 500 ms UI timer rather than
        //the driver's ReadingChanged event, because that event only fires on a successful decode:
        //a tachometer that is still enumerated but has stopped sending frames (auto power-off,
        //blocked beam) would otherwise leave the last RPM on screen forever, contradicting the fan
        //column and inviting the operator to record a dead instrument's reading.
        private void Update_Tach_Panel()
        {
            if (bench_tach == null || Tach_RPM_Readout == null) return;

            if (!bench_tach.IsConnected)
            {
                Tach_RPM_Readout.Text = "----";
                Tach_Battery_Label.Visibility = Visibility.Collapsed;
                tachBatteryWarned = false;      //re-arm the warning for the next session
                tachBatteryLowSeen = false;     //changing the cell power-cycles the meter, so this
                                                //is exactly where a fresh battery clears the label
                return;
            }

            double? rpm = bench_tach.ReadRpm();
            if (rpm.HasValue)
            {
                Tach_RPM_Readout.Text = Math.Round(rpm.Value).ToString();
                Tach_RPM_Readout.Foreground = UpdateInfoBrush;
            }
            else
            {
                //Connected but no fresh frame - say so instead of showing a stale number.
                Tach_RPM_Readout.Text = "no signal";
                Tach_RPM_Readout.Foreground = UpdateAlertBrush;
            }

            //Battery state is shown whenever the meter is connected, INDEPENDENT of whether a
            //reading is fresh. Tying it to a fresh reading hid the warning in exactly the case
            //that matters: a battery flat enough to stop the meter sending frames.
            bool low = bench_tach.BatteryLow;
            if (low) tachBatteryLowSeen = true;
            Tach_Battery_Label.Visibility = tachBatteryLowSeen ? Visibility.Visible : Visibility.Collapsed;

            if (low && !tachBatteryWarned)
            {
                tachBatteryWarned = true;
                AppLog.Warn("Tachometer battery is LOW - readings may stop or drift. Replace it.");

                //Say it once per connection, and not from inside this timer tick: a modal dialog
                //here would stall the UI timer that drives the whole panel.
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    MessageBox.Show(
                        "The bench tachometer reports a LOW BATTERY.\n\n" +
                        "Replace it before trusting these readings - a flat battery makes the meter " +
                        "drift and then stop sending, which will also stall any RPM hold that is " +
                        "using it for feedback.",
                        "pCUE - tachometer battery low", MessageBoxButton.OK, MessageBoxImage.Warning);
                }));
            }
            //Deliberately NOT re-armed when the flag merely goes clear again. It flickers, and
            //re-arming on every dip would fire the modal dialog over and over on one tired cell.
            //Disconnecting the meter re-arms it, so fresh cells still get a fresh warning.
        }

        //Connection state -> update the button caption and status line.
        private void Bench_Tach_ConnectionChanged(object sender, bool connected)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Tach_Connect_Button.Content = connected ? "Disconnect" : "Connect Tach";
                    //Same wording and colours as the Commander PRO status line above.
                    Tach_Status_Label.Text = connected ? "● Connected" : "● Disconnected";
                    Tach_Status_Label.Foreground = connected
                        ? System.Windows.Media.Brushes.Lime
                        : System.Windows.Media.Brushes.Gainsboro;
                    if (!connected)
                    {
                        Tach_RPM_Readout.Text = "----";
                        Tach_RPM_Readout.Foreground = UpdateInfoBrush;
                        Tach_Battery_Label.Visibility = Visibility.Collapsed;
                    }
                }));
            }
            catch (Exception ex) { Debug.WriteLine("pCUE: tach connection UI dispatch failed: " + ex.Message); }
        }
        #endregion

        private void Startup(bool add)
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (add)
            {
                //Surround path with " " to make sure that there are no problems
                //if path contains spaces.
                key.SetValue("pCUE", "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
            }
            else
                key.DeleteValue("pCUE");

            key.Close();
        }

        private void Autostart(object sender, RoutedEventArgs e)
        {
            if (autostartCheckBox.IsChecked == true)
            {
                this.Startup(true);
                Properties.Settings.Default.AutoStart1 = true;
                Properties.Settings.Default.Save();
            }
            else
            {
                this.Startup(false);
                Properties.Settings.Default.AutoStart1 = false;
                Properties.Settings.Default.Save();
            }
        }

        private void Start_CPU_data_Click(object sender, RoutedEventArgs e)
        {
            if (Start_CPU_data.Content.ToString() == "Start")
            {
                try
                {
                    Start_CPU_data.Content = "Stop";

                    //LibreHardwareMonitor is opened at startup; just begin polling its CPU sensors.
                    CpuDataTimer.Start();
                }
                catch (Exception ex)
                {
                    CpuDataTimer.Stop();
                    Start_CPU_data.Content = "Start";
                    Debug.WriteLine("pCUE: could not start CPU monitoring: " + ex.Message);
                }
            }
            else if (Start_CPU_data.Content.ToString() == "Stop")
            {
                Start_CPU_data.Content = "Start";
                CpuDataTimer.Stop();
            }
        }

    }
}


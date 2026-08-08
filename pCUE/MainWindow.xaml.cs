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

//struct ResultsStruct
//{
//    public double Min;
//    public double Max;
//    public double Average;
//    public double Sum;
//};

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

//gia na ekteleite i PerformClick kanonika
namespace System.Windows.Controls
{
    public static class MyExt
    {
        public static void PerformClick(this Button btn)
        {
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}

namespace pCUE
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, IRemoteControlTarget
    {
        //App directory
        public static string BaseDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

        //gia to app autostart
        bool isinstartup = false;
        
        //gia to Corsair Commander Pro
        HidSharp.HidDeviceLoader Commander_Loader = new HidSharp.HidDeviceLoader();
        HidSharp.HidStream stream;
        HidSharp.HidDevice device;
        byte[] outbuf = new byte[64];
        byte[] inbuf = new byte[16];
        bool Corsair_Commander_Connected = false;

        //Serializes every HID stream transaction so the background poll loop and the
        //UI-thread commands (set speed, set mode, connect) can never overlap on the device.
        readonly object hidLock = new object();
        //Background fan-RPM polling (replaces the old UI-thread WinForms timer).
        CancellationTokenSource fanPollCts;
        Task fanPollTask;
        volatile bool fanPollErrorLogged = false;
        //Auto-disconnect once this many poll passes fail back-to-back (a few seconds of dead I/O).
        const int MaxConsecutivePollFailures = 3;
        
        //gia na exo tin teleia os decimal separator panta
        System.IFormatProvider cultureUS = new System.Globalization.CultureInfo("en-US");      

        //gia toys sensors toy GPU-Z (Nvidia defaults)
        int fan_speed = 4;
        int gpu_temperature = 2;
        int gpu_load = 6;
        int gpu_Watts = 10;
        int core_clock = 0;
        int memory_clock = 1;
        int vddc = 13;
        int cpu_temperature = 14;

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

        //Timer for min-max-avg values
        static System.Windows.Forms.Timer Set_Min_Max_AVG_timer = new System.Windows.Forms.Timer();


        //for min-max-avg
        int counter_min_max_avg = 0;
        int CPU_temp_counter_min_max_avg = 0;
        int CPU_MHz_counter_min_max_avg = 0;
        int CPU_Load_counter_min_max_avg = 0;
        int avg_fan1_counter_min_max_avg = 0;
        int avg_fan2_counter_min_max_avg = 0;
        int avg_fan3_counter_min_max_avg = 0;
        int avg_fan4_counter_min_max_avg = 0;
        int avg_fan5_counter_min_max_avg = 0;
        int avg_fan6_counter_min_max_avg = 0;

        double overal_CPU_temp = 0.0;
        double overal_CPU_MHz = 0.0;
        double overal_CPU_Load = 0.0;
        double overal_fan1_speed = 0.0;
        double overal_fan2_speed = 0.0;
        double overal_fan3_speed = 0.0;
        double overal_fan4_speed = 0.0;
        double overal_fan5_speed = 0.0;
        double overal_fan6_speed = 0.0;

        double avg_CPU_temp = 0.0;
        double avg_CPU_MHz = 0.0;
        double avg_CPU_Load = 0.0;
        double avg_fan1_speed = 0.0;
        double avg_fan2_speed = 0.0;
        double avg_fan3_speed = 0.0;
        double avg_fan4_speed = 0.0;
        double avg_fan5_speed = 0.0;
        double avg_fan6_speed = 0.0;       

        //Control Arrays       
        List<TextBox> CPU_array = new List<TextBox>();
        List<TextBox> Fan_array = new List<TextBox>();
        List<NumericUpDownLib.UIntegerUpDown> Fan_Numeric_Boxes = new List<NumericUpDownLib.UIntegerUpDown>();
        List<Slider> Fan_Slider = new List<Slider>();
        List<ComboBox> Fan_Mode_Controls = new List<ComboBox>();

        //Give time to form to load properly timer
        System.Windows.Threading.DispatcherTimer oneShot = new System.Windows.Threading.DispatcherTimer();

        //External bench tachometer (USB-HID, VID 0x1A86 / PID 0xE008). Connected on demand from the
        //Tachometer panel. When assigned to a fan channel it overrides that fan's displayed RPM with
        //the tach reading; when the tach signal is stale/lost it falls back to the Commander PRO value.
        HidTachometer bench_tach;
        volatile int tachAssignedChannel = -1;   // -1 = None; 0..5 = Fan #1..#6

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

            //Read CPU Data
            CpuDataTimer.Tick += new EventHandler(CpuDataTimer_Tick);
            CpuDataTimer.Interval = 500; // specify interval time

            //Fan RPMs are now polled on a background task (StartFanPolling), not a UI timer.

            //Give time to form to load properly timer
            oneShot.Interval = new TimeSpan(0, 0, 0, 1, 0);
            oneShot.Tick += new EventHandler(OneShot_Tick);    

            //timer for min-max-avg-values
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

            //Fills the Control Lists
            oneShot.Start();

            if (Properties.Settings.Default.AutoStart1)
            { autostartCheckBox.IsChecked = true; }

            if (Properties.Settings.Default.AVG_Values)
            { AVG_values.IsChecked = true; }

            StartRemoteControlIfRequested();

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
            CloseHidStream();

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

        //finds all controls
        public static IEnumerable<T> FindLogicalChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj != null)
            {
                foreach (object rawChild in LogicalTreeHelper.GetChildren(depObj))
                {
                    if (rawChild is DependencyObject)
                    {
                        DependencyObject child = (DependencyObject)rawChild;
                        if (child is T)
                        {
                            yield return (T)child;
                        }

                        foreach (T childOfChild in FindLogicalChildren<T>(child))
                        {
                            yield return childOfChild;
                        }
                    }
                }
            }
        }

        private void Set_Min_Max_AVG_timer_Tick(object sender, EventArgs e)
        {
            Set_min_max(0, 1, 2, 1);
            Set_min_max(3, 4, 5, 1);
            Set_min_max(6, 7, 8, 1);
            Set_min_max(0, 1, 2, 2);
            Set_min_max(3, 4, 5, 2);
            Set_min_max(6, 7, 8, 2);
            Set_min_max(9, 10, 11, 2);
            Set_min_max(12, 13, 14, 2);
            Set_min_max(15, 16, 17, 2);

            //feed the dedicated fan Average column (always live, independent of the AVG checkbox)
            Set_Fan_Average_Column();

            //keep the bench-tachometer readout honest about stale/lost signal
            Update_Tach_Panel();
        }

        //Shows the running average of each fan in its own column (ed28..ed33)
        private void Set_Fan_Average_Column()
        {
            ed28.Text = Math.Round(avg_fan1_speed).ToString();
            ed29.Text = Math.Round(avg_fan2_speed).ToString();
            ed30.Text = Math.Round(avg_fan3_speed).ToString();
            ed31.Text = Math.Round(avg_fan4_speed).ToString();
            ed32.Text = Math.Round(avg_fan5_speed).ToString();
            ed33.Text = Math.Round(avg_fan6_speed).ToString();
        }

        //Give time to form to properly load timer
        void OneShot_Tick(object sender, EventArgs e)
        {
            oneShot.Stop();

            foreach (TextBox tb in FindLogicalChildren<TextBox>(CPU_Grid))
            {
                CPU_array.Add(tb);
            }

            foreach (TextBox tb in FindLogicalChildren<TextBox>(Fan_Grid))
            {
                Fan_array.Add(tb);
            }

            foreach (NumericUpDownLib.UIntegerUpDown tb in FindLogicalChildren<NumericUpDownLib.UIntegerUpDown>(Fans_Grid))
            {
                Fan_Numeric_Boxes.Add(tb);
            }

            foreach (Slider tb in FindLogicalChildren<Slider>(Fans_Grid))
            {
                Fan_Slider.Add(tb);
            }

            foreach (ComboBox tb in FindLogicalChildren<ComboBox>(Fans_Grid))
            {
                Fan_Mode_Controls.Add(tb);
            }
                          
        }

        //Generic Functions      

        #region Commander Pro Functions
        // ---- Background fan-RPM polling --------------------------------------------------
        // Fan speeds used to be read on a WinForms (UI-thread) timer, so a slow or stalled HID
        // transfer froze the whole window. We now poll on a background task, serialize every
        // HID access through hidLock, and marshal only the final RPM values onto the UI thread.

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
                            string fan_mask = ReadFanMaskLocked();   //e.g. "011000"

                            for (int ch = 0; (ch < 6) && (ch < fan_mask.Length); ch++)
                            {
                                token.ThrowIfCancellationRequested();

                                char y = fan_mask[ch];
                                //'1' = 3-pin, '2' = 4-pin => active; anything else => inactive
                                rpms[ch] = ((y == '1') || (y == '2')) ? ReadFanRpmLocked(ch) : 0;
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
        //Inactive/disconnected channels are cleared to "0" so stale RPMs never linger.
        private void UpdateFanRpmUi(int[] rpms)
        {
            if (rpms == null) return;

            for (int ch = 0; ch < 6; ch++)
            {
                int idx = ch * 3;                     // channel -> "Current" index in Fan_array (0,3,6,9,12,15)
                if (idx >= Fan_array.Count) return;   // controls not collected yet
                Fan_array[idx].Text = rpms[ch].ToString();
            }
        }

        //Locked HID read of the fan mask (which channels are populated). Background-thread safe.
        private string ReadFanMaskLocked()
        {
            string fan_mask = "";
            lock (hidLock)
            {
                HidSharp.HidStream s = stream;
                if (s == null) return "000000";

                byte[] o = new byte[64];
                byte[] i = new byte[16];
                o[1] = (byte)CorsairLightingProtocolConstants.READ_FAN_MASK;
                s.Write(o);
                s.Read(i);

                for (int k = 2; k < 8; k++) { fan_mask = fan_mask + i[k].ToString(); }
            }
            return (fan_mask.Length == 6) ? fan_mask : "000000";
        }

        //Locked HID read of a single fan's RPM. Background-thread safe.
        private int ReadFanRpmLocked(int channel)
        {
            lock (hidLock)
            {
                HidSharp.HidStream s = stream;
                if (s == null) return 0;

                byte[] o = new byte[64];
                byte[] i = new byte[16];
                o[1] = (byte)CorsairLightingProtocolConstants.READ_FAN_SPEED;
                o[2] = (byte)channel;
                s.Write(o);
                s.Read(i);

                return (i[2] << 8) + i[3];
            }
        }

        //Close/dispose the HID stream. Nulling the field first makes any in-flight locked read
        //bail out; closing the captured stream interrupts a read that is currently blocking.
        private void CloseHidStream()
        {
            HidSharp.HidStream local = stream;
            stream = null;
            if (local == null) return;
            try { local.Close(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: HID stream close failed: " + ex.Message); }
            try { local.Dispose(); } catch { }
        }

        //Single safe teardown for the Commander Pro connection + UI reset. MUST run on the UI
        //thread. Shared by manual disconnect, connect-failure cleanup and the automatic
        //disconnect that fires after repeated poll failures. Idempotent and null-safe.
        private void DisconnectCommanderPro(string statusText, System.Windows.Media.Brush statusBrush)
        {
            StopRpmHold("Commander disconnected");   //the loop has no actuator without the device
            Corsair_Commander_Connected = false;
            StopFanPolling();   //cancellation only - never waits on the poll task
            CloseHidStream();   //nulls + closes the stream, interrupting any blocked read

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

        private string Commander_Pro_READ_FAN_MASK()
        {
            string fan_mask = "";

            if (Corsair_Commander_Connected == true)
            {
                lock (hidLock)
                {
                    if (stream == null) return "000000";

                    //clear the output buffer
                    for (int i = 0; i < 63; ++i)
                    {
                        outbuf[i] = 0x00;
                    }

                    // Read Fan Mode
                    outbuf[1] = CorsairLightingProtocolConstants.READ_FAN_MASK;

                    // Send the command
                    stream.Write(outbuf);

                    stream.Read(inbuf);

                    for (int i = 2; i < 8; ++i)
                    {
                        fan_mask = fan_mask + inbuf[i].ToString();
                    }
                }
            }
            if (fan_mask.Length == 6) return fan_mask;
            else return "000000";
        }

        private void Commander_Pro_READ_FAN_MODEs()
        {
            string fan_mask = Commander_Pro_READ_FAN_MASK(); //px. 011000

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

        private int Commander_Pro_READ_FAN_Power(byte fan_number)
        {
            int fan_power = 0;

            if (Corsair_Commander_Connected == true)
            {
                lock (hidLock)
                {
                    if (stream == null) return 0;

                    //clear the output buffer
                    for (int i = 0; i < 64; ++i)
                    {
                        outbuf[i] = 0x00;
                    }

                    // Read Fan Mode
                    outbuf[1] = CorsairLightingProtocolConstants.READ_FAN_POWER;
                    outbuf[2] = fan_number;

                    // Send the command
                    stream.Write(outbuf);

                    stream.Read(inbuf);

                    if (inbuf[2] <= 100)
                    {
                        fan_power = inbuf[2];
                    }
                }
            }

            return fan_power;
        }

        private int Commander_Pro_READ_FAN_Speed(byte fan_number)
        {
            int fan_speed = 0;

            if (Corsair_Commander_Connected == true)
            {
                lock (hidLock)
                {
                    if (stream == null) return 0;

                    //clear the output buffer
                    for (int i = 0; i < 64; ++i)
                    {
                        outbuf[i] = 0x00;
                    }

                    // Read Fan Mode
                    outbuf[1] = CorsairLightingProtocolConstants.READ_FAN_SPEED;
                    outbuf[2] = fan_number;

                    // Send the command
                    stream.Write(outbuf);

                    stream.Read(inbuf);
                    fan_speed = (inbuf[2] << 8) + inbuf[3];
                }
            }

            return fan_speed;
        }

        //Set the fan mode
        private void Commander_Pro_Set_Fan_Connection_Mode(object sender, SelectionChangedEventArgs e)
        {
            if (Corsair_Commander_Connected == true)
            {
                String nam = ((ComboBox)sender).Name;
                byte selected_fan = 0;

                for (int i = 0; i < 6; ++i)
                {
                    if (Fan_Mode_Controls[i].Name == nam)
                    {
                        selected_fan = (byte)i;
                        break;
                    }
                }

                lock (hidLock)
                {
                    if (stream == null) return;

                    //clear the output buffer
                    for (int i = 0; i < 64; ++i)
                    {
                        outbuf[i] = 0x00;
                    }

                    outbuf[1] = CorsairLightingProtocolConstants.WRITE_FAN_DETECTION_TYPE;
                    outbuf[2] = 0x02;
                    outbuf[3] = selected_fan;

                    switch (Fan_Mode_Controls[selected_fan].SelectedIndex)
                    {
                        case 0:
                            outbuf[4] = (byte)FanDetectionType.Auto;
                            break;
                        case 1:
                            outbuf[4] = (byte)FanDetectionType.ThreePin;
                            break;
                        case 2:
                            outbuf[4] = (byte)FanDetectionType.FourPin;
                            break;
                        case 3:
                            outbuf[4] = (byte)FanDetectionType.Disconnected;
                            break;
                        default:
                            outbuf[4] = (byte)FanDetectionType.Auto;
                            break;
                    }

                    // Send the command
                    stream.Write(outbuf);
                    stream.Read(inbuf);
                    LogHidExchange("WRITE_FAN_DETECTION_TYPE fan=" + (selected_fan + 1) +
                                   " mode=" + Fan_Mode_Controls[selected_fan].SelectedIndex, 5);
                }
            }
        }

        //Set The Fan Speed
        private void Commander_Pro_Set_Fan_Speed(int fan_channel, int fan_speed)
        {
            if (Corsair_Commander_Connected == true)
            {
                lock (hidLock)
                {
                    if (stream == null) return;

                    //clear the output buffer
                    for (int i = 0; i < 64; ++i)
                    {
                        outbuf[i] = 0x00;
                    }

                    outbuf[1] = CorsairLightingProtocolConstants.WRITE_FAN_SPEED;
                    outbuf[2] = (byte)fan_channel;
                    outbuf[3] = (byte)(fan_speed >> 8);  //convert fan speed to big endian
                    outbuf[4] = (byte)(fan_speed & 0xff); //convert fan speed to big endian

                    // Send the command
                    stream.Write(outbuf);
                    stream.Read(inbuf);
                    LogHidExchange("WRITE_FAN_SPEED fan=" + (fan_channel + 1) + " rpm=" + fan_speed, 5);
                }
            }
        }

        //Trace an HID command and the device's reply. The Commander PRO answers with a status byte
        //(0x00 OK / 0x01 error) that pCUE historically ignored, so a rejected command looked
        //identical to a successful one - e.g. an RPM target on a 3-pin channel, which the firmware
        //does not support. Logging it is what makes that visible.
        private void LogHidExchange(string what, int outBytes)
        {
            byte status = inbuf.Length > 0 ? inbuf[0] : (byte)0xFF;
            bool ok = status == CorsairLightingProtocolConstants.PROTOCOL_RESPONSE_OK;
            string text = what
                        + "  ->  " + AppLog.Hex(outbuf, outBytes)
                        + "  <-  " + AppLog.Hex(inbuf, 6)
                        + (ok ? "  [OK]" : "  [DEVICE REPORTED 0x" + status.ToString("x2") + "]");
            if (ok) AppLog.Debug(text); else AppLog.Warn(text);
        }

        //Set The Fan Power
        private void Commander_Pro_Set_Fan_Power(int fan_channel, int fan_power)
        {
            if (Corsair_Commander_Connected == true)
            {
                lock (hidLock)
                {
                    if (stream == null) return;

                    //clear the output buffer
                    for (int i = 0; i < 64; ++i)
                    {
                        outbuf[i] = 0x00;
                    }

                    outbuf[1] = CorsairLightingProtocolConstants.WRITE_FAN_POWER;
                    outbuf[2] = (byte)fan_channel;
                    outbuf[3] = (byte)(fan_power);

                    // Send the command
                    stream.Write(outbuf);
                    stream.Read(inbuf);
                    LogHidExchange("WRITE_FAN_POWER fan=" + (fan_channel + 1) + " duty=" + fan_power + "%", 4);
                }
            }
        }

        private void Open_Corsair_Commander_Click(object sender, RoutedEventArgs e)
        {
             string firmware = "";

             if (Open_Corsair_Commander.Content.ToString() == "Open")
            {
                try
                {
                    //kill iCUE services because it messes with the readings
                    Kill_iCUE_Function();

                    //brisko to commander pro kai to anoigo
                    device = Commander_Loader.GetDevices(0x1b1c, 0x0c10, null, null).First();

                    //to brike kai to anoikse
                    if (device.GetProductName() == "Commander PRO")
                    {
                        Open_Corsair_Commander.Content = "Close";
                        Corsair_Commander_Connected = true;                     

                        device.TryOpen(out stream);                    
                        
                        //Bound any blocking HID transfer so a stalled device cannot hang the
                        //background poll loop (or a UI command waiting on hidLock) indefinitely.
                        if (stream != null)
                        {
                            stream.ReadTimeout = 1000;
                            stream.WriteTimeout = 1000;
                        }

                        int i = 0;

                        //clear the output buffer
                        for (i = 0; i < 64; ++i)
                        {
                            outbuf[i] = 0x00;
                        }                    

                        // Get firmware version
                        outbuf[1] = CorsairLightingProtocolConstants.READ_FIRMWARE_VERSION;
                        
                        // Send the command
                        stream.Write(outbuf);                      

                        //Read the response
                        stream.Read(inbuf);

                        for (i = 2; i < 5; ++i)
                        {                            
                            //memo1.AppendText(inbuf[i].ToString());
                            if (i>2) {firmware = firmware +"." + inbuf[i];}
                            else { firmware = firmware + inbuf[i]; }
                        }

                        Commander_SN.Text = firmware;

                        Commander_Pro_READ_FAN_MODEs();                       
                        
                        //substitute for the above function
                        //show speed at first
                        for (i = 0; i < 6; ++i)
                        {
                            uint rpm = (uint)Commander_Pro_READ_FAN_Speed((byte)i);
                            Fan_Numeric_Boxes[i].Value = rpm;
                            Fan_Slider[i].Value = rpm; 
                            //Fan_Numeric_Boxes[i].Value = (uint)commander_Pro_READ_FAN_Power((byte)i);                          
                        }
                    
                        //Thread.Sleep(100);
                    
                        //Fan_Power_Mode.IsEnabled = true;
                        //Fan_Speed_Mode.IsEnabled = true;
                        //start polling the fans on a background task
                        StartFanPolling();
                        SetStatus("● Connected", System.Windows.Media.Brushes.Lime);
                                      
                    }

                    else if (device.GetProductName() != "Commander PRO")
                    {
                        //await Task.Delay(100);
                        MessageBox.Show("Cannot open Commander Pro!");
                        SetStatus("● Wrong device", System.Windows.Media.Brushes.Orange);
                    }
                        
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: CPU sensor read failed: " + ex.Message);
            }
        }
        #endregion

        #region App Kill functions
        private static bool IsProcessOpen(string name)
        {
            foreach (Process clsProcess in Process.GetProcesses())
            {
                if (clsProcess.ProcessName.Contains(name))
                {
                    return true;
                }
            }
            return false;
        }

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
                        //ForceKill(pr);
                    }
                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString());
            }
        }
    

        private void Kill_Function(string App)
        {           
            Process[] processes = Process.GetProcessesByName(App);
            foreach (var process in processes)
            {
                //process.Kill();
                ForceKill(process);
                //break;
            }
        }

        public static void ForceKill(Process proc)
        {

            // Accessing ProcessName could throw an exception if the process has already been killed.
            string processName = string.Empty;
            try { processName = proc.ProcessName; } catch (Exception ex) { }

            // ProcessId can be accessed after the process has been killed but we'll do this safely anyways.
            int pId = 0;
            try { pId = proc.Id; } catch (Exception ex) { }

            // Will only work if started by this instance of the dll.
            try { proc.Kill(); } catch (Exception ex) { }

            // Fallback to task kill
            if (pId > 0)
            {
                var taskKilPsi = new ProcessStartInfo("taskkill");
                taskKilPsi.Arguments = $"/pid {proc.Id} /T /F";
                taskKilPsi.WindowStyle = ProcessWindowStyle.Hidden;
                taskKilPsi.UseShellExecute = false;
                taskKilPsi.RedirectStandardOutput = true;
                taskKilPsi.RedirectStandardError = true;
                taskKilPsi.CreateNoWindow = true;
                var taskKillProc = Process.Start(taskKilPsi);
                taskKillProc.WaitForExit();
                String taskKillOutput = taskKillProc.StandardOutput.ReadToEnd(); // Contains success
                String taskKillErrorOutput = taskKillProc.StandardError.ReadToEnd();
            }

            // Fallback to wmic delete process.
            if (!string.IsNullOrEmpty(processName))
            {
                // https://stackoverflow.com/a/38757852/591285
                var wmicPsi = new ProcessStartInfo("wmic");
                wmicPsi.Arguments = $@"process where ""name='{processName}.exe'"" delete";
                wmicPsi.WindowStyle = ProcessWindowStyle.Hidden;
                wmicPsi.UseShellExecute = false;
                wmicPsi.RedirectStandardOutput = true;
                wmicPsi.RedirectStandardError = true;
                wmicPsi.CreateNoWindow = true;
                var wmicProc = Process.Start(wmicPsi);
                wmicProc.WaitForExit();
                String wmicOutput = wmicProc.StandardOutput.ReadToEnd(); // Contains success
                String wmicErrorOutput = wmicProc.StandardError.ReadToEnd();
            }

        }

        #endregion

        //initialize all counters and AVG/Overall values
        public void Initialize_all_values()
        {
            counter_min_max_avg = 0;

            //initialize all counters and AVG/Overall values
            CPU_temp_counter_min_max_avg = 0;
            CPU_MHz_counter_min_max_avg = 0;
            CPU_Load_counter_min_max_avg = 0;
            avg_fan1_counter_min_max_avg = 0;
            avg_fan2_counter_min_max_avg = 0;
            avg_fan3_counter_min_max_avg = 0;
            avg_fan4_counter_min_max_avg = 0;
            avg_fan5_counter_min_max_avg = 0;
            avg_fan6_counter_min_max_avg = 0;

            avg_CPU_temp = 0.0;
            avg_CPU_MHz = 0.0;
            avg_CPU_Load = 0.0;
            avg_fan1_speed = 0.0;
            avg_fan2_speed = 0.0;
            avg_fan3_speed = 0.0;
            avg_fan4_speed = 0.0;
            avg_fan5_speed = 0.0;
            avg_fan6_speed = 0.0;

            overal_CPU_temp = 0.0;
            overal_CPU_MHz = 0.0;
            overal_CPU_Load = 0.0;
            overal_fan1_speed = 0.0;
            overal_fan2_speed = 0.0;
            overal_fan3_speed = 0.0;
            overal_fan4_speed = 0.0;
            overal_fan5_speed = 0.0;
            overal_fan6_speed = 0.0;
        }

        public void Set_min_max(int current, int min, int max, int Grid)
        {                   

            //List to use depending on the Grid
            List<TextBox> Sample_array = new List<TextBox>();

            // with 500 samples per sec this is 27.8 hours
            if (counter_min_max_avg >= 100000)
            {
                Initialize_all_values();
            }

            // Select the Grid that I will have as input
            if (Grid == 1) { Sample_array = CPU_array; }
            else if (Grid == 2) { Sample_array = Fan_array; }

            try
            {

                if ((Sample_array[current].Text != null) && (Sample_array[min].Text != null) && (Sample_array[max].Text != null))
                {

                    if (Convert.ToDouble(Sample_array[current].Text) > 0)
                    {
                        counter_min_max_avg += 1;

                        if ((current == 0) && (Grid ==1))
                        {
                           if (Convert.ToDouble(Sample_array[current].Text)>0)
                           { 
                            CPU_temp_counter_min_max_avg += 1;
                            overal_CPU_temp += Convert.ToDouble(Sample_array[current].Text);
                            avg_CPU_temp = overal_CPU_temp / CPU_temp_counter_min_max_avg;
                           }
                        }                        

                        else if ((current == 3) && (Grid == 1))
                        {
                            if (Convert.ToDouble(Sample_array[current].Text) > 0)
                            { 
                            CPU_MHz_counter_min_max_avg += 1;
                            overal_CPU_MHz += Convert.ToDouble(Sample_array[current].Text);
                            avg_CPU_MHz = overal_CPU_MHz / CPU_MHz_counter_min_max_avg;
                            }
                        }

                        else if ((current == 6) && (Grid == 1))
                        {
                            if (Convert.ToDouble(Sample_array[current].Text) > 0)
                            {
                            CPU_Load_counter_min_max_avg += 1;
                            overal_CPU_Load += Convert.ToDouble(Sample_array[current].Text);
                            avg_CPU_Load = overal_CPU_Load / CPU_Load_counter_min_max_avg;
                            }
                        }

                        else if ((current == 0) && (Grid == 2))
                        {
                            if (Convert.ToDouble(Sample_array[current].Text) > 0)
                            {
                                avg_fan1_counter_min_max_avg += 1;
                                overal_fan1_speed += Convert.ToDouble(Sample_array[current].Text);
                                avg_fan1_speed = overal_fan1_speed / avg_fan1_counter_min_max_avg;
                            }
                        }

                        else if ((current == 3) && (Grid == 2))
                        {
                            if (Convert.ToDouble(Sample_array[current].Text) > 0)
                            {
                                avg_fan2_counter_min_max_avg += 1;
                                overal_fan2_speed += Convert.ToDouble(Sample_array[current].Text);
                                avg_fan2_speed = overal_fan2_speed / avg_fan2_counter_min_max_avg;
                            }
                        }

                        else if ((current == 6) && (Grid == 2))
                        {
                            if (Convert.ToDouble(Sample_array[current].Text) > 0)
                            {
                                avg_fan3_counter_min_max_avg += 1;
                                overal_fan3_speed += Convert.ToDouble(Sample_array[current].Text);
                                avg_fan3_speed = overal_fan3_speed / avg_fan3_counter_min_max_avg;
                            }
                        }

                        else if ((current == 9) && (Grid == 2))
                        {
                            if (Convert.ToDouble(Sample_array[current].Text) > 0)
                            {
                                avg_fan4_counter_min_max_avg += 1;
                                overal_fan4_speed += Convert.ToDouble(Sample_array[current].Text);
                                avg_fan4_speed = overal_fan4_speed / avg_fan4_counter_min_max_avg;
                            }
                        }

                        else if ((current == 12) && (Grid == 2))
                        {
                            if (Convert.ToDouble(Sample_array[current].Text) > 0)
                            {
                                avg_fan5_counter_min_max_avg += 1;
                                overal_fan5_speed += Convert.ToDouble(Sample_array[current].Text);
                                avg_fan5_speed = overal_fan5_speed / avg_fan5_counter_min_max_avg;
                            }
                        }

                        else if ((current == 15) && (Grid == 2))
                        {
                            if (Convert.ToDouble(Sample_array[current].Text) > 0)
                            {
                                avg_fan6_counter_min_max_avg += 1;
                                overal_fan6_speed += Convert.ToDouble(Sample_array[current].Text);
                                avg_fan6_speed = overal_fan6_speed / avg_fan6_counter_min_max_avg;
                            }
                        }
                    }

                    // Min column:
                    //  - Fans (Grid 2) always keep the real Min; their running Average has its own column now.
                    //  - CPU (Grid 1) shows the real Min, or the running Average when the "Average Values" box is ticked.
                    //
                    // Only a reading > 0 may move Min. A 0 means "no sample" here (an unpopulated
                    // Commander channel, or a bench tachometer whose signal went stale), and this
                    // block already treats 0 in the Min box as "unset" - so without this guard a
                    // single 0 would overwrite an established minimum and then be re-seeded from
                    // the next reading, permanently losing the real minimum for the run.
                    if (((Grid == 2) || (AVG_values.IsChecked == false))
                        && (Convert.ToDouble(Sample_array[current].Text) > 0))
                    {
                        if (Convert.ToDouble(Sample_array[min].Text) == 0)
                        {
                            Sample_array[min].Text = Sample_array[current].Text;
                        }

                        else if (Convert.ToDouble(Sample_array[current].Text) < Convert.ToDouble(Sample_array[min].Text))
                        {
                            Sample_array[min].Text = Sample_array[current].Text;
                        }
                    }

                    // CPU + "Average Values" ticked -> show the CPU average in the Min column
                    else
                    {
                        switch (current)
                        {
                            case 0:
                                Sample_array[min].Text = avg_CPU_temp.ToString("0.#");
                                break;
                            case 3:
                                Sample_array[min].Text = avg_CPU_MHz.ToString("0.#");
                                break;
                            case 6:
                                Sample_array[min].Text = avg_CPU_Load.ToString("0.#");
                                break;
                        }
                    }

                      if (Convert.ToDouble(Sample_array[current].Text) > Convert.ToDouble(Sample_array[max].Text))
                    {
                        Sample_array[max].Text = Sample_array[current].Text;
                    }
                  
                }
            }

            catch 
            { 
                //Not implemented
            }
        }

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
        }       

        private void Set_Fan_Speed_Click(object sender, RoutedEventArgs e)
        {
            //A manual Set Speed is the user taking over; a running hold loop would immediately
            //fight it back to its own duty.
            StopRpmHold("manual Set Speed");

            for (int i = 0; i <= 5; i++)
            {
                Set_Fan_Speed_Function_Commander_Pro(i);
            }
        }

        //With this function I am able to set the fans separately, either with speed or power
        private void Set_Fan_Speed_Function_Commander_Pro(int fan)
        {

            int fan_speed = 0;

           fan_speed = (int)Fan_Numeric_Boxes[fan].Value; 
           
            if (fan_speed <= 100) //Gia to Power
                {                            
                    Commander_Pro_Set_Fan_Power(fan, fan_speed);
                }

                else if (fan_speed > 100) //Gia to Speed
                {
                    Commander_Pro_Set_Fan_Speed(fan, fan_speed);                 
                }                          
        }      

        #region Remote control API (IRemoteControlTarget)
        //Every member here can be called from an HTTP worker thread, so anything that touches WPF
        //or shared UI state is marshalled onto the UI thread with Dispatcher.Invoke. The HID calls
        //themselves are already serialized by hidLock and are safe from any thread.

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
                    if (ch < Fan_Mode_Controls.Count)
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
                        setpoint = ch < Fan_Numeric_Boxes.Count ? (int)Fan_Numeric_Boxes[ch].Value : 0,
                    });
                }

                double? tachRpm = bench_tach != null && bench_tach.IsConnected ? bench_tach.ReadRpm() : null;

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
                        temperature = CPU_array.Count > 0 ? CPU_array[0].Text : null,
                        mhz = CPU_array.Count > 3 ? CPU_array[3].Text : null,
                        load = CPU_array.Count > 6 ? CPU_array[6].Text : null,
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
                        duty = rpmHold != null ? rpmHold.CurrentDuty : 0,
                        target = (int)Hold_Target_Numeric.Value,
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
            Commander_Pro_Set_Fan_Power(channel, duty);
            return null;
        }

        public string SetFanRpm(int fan, int rpm)
        {
            if (!TryChannel(fan, out int channel, out string error)) return error;
            if (rpm <= 100 || rpm > 3500) return "value must be 101-3500 RPM (<=100 would be read as a percent).";
            if (!Corsair_Commander_Connected) return "Commander PRO is not connected.";

            Dispatcher.Invoke(new Action(delegate { StopRpmHold("remote rpm command"); }));
            Commander_Pro_Set_Fan_Speed(channel, rpm);
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
                Hold_Fan_Select.SelectedIndex = fan;          // index 0 is "None"
                Hold_Target_Numeric.Value = (uint)rpm;
                Hold_Start_Button_Click(this, null);
                //The click handler reports its own reason (no feedback, not connected, ...).
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

            if ((v = get("target")).HasValue)
            {
                holdConfig.TargetRpm = v.Value;
                Dispatcher.Invoke(new Action(delegate { Hold_Target_Numeric.Value = (uint)v.Value; }));
                if (rpmHold != null && rpmHold.IsRunning) rpmHold.UpdateTarget(v.Value);
            }

            if (holdConfig.MinDuty < 0) holdConfig.MinDuty = 0;
            if (holdConfig.MaxDuty > 100) holdConfig.MaxDuty = 100;
            if (holdConfig.RpmTolerance <= 0) holdConfig.RpmTolerance = 25;

            AppLog.Info("Hold config updated via remote API.");
            return null;
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

        private void Hold_Start_Button_Click(object sender, RoutedEventArgs e)
        {
            if (rpmHold != null && rpmHold.IsRunning)
            {
                rpmHold.Stop();                  //status/caption update when the loop exits
                return;
            }

            int sel = Hold_Fan_Select.SelectedIndex;
            if (sel < 1 || sel > 6)
            {
                SetHoldStatus("Pick a fan to hold first.", UpdateAlertBrush);
                return;
            }
            if (!Corsair_Commander_Connected)
            {
                SetHoldStatus("Open the Commander PRO first.", UpdateAlertBrush);
                return;
            }

            holdChannel = sel - 1;

            if (ReadHeldFanRpm() == null)
            {
                SetHoldStatus("No RPM feedback for Fan #" + sel +
                              " - assign the tachometer to it, or check its tach wire.", UpdateAlertBrush);
                holdChannel = -1;
                return;
            }

            holdConfig.TargetRpm = Hold_Target_Numeric.Value;
            var cfg = holdConfig;

            rpmHold = new FanRpmHoldController(
                duty => Commander_Pro_Set_Fan_Power(holdChannel, duty),
                ReadHeldFanRpm,
                () => Corsair_Commander_Connected);

            rpmHold.SnapshotUpdated += Rpm_Hold_SnapshotUpdated;
            rpmHold.StatusChanged += Rpm_Hold_StatusChanged;

            try
            {
                rpmHold.StartAsync(cfg);
                Hold_Start_Button.Content = "Stop Hold";
                SetHoldStatus("Starting...", UpdateInfoBrush);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: could not start RPM hold: " + ex.Message);
                SetHoldStatus("Could not start: " + ex.Message, UpdateAlertBrush);
            }
        }

        //Live setpoint change while the loop runs - no restart needed.
        private void Hold_Target_ValueChanged(object sender, RoutedPropertyChangedEventArgs<uint> e)
        {
            if (rpmHold != null && rpmHold.IsRunning) rpmHold.UpdateTarget(Hold_Target_Numeric.Value);
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
            if (status != FanHoldStatus.Fault && status != FanHoldStatus.Stopped) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate { Hold_Start_Button.Content = "Hold RPM"; }));
            }
            catch (Exception ex) { Debug.WriteLine("pCUE: hold status dispatch failed: " + ex.Message); }
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
                return;
            }

            double? rpm = bench_tach.ReadRpm();
            if (rpm.HasValue)
            {
                Tach_RPM_Readout.Text = Math.Round(rpm.Value).ToString();
                Tach_RPM_Readout.Foreground = UpdateInfoBrush;
                Tach_Battery_Label.Visibility = bench_tach.BatteryLow ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                //Connected but no fresh frame - say so instead of showing a stale number.
                Tach_RPM_Readout.Text = "no signal";
                Tach_RPM_Readout.Foreground = UpdateAlertBrush;
                Tach_Battery_Label.Visibility = Visibility.Collapsed;
            }
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
            isinstartup = add;
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


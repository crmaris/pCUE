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
    //Walks the LibreHardwareMonitor tree and refreshes every hardware node + its sub-hardware.
    //Required before reading sensor values: LibreHardwareMonitor only updates on Accept/Update.
    public class UpdateVisitor : IVisitor
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

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
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

        //CPU temp/clock/load now come from LibreHardwareMonitor in-process (CpuSensorTimer below).

        //fan constants
        public const int FAN_FORCE_THREE_PIN_MODE_ON = 0x01;
        public const int FAN_FORCE_THREE_PIN_MODE_OFF = 0x00;
        public const int FAN_CURVE_POINTS_NUM = 6;
        public const int FAN_CURVE_TEMP_GROUP_EXTERNAL = 255;

        //LibreHardwareMonitor sensor tree (CPU temp/clock/load)
        Computer thisComputer;


        //Timer for CPU Data
        static System.Windows.Forms.Timer CpuSensorTimer = new System.Windows.Forms.Timer();

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

        public MainWindow()
        {
            InitializeComponent();

            //Read CPU Data
            CpuSensorTimer.Tick += new EventHandler(CpuSensorTimer_Tick);
            CpuSensorTimer.Interval = 500; // specify interval time

            //Fan RPMs are now polled on a background task (StartFanPolling), not a UI timer.

            //Give time to form to load properly timer
            oneShot.Interval = new TimeSpan(0, 0, 0, 1, 0);
            oneShot.Tick += new EventHandler(OneShot_Tick);    

            //timer for min-max-avg-values
            Set_Min_Max_AVG_timer.Tick += new EventHandler(Set_Min_Max_AVG_timer_Tick);
            Set_Min_Max_AVG_timer.Interval = 500; // specify interval time as you want  
            Set_Min_Max_AVG_timer.Start();
 
            // Open LibreHardwareMonitor; only the CPU is needed for temp/clock/load.
            thisComputer = new Computer() { IsCpuEnabled = true };
            thisComputer.Open();
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

        }

        private void Window_Closed(object sender, EventArgs e)
        {
            CpuSensorTimer.Stop();

            //stop background fan polling and release the HID stream
            Corsair_Commander_Connected = false;
            StopFanPolling();
            CloseHidStream();

            //release the LibreHardwareMonitor sensor tree
            try { thisComputer.Close(); } catch (Exception ex) { Debug.WriteLine("pCUE: computer close failed: " + ex.Message); }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show("Really close?", "Warning", MessageBoxButton.YesNo);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }

            else
            {
                CpuSensorTimer.Stop();
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
                }
            }
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

        #region CPU sensors (LibreHardwareMonitor)
        //Refreshes the LibreHardwareMonitor tree once per pass before sensors are read.
        private readonly UpdateVisitor _updateVisitor = new UpdateVisitor();

        //Replaces the old Core Temp shared-memory read. Pulls CPU temperature, clock and
        //load straight out of LibreHardwareMonitor's in-process sensors - no external exe.
        //The displayed values mirror the previous behaviour: a whole-CPU temperature, the
        //average core clock (MHz) and total CPU load (%). Number formatting is left at the
        //current culture, exactly as before, so Set_min_max's Convert.ToDouble round-trips.
        private void CpuSensorTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                //Update every node first; LibreHardwareMonitor sensor Values are null until then.
                thisComputer.Accept(_updateVisitor);

                double? temperature = null;   //a whole-CPU temperature (package / Tctl / average)
                double? load = null;          //CPU total load %
                double coreTempSum = 0; int coreTempCount = 0;     //fallback: average per-core temps
                double coreClockSum = 0; int coreClockCount = 0;   //average per-core clocks

                foreach (IHardware hw in thisComputer.Hardware)
                {
                    if (hw.HardwareType != HardwareType.Cpu) continue;

                    foreach (ISensor s in hw.Sensors)
                    {
                        if (s.Value == null) continue;
                        double v = s.Value.Value;

                        switch (s.SensorType)
                        {
                            case SensorType.Temperature:
                                //Prefer a single whole-CPU reading; else average the per-core sensors.
                                if (s.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    s.Name.IndexOf("Average", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    s.Name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    temperature = v;
                                }
                                else if (s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    coreTempSum += v; coreTempCount++;
                                }
                                break;

                            case SensorType.Load:
                                if (s.Name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0)
                                    load = v;
                                break;

                            case SensorType.Clock:
                                //Average the per-core clocks; ignore the bus/reference clock.
                                if (s.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                    s.Name.IndexOf("Bus", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    coreClockSum += v; coreClockCount++;
                                }
                                break;
                        }
                    }

                    break;   //first CPU package only, matching the old single-CPU display
                }

                if (temperature == null && coreTempCount > 0) temperature = coreTempSum / coreTempCount;
                double? clock = (coreClockCount > 0) ? (double?)(coreClockSum / coreClockCount) : null;

                if (temperature != null) CPU_array[0].Text = temperature.Value.ToString("0.0");
                if (clock != null)       CPU_array[3].Text = clock.Value.ToString("N1");
                if (load != null)        CPU_array[6].Text = load.Value.ToString("N1");
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
                    if ((Grid == 2) || (AVG_values.IsChecked == false))
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
            //CPU temp/clock/load now come from LibreHardwareMonitor in-process; no external exe.
            if (Start_CPU_data.Content.ToString() == "Start")
            {
                Start_CPU_data.Content = "Stop";
                CpuSensorTimer.Start();
            }
            else if (Start_CPU_data.Content.ToString() == "Stop")
            {
                Start_CPU_data.Content = "Start";
                CpuSensorTimer.Stop();
            }
        }

    }  
}


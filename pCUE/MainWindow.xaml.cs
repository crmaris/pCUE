using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OpenHardwareMonitor.Hardware;
using System.Diagnostics;
using Microsoft.Win32;
using System.Threading;
using GetCoreTempInfoNET;

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

        //CoreTemp          
        static CoreTempInfo CTInfo;
        static System.Timers.Timer RefreshInfo;
        static Process CoreTemp = new Process();

        //fan constants
        public const int FAN_FORCE_THREE_PIN_MODE_ON = 0x01;
        public const int FAN_FORCE_THREE_PIN_MODE_OFF = 0x00;
        public const int FAN_CURVE_POINTS_NUM = 6;
        public const int FAN_CURVE_TEMP_GROUP_EXTERNAL = 255;

        //Gia to open Hardware
        Computer thisComputer;


        //Timer for CPU Data
        static System.Windows.Forms.Timer CoreTempTimer = new System.Windows.Forms.Timer();

        //Timer for min-max-avg values
        static System.Windows.Forms.Timer Set_Min_Max_AVG_timer = new System.Windows.Forms.Timer();


        //Timer Read Fans From Commander Pro
        static System.Windows.Forms.Timer Commander_Pro_Reader_fan = new System.Windows.Forms.Timer();

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
            CoreTempTimer.Tick += new EventHandler(CoreTempTimer_Tick);
            CoreTempTimer.Interval = 500; // specify interval time
  
            //Read Data from Commander Pro
            Commander_Pro_Reader_fan.Tick += new EventHandler(Commander_Pro_Reader_fan_Tick);
            Commander_Pro_Reader_fan.Interval = 500; // specify interval time as you want    

            //Give time to form to load properly timer
            oneShot.Interval = new TimeSpan(0, 0, 0, 1, 0);
            oneShot.Tick += new EventHandler(OneShot_Tick);    

            //timer for min-max-avg-values
            Set_Min_Max_AVG_timer.Tick += new EventHandler(Set_Min_Max_AVG_timer_Tick);
            Set_Min_Max_AVG_timer.Interval = 500; // specify interval time as you want  
            Set_Min_Max_AVG_timer.Start();
 
            // For OpenHardware
            thisComputer = new Computer() { CPUEnabled = true, GPUEnabled = true, HDDEnabled = true, RAMEnabled = true, FanControllerEnabled = true };
            thisComputer.Open();
        }

        #region Main Window Functions
        //Window Functions
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //Fills the Control Lists
            oneShot.Start();

            if (Properties.Settings.Default.AutoStart1)
            { autostartCheckBox.IsChecked = true; }

            if (Properties.Settings.Default.AVG_Values)
            { AVG_values.IsChecked = true; }

        }

        private void Window_Closed(object sender, EventArgs e)
        {
            CoreTempTimer.Stop();
            //timer_min_max.Stop(); 

            //if pCUE closes kill Core Temp
            Kill_Function("Core Temp");
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
                CoreTempTimer.Stop();
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
        //ask for fan speed and power every 1 sec
        //I only want to see active fans to not loose time and resources
        //I check fan enabled check boxes
        private void Commander_Pro_Reader_fan_Tick(object sender, EventArgs e)
        {
            if (Corsair_Commander_Connected == true)
            {

                //prepei na tsekaro to Fan_Mask gia na do pia fans einai ontos energa
                //to parakato prepei na trexei MONO mia fora se kathe loop
                //allios gamietai to apotelesma sto fan 0

                string fan_mask = Commander_Pro_READ_FAN_MASK(); //px. 011000


                //clear the output buffer
                for (int i = 0; i < 64; ++i)
                {
                    outbuf[i] = 0x00;
                }

                for (int j = 0; j < fan_mask.Length; j++)
                {
                    int fan_RPM = 0;
                    char y;

                    y = fan_mask[j];

                    // Get Fan Speed
                    if ((y == '1') || (y == '2'))
                    {
                        outbuf[1] = CorsairLightingProtocolConstants.READ_FAN_SPEED;  // //    outbuf[1] = CorsairLightingProtocolConstants.READ_FAN_POWER;
                        outbuf[2] = (byte)j;   // Select the fans one by one  

                        //send the command                       
                        stream.Write(outbuf);
                        //memo1.AppendText(string.Join(", ", outbuf1) + Environment.NewLine);                                      

                        //Read the response                        
                        stream.Read(inbuf);
                        //memo1.AppendText(string.Join(", ", inbuf1) + Environment.NewLine);

                        fan_RPM = 256 * inbuf[2] + inbuf[3];   //fan_RPM_Power = inbuf[2];

                        switch (j)
                        {
                            case 0:
                                Fan_array[0].Text = fan_RPM.ToString();                              
                                break;
                            case 1:
                                Fan_array[3].Text = fan_RPM.ToString();  
                                break;
                            case 2:
                                Fan_array[6].Text = fan_RPM.ToString();  
                                break;
                            case 3:
                                Fan_array[9].Text = fan_RPM.ToString();  
                                break;
                            case 4:
                                Fan_array[12].Text = fan_RPM.ToString();  
                                break;
                            case 5:
                                Fan_array[15].Text = fan_RPM.ToString();  
                                break;
                        }
                    }

                }
            }
        }     

        private string Commander_Pro_READ_FAN_MASK()
        {
            string fan_mask = "";

            if (Corsair_Commander_Connected == true)
            {

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

            return fan_power;
        }

        private int Commander_Pro_READ_FAN_Speed(byte fan_number)
        {
            int fan_speed = 0;

            if (Corsair_Commander_Connected == true)
            {
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

            return fan_speed;
        }

        //Set the fan mode
        private void Commander_Pro_Set_Fan_Connection_Mode(object sender, SelectionChangedEventArgs e)
        {
            if (Corsair_Commander_Connected == true)
            {
                //Commander_Pro_Reader_fan.Stop();

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

                //Thread.Sleep(100);
                //Commander_Pro_Reader_fan.Start();                
            }
        }

        //Set The Fan Speed
        private void Commander_Pro_Set_Fan_Speed(int fan_channel, int fan_speed)
        {
            if (Corsair_Commander_Connected == true)
            {
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

        //Set The Fan Power
        private void Commander_Pro_Set_Fan_Power(int fan_channel, int fan_power)
        {
            if (Corsair_Commander_Connected == true)
            {
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
                        
                        int i = 0;
                        //stream.WriteTimeout = -1; //disable the timeout
                        //stream.ReadTimeout = -1; //disable the timeout

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
                            Fan_Numeric_Boxes[i].Value = (uint)Commander_Pro_READ_FAN_Speed((byte)i);
                            Fan_Slider[i].Value = (uint)Commander_Pro_READ_FAN_Speed((byte)i); 
                            //Fan_Numeric_Boxes[i].Value = (uint)commander_Pro_READ_FAN_Power((byte)i);                          
                        }
                    
                        //Thread.Sleep(100);
                    
                        //Fan_Power_Mode.IsEnabled = true;
                        //Fan_Speed_Mode.IsEnabled = true;
                        //timer gia na zitaei synexos dedomena apo ta fans
                        Commander_Pro_Reader_fan.Start();
                                      
                    }

                    else if (device.GetProductName() != "Commander PRO")
                    {
                        //await Task.Delay(100);
                        MessageBox.Show("Cannot open Commander Pro!");                      
                    }
                        
            }
                 catch
                {
                    Open_Corsair_Commander.Content = "Open";
                    MessageBox.Show("Cannot open Commander Pro! Is it connected?");  
                    Commander_Pro_Reader_fan.Stop();
                    Corsair_Commander_Connected = false;
                    //Fan_Power_Mode.IsEnabled = false;
                    //Fan_Speed_Mode.IsEnabled = false;

                    foreach (TextBox box in Fan_array)
                    {
                        box.Text = "0000";
                    }
                  
                }
            }
             else if (Open_Corsair_Commander.Content.ToString() == "Close")
                    {
                Open_Corsair_Commander.Content = "Open";                      
                        Commander_Pro_Reader_fan.Stop();
                        Corsair_Commander_Connected = false;
                        //Fan_Power_Mode.IsEnabled = false;
                        //Fan_Speed_Mode.IsEnabled = false;

                        foreach (TextBox box in Fan_array)
                        {
                            box.Text = "0000";
                        }
                    }
                }

        #endregion

        #region For CoreTemp
        public void CTInfo_ReportError(ErrorCodes ErrCode, string ErrMsg)
        {
            CoreTempTimer.Stop();
            Start_CPU_data.Content = "Start";
            MessageBox.Show(ErrMsg);
        }

        private void CoreTempTimer_Tick(object sender, EventArgs e)
        {
            //Attempt to read shared memory.
            bool bReadSuccess = CTInfo.GetData();

            double Temperature = 0;
            double Load = 0;

            //If read was successful the post the new info on the console.
            if (bReadSuccess)
            {
                uint index;
                char TempType;

                //if (CTInfo.IsFahrenheit)
                //{
                //    label97.Text = "CPU Temp (°F)";
                //}
                //else
                //{
                //    label97.Text = "CPU Temp (°C)";
                //}

                //Console.WriteLine("CPU Name: " + CTInfo.GetCPUName);
                //Console.WriteLine("CPU Speed: " + CTInfo.GetCPUSpeed + "MHz (" + CTInfo.GetFSBSpeed + " x " + CTInfo.GetMultiplier + ")");
                //Console.WriteLine("CPU VID: " + CTInfo.GetVID + "v");
                //Console.WriteLine("Physical CPUs: " + CTInfo.GetCPUCount);
                //Console.WriteLine("Cores per CPU: " + CTInfo.GetCoreCount);              

                for (uint i = 0; i < CTInfo.GetCPUCount; i++)
                {
                    for (uint g = 0; g < CTInfo.GetCoreCount; g++)
                    {
                        index = g + (i * CTInfo.GetCoreCount);

                        Temperature += CTInfo.GetTemp[index];
                        Load += CTInfo.GetCoreLoad[index];
                    }
                }

                double AVGTemperature = Temperature / (CTInfo.GetCoreCount * CTInfo.GetCPUCount);

                double AVGLoad = Load / (CTInfo.GetCoreCount * CTInfo.GetCPUCount);

                double VID = CTInfo.GetVID;

                CPU_array[0].Text = AVGTemperature.ToString("0.0");
                CPU_array[3].Text = CTInfo.GetCPUSpeed.ToString("N1");
                CPU_array[6].Text = AVGLoad.ToString("N1");
            }

            else
            {
                //CoreTempTimer.Stop();
                //CoreTemp_timer_min_max.Stop();
                //MessageBox.Show("Internal error name: " + CTInfo.GetLastError + "Internal error name: " + CTInfo.GetLastError + "Internal error message: " + CTInfo.GetErrorMessage(CTInfo.GetLastError));               
                //Start_CPU_data.Text = "Start";
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
             foreach (System.Diagnostics.Process pr in System.Diagnostics.Process.GetProcesses()) //GETS PROCESSES
            {
                if ((pr.ProcessName == "CueLLAccessService") || (pr.ProcessName == "Corsair.Service.CpuIdRemote64")
                    || (pr.ProcessName == "Corsair.Service.DisplayAdapter") || (pr.ProcessName == "Corsair.Service"))
                {
                    pr.Kill(); //KILLS THE PROCESSES
                }
            }
        }
    

        private void Kill_Function(string App)
        {           
            Process[] processes = Process.GetProcessesByName(App);
            foreach (var process in processes)
            {
                process.Kill();
                //break;
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

                    //no AVG values
                    if (AVG_values.IsChecked == false)
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

                    // AVG values
                    else if (AVG_values.IsChecked == true)
                    {
                        switch (current)
                        {
                            case 0:
                                if (Grid == 1) { Sample_array[min].Text = avg_CPU_temp.ToString("0.#"); }
                                else if (Grid == 2) { Sample_array[min].Text = Math.Round(avg_fan1_speed).ToString(); }
                                break;
                            case 3:
                                if (Grid == 1) { Sample_array[min].Text = avg_CPU_MHz.ToString("0.#"); }
                                else if (Grid == 2) { Sample_array[min].Text = Math.Round(avg_fan2_speed).ToString(); }
                                break;
                            case 6:
                                if (Grid == 1) { Sample_array[min].Text = avg_CPU_Load.ToString("0.#"); }
                                else if (Grid == 2) { Sample_array[min].Text = Math.Round(avg_fan3_speed).ToString(); }
                                break;
                            case 9:
                                Sample_array[min].Text = Math.Round(avg_fan4_speed).ToString("0.#");
                                break;
                            case 12:
                                Sample_array[min].Text = Math.Round(avg_fan5_speed).ToString("0.#");
                                break;
                            case 15:
                                Sample_array[min].Text = Math.Round(avg_fan6_speed).ToString("0.#");
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
                Fan_Box.Header = "Fans Current/AVG/Max";
                Properties.Settings.Default.AVG_Values = true;
                Properties.Settings.Default.Save();
            }
            else
            {
                CPU_box.Header = "CPU Current/Min/Max";
                Fan_Box.Header = "Fans Current/Min/Max";
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
            if (Start_CPU_data.Content.ToString() == "Start")
            {
                try
                {
                    Start_CPU_data.Content = "Stop";
                   
                    //Start Core Temp
                    CoreTemp.StartInfo.FileName = @"Core Temp.exe";
                    CoreTemp.StartInfo.Arguments = "-minimized";
                    CoreTemp.Start();

                    Thread.Sleep(500);

                    //Initiate CoreTempInfo class.
                    CTInfo = new CoreTempInfo();

                    //Sign up for an event reporting errors
                    CTInfo.ReportError += new ErrorOccured(CTInfo_ReportError);

                    //Start to get readings from Core Temp
                    CoreTempTimer.Start();                   
                }
                catch
                {
                    CoreTempTimer.Stop();
                    Start_CPU_data.Content = "Start";

                    Kill_Function("Core Temp");                  
                }


            }
            else if (Start_CPU_data.Content.ToString() == "Stop")
            {
                Start_CPU_data.Content = "Start";
                Kill_Function("Core Temp");
                CoreTempTimer.Stop();             
            }
        }       

    }  
}


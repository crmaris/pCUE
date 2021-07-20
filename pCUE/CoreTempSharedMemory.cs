using GetCoreTempInfoNET;
using Syncfusion.Styles;
using System;
using System.Runtime.InteropServices;

namespace Case_Tester
{
    public class CoreTempInfo
    {
        Fields
        private ErrorMessages ErrMsgs;
        private core_temp_shared_data CoreTempData;     
        
        Events
        public event ErrorOccured ReportError;

        Methods
        public CoreTempInfo();
        public bool GetData();
        public string GetErrorMessage(ErrorCodes ErrCode);
        public void SendErrorReport(ErrorCodes ErrCode);

        Properties
        public float GetVID { get; }
        public bool IsFahrenheit { get; }
        public string GetCPUName { get; }
        public float GetMultiplier { get; }
        public float GetFSBSpeed { get; }
        public float GetCPUSpeed { get; }
        public bool IsDistanceToTjMax { get; }
        public float[] GetTemp { get; }
        public uint GetCoreCount { get; }
        public uint[] GetTjMax { get; }
        public uint[] GetCoreLoad { get; }
        public ErrorCodes GetLastError { get; }
        public uint GetCPUCount { get; }
        public core_temp_shared_data GetDataStruct { get; }

        Nested Types

        [StructLayout(LayoutKind.Sequential,Pack =1)]
        public struct core_temp_shared_data
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=0x100)]
            public uint[] uiLoad;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x80)]
            public uint[] uiTjMax;
            public uint uiCoreCnt;
            public uint uiCPUCnt;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x100)]
            public float[] fTemp;
            public float fVID;
            public float fCPUSpeed;
            public float fFSBSpeed;
            public float fMultipier;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 0x100)]
            public string CPUName;
            public byte ucFahrenheit;
            public byte ucDeltaToTjMax;
            public byte ucTdpSupported;
            public byte ucPowerSupported;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x80)]
            public uint uiStructVersion;
            public uint[] uiTdp;
            public uint[] fPower;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x100)]
            public uint[] fMultipliers;
        }


        public enum ErrorCodes
        {
            Data_Retrieve_Failed,
            Access_Denied,
            CoreTemp_Not_Found
        }

        internal class ErrorMessages
        {
            private string[] ErrMsgs;

            public ErrorMessages();

            public string this[ErrorCodes index] { get; }
            public string SetCustomError { set; }

        }

        public delegate void ErrorOccured(ErrorCodes ErrCode, string ErrMsg);      

        internal static class Win32Native
        {
            [DllImport("kernel32.dll)", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern bool CloseHandle(IntPtr hHandle);

            [DllImport("kernel32.dll)", CharSet = CharSet.Ansi, ExactSpelling = true)]
            public static extern IntPtr FreeLibary(IntPtr hModule);

            [DllImport("kernel32.dll)", CharSet = CharSet.Ansi, ExactSpelling = true)]
            public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

            [DllImport("kernel32.dll)", CharSet = CharSet.Ansi)]
            public static extern IntPtr LoadLibrary(string IpFileName);

            [DllImport("kernel32.dll)", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, Unit dwDesiredAccess, Unit dwFileOffestHigh, Unit dwFileOffesetLow, Unit dwNumberOfBytesToMap);
            [DllImport("kernel32.dll)", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, StyleInfoPropertyGrid IpName);
            [DllImport("kernel32.dll)", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr UnmapViewOfFile(IntPtr IpBaseAddress);
        }
    }
}

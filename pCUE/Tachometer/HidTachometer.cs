using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HidSharp;

namespace pCUE
{
    /// <summary>A single tachometer measurement.</summary>
    public struct TachoReading
    {
        public TachoReading(double rpm, bool batteryLow, DateTime timestampUtc)
        {
            Rpm = rpm;
            BatteryLow = batteryLow;
            TimestampUtc = timestampUtc;
        }

        public double Rpm { get; }
        public bool BatteryLow { get; }
        public DateTime TimestampUtc { get; }
    }

    public sealed class TachoReadingEventArgs : EventArgs
    {
        public TachoReadingEventArgs(TachoReading reading) { Reading = reading; }
        public TachoReading Reading { get; }
    }

    /// <summary>
    /// USB-HID bench tachometer driver. Ported into pCUE from the Fan Control Application
    /// (FanRpmControl), which itself adapted it from the Faganas ATX12V project. Transport is
    /// <b>HidSharp</b> (the same library pCUE already uses for the Commander PRO): a blocking
    /// <see cref="HidStream"/> read on a dedicated background thread, with auto-reconnect via
    /// <see cref="DeviceList"/>.Changed.
    ///
    /// Hardware: CH340-class HID bridge, VID 0x1A86 / PID 0xE008. The RPM decode (7-segment byte
    /// stream, 0d 0a sentinel, 24 half-byte tokens, digit table, battery flag) is preserved
    /// byte-for-byte from the original - only the transport and the logging changed.
    ///
    /// Measurement only: it never drives a fan. pCUE overlays the decoded RPM onto the assigned
    /// fan's readout and falls back to the Commander PRO's own reading when the tach signal is stale.
    /// </summary>
    public sealed class HidTachometer : IDisposable
    {
        private const int VendorId = 0x1A86;
        private const int ProductId = 0xE008;

        // The handheld tachometer will not display RPM above this; treat higher as noise.
        private const int MaxRpm = 5000;

        private const int FeatureReportLength = 8;

        // Index of the device's significant byte within a full HID input report. Windows prepends a
        // report-ID byte at index 0; HidLibrary exposed it as report.Data[1] (= raw index 2). HidSharp
        // returns the full raw report, so the equivalent index is 2.
        private const int SignificantByteIndex = 2;

        // A decoded RPM older than this is considered stale (no fresh signal).
        public int StalenessMs { get; set; } = 1500;

        // Read timeout so the loop periodically wakes to observe shutdown/removal.
        public int ReadTimeoutMs { get; set; } = 2000;

        private readonly object _stateLock = new object();
        private readonly object _rxLock = new object();
        private readonly List<string> _hexList = new List<string>(64);

        private HidDevice _device;
        private HidStream _stream;
        private Thread _readThread;

        private volatile bool _connected;
        private volatile bool _disposing;
        private volatile bool _autoReconnectEnabled;
        private volatile bool _running;
        private int _reconnectScanRunning;

        private double _latestRpm;
        private DateTime _latestRpmUtc = DateTime.MinValue;
        private volatile bool _batteryLow;
        private volatile bool _changedSubscribed;

        public string Name { get { return "HID Tachometer (VID 1A86 / PID E008)"; } }
        public bool IsConnected { get { return _connected; } }
        public bool BatteryLow { get { return _batteryLow; } }

        public event EventHandler<TachoReadingEventArgs> ReadingChanged;
        public event EventHandler<bool> ConnectionChanged;

        private static class Digit
        {
            public const int C0 = 123, C0a = 251;
            public const int C1 = 96, C1a = 224;
            public const int C2 = 94, C2a = 222;
            public const int C3 = 124, C3a = 252;
            public const int C4 = 101, C4a = 229;
            public const int C5 = 61, C5a = 189;
            public const int C6 = 63, C6a = 191;
            public const int C7 = 112, C7a = 240;
            public const int C8 = 127, C8a = 255;
            public const int C9 = 125, C9a = 253;
            public const int L = 11, La = 139;
        }

        // ============================ Public API ============================
        public void Connect()
        {
            lock (_stateLock)
            {
                _autoReconnectEnabled = false;
                LogEnvironmentAndHidInventory();
                EnsureChangedSubscribed();
                ConnectNoLock(true);
            }
        }

        public void Disconnect()
        {
            lock (_stateLock)
            {
                _autoReconnectEnabled = false;
                DisconnectNoLock();
            }
        }

        // Returns the most recent valid and fresh RPM value, or null when no trustworthy reading is
        // currently available (device lost, stale, or out of range).
        public double? ReadRpm()
        {
            if (!_connected) return null;
            lock (_rxLock)
            {
                if (_latestRpmUtc == DateTime.MinValue) return null;
                if ((DateTime.UtcNow - _latestRpmUtc).TotalMilliseconds > StalenessMs) return null;
                return _latestRpm;
            }
        }

        public void Dispose()
        {
            _disposing = true;
            if (_changedSubscribed)
            {
                try { DeviceList.Local.Changed -= OnDeviceListChanged; } catch { }
                _changedSubscribed = false;
            }
            Disconnect();
        }

        // ============================ Internals ============================
        private void EnsureChangedSubscribed()
        {
            if (_changedSubscribed) return;
            try { DeviceList.Local.Changed += OnDeviceListChanged; _changedSubscribed = true; }
            catch (Exception ex) { Debug.WriteLine("pCUE: tach device-change subscription failed: " + ex.Message); }
        }

        private void ConnectNoLock(bool throwIfMissing)
        {
            if (_disposing) return;
            if (_connected && _stream != null) return;

            DisconnectNoLock();

            HidDevice device = DeviceList.Local.GetHidDevices(VendorId, ProductId, null, null).FirstOrDefault();
            if (device == null)
            {
                _connected = false;
                if (throwIfMissing)
                    throw new InvalidOperationException("Tachometer not found (VID 0x1A86 / PID 0xE008). Is it connected and powered on?");
                Debug.WriteLine("pCUE: tach reconnect skipped; device not found.");
                return;
            }

            HidStream stream;
            if (!device.TryOpen(out stream))
            {
                _connected = false;
                if (throwIfMissing)
                    throw new InvalidOperationException("Tachometer was found but could not be opened (in use by another app?).");
                Debug.WriteLine("pCUE: tach reconnect skipped; device could not be opened.");
                return;
            }

            stream.ReadTimeout = ReadTimeoutMs;
            _device = device;
            _stream = stream;
            _connected = true;
            _autoReconnectEnabled = true;

            // Best-effort feature reset (original behaviour); harmless if the device ignores it.
            try { _stream.SetFeature(new byte[FeatureReportLength]); }
            catch (Exception ex) { Debug.WriteLine("pCUE: tach feature reset failed: " + ex.Message); }

            Debug.WriteLine("pCUE: tachometer connected.");
            RaiseConnection(true);
            StartReadThread();
        }

        private void DisconnectNoLock()
        {
            bool wasConnected = _connected;
            _connected = false;
            _running = false;

            Thread thread = _readThread;
            _readThread = null;

            lock (_rxLock)
            {
                _hexList.Clear();
                _latestRpmUtc = DateTime.MinValue;
                _latestRpm = 0;
            }
            _batteryLow = false;

            // Closing the stream unblocks any in-progress blocking Read on the loop thread.
            try { if (_stream != null) _stream.Close(); } catch { }
            try { if (_stream != null) _stream.Dispose(); } catch { }
            _stream = null;
            _device = null;

            if (thread != null && thread.IsAlive &&
                thread.ManagedThreadId != Environment.CurrentManagedThreadId)
            {
                try { thread.Join(1000); } catch { }
            }

            if (wasConnected)
            {
                Debug.WriteLine("pCUE: tachometer disconnected.");
                RaiseConnection(false);
            }
        }

        private void StartReadThread()
        {
            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "HidTachRead" };
            _readThread.Start();
        }

        private void ReadLoop()
        {
            HidStream stream = _stream;
            HidDevice device = _device;
            if (stream == null || device == null) return;

            int len;
            try { len = device.GetMaxInputReportLength(); } catch { len = 64; }
            if (len <= 0) len = 64;

            byte[] buffer = new byte[len];
            bool firstReportLogged = false;

            while (_running && _connected)
            {
                int n;
                try
                {
                    n = stream.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue; // no report this interval; loop and re-check _running
                }
                catch (Exception ex)
                {
                    if (_running) { Debug.WriteLine("pCUE: tach HID read failed: " + ex.Message); HandleDeviceLost(); }
                    break;
                }

                if (n <= 0) continue;
                if (!firstReportLogged) { firstReportLogged = true; LogFirstReport(buffer, n); }
                ProcessReport(buffer, n);
            }
        }

        private void HandleDeviceLost()
        {
            // Keep auto-reconnect armed; the DeviceList.Changed handler re-opens on re-insertion.
            lock (_stateLock)
            {
                if (_disposing) return;
                DisconnectNoLock();
            }
        }

        private void ProcessReport(byte[] buffer, int count)
        {
            if (!_connected) return;

            try
            {
                if (count <= SignificantByteIndex) return;

                string hex = buffer[SignificantByteIndex].ToString("x2");
                List<string> rpmDigits = null;

                lock (_rxLock)
                {
                    _hexList.Add(hex);
                    if (_hexList.Count > 64)
                        _hexList.RemoveRange(0, _hexList.Count - 64);

                    if (_hexList.Count >= 50)
                    {
                        int oaIndex = -1;
                        for (int idx = 27; idx < _hexList.Count; idx++)
                        {
                            if (_hexList[idx] == "0a" && _hexList[idx - 1] == "0d")
                            {
                                oaIndex = idx;
                                break;
                            }
                        }

                        if (oaIndex >= 27)
                        {
                            List<string> digits = new List<string>(24);
                            for (int i = 1; i <= 24; i++)
                            {
                                string token = _hexList[oaIndex - 26 + i];
                                int lowNibble = Convert.ToInt32(token.Substring(1, 1), 16);
                                digits.Add(lowNibble.ToString("X"));
                            }
                            rpmDigits = digits;
                            _hexList.Clear();
                        }
                    }
                }

                if (rpmDigits != null)
                    DecodeRpm(rpmDigits);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: tach report decode error: " + ex.Message);
            }
        }

        // Decodes the 24 half-byte tokens into an RPM value (original algorithm, verbatim).
        private void DecodeRpm(List<string> tachoData)
        {
            List<string> bytesHex = new List<string>(12);
            List<int> bytesVal = new List<int>(12);

            try
            {
                for (int i = 0; i <= 11; i++)
                {
                    bytesHex.Add(tachoData[i * 2] + tachoData[(i * 2) + 1]);
                    bytesVal.Add(Convert.ToInt32(bytesHex[i], 16));
                }
            }
            catch { return; }

            string result = "";
            for (int i = 0; i <= bytesHex.Count - 2; i++)
            {
                switch (bytesVal[i])
                {
                    case Digit.C0: result += "0"; break; case Digit.C0a: result += ".0"; break;
                    case Digit.C1: result += "1"; break; case Digit.C1a: result += ".1"; break;
                    case Digit.C2: result += "2"; break; case Digit.C2a: result += ".2"; break;
                    case Digit.C3: result += "3"; break; case Digit.C3a: result += ".3"; break;
                    case Digit.C4: result += "4"; break; case Digit.C4a: result += ".4"; break;
                    case Digit.C5: result += "5"; break; case Digit.C5a: result += ".5"; break;
                    case Digit.C6: result += "6"; break; case Digit.C6a: result += ".6"; break;
                    case Digit.C7: result += "7"; break; case Digit.C7a: result += ".7"; break;
                    case Digit.C8: result += "8"; break; case Digit.C8a: result += ".8"; break;
                    case Digit.C9: result += "9"; break; case Digit.C9a: result += ".9"; break;
                    case Digit.L: result += "L"; break;
                }
            }

            double realRpm = 0;
            try
            {
                string rpmText = result.Length >= 6 ? result.Substring(0, 6) : "";
                double parsed;
                if (double.TryParse(rpmText, out parsed))
                {
                    rpmText = Reverse(rpmText);
                    realRpm = Convert.ToDouble(rpmText);
                }
            }
            catch { }

            bool batteryLow = false;
            try
            {
                string b = Convert.ToString(bytesVal[10], 2).PadLeft(8, '0');
                batteryLow = b.Substring(6, 1) == "1";
            }
            catch { }
            _batteryLow = batteryLow;

            if (realRpm >= 0 && realRpm < MaxRpm)
            {
                double rounded = RoundI(realRpm, 1);
                lock (_rxLock)
                {
                    _latestRpm = rounded;
                    _latestRpmUtc = DateTime.UtcNow;
                }

                EventHandler<TachoReadingEventArgs> handler = ReadingChanged;
                if (handler != null)
                {
                    try { handler(this, new TachoReadingEventArgs(new TachoReading(rounded, batteryLow, DateTime.UtcNow))); }
                    catch { }
                }
            }
        }

        // ============================ Device events ============================
        private void OnDeviceListChanged(object sender, DeviceListChangedEventArgs e)
        {
            if (_disposing || !_autoReconnectEnabled) return;

            if (Interlocked.CompareExchange(ref _reconnectScanRunning, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    lock (_stateLock)
                    {
                        if (_disposing || !_autoReconnectEnabled) return;

                        bool present = DeviceList.Local.GetHidDevices(VendorId, ProductId, null, null).Any();
                        if (_connected && !present)
                        {
                            DisconnectNoLock(); // physically removed; stay armed for re-insertion
                        }
                        else if (!_connected && present)
                        {
                            try { ConnectNoLock(false); }
                            catch (Exception ex) { _connected = false; Debug.WriteLine("pCUE: tach reconnect failed: " + ex.Message); }
                        }
                    }
                }
                finally { Interlocked.Exchange(ref _reconnectScanRunning, 0); }
            });
        }

        private void RaiseConnection(bool connected)
        {
            EventHandler<bool> handler = ConnectionChanged;
            if (handler != null)
            {
                try { handler(this, connected); } catch { }
            }
        }

        // ============================ Diagnostics ============================
        // Logs a HID inventory before connecting so the target VID/PID can be confirmed present.
        private void LogEnvironmentAndHidInventory()
        {
            try
            {
                Debug.WriteLine("pCUE: tach HID diagnostics --------------------------------------");
                Debug.WriteLine("  OS: " + Environment.OSVersion + " (64-bit process: " + Environment.Is64BitProcess + ")");

                Version hidVer = null;
                try { hidVer = typeof(HidDevice).Assembly.GetName().Version; } catch { }
                Debug.WriteLine("  HidSharp version: " + (hidVer != null ? hidVer.ToString() : "(unknown)"));
                Debug.WriteLine(string.Format("  Expected tachometer: VID 0x{0:X4} / PID 0x{1:X4}", VendorId, ProductId));

                int count = 0;
                bool targetFound = false;
                foreach (HidDevice d in DeviceList.Local.GetHidDevices())
                {
                    count++;
                    int vid = 0, pid = 0;
                    try { vid = d.VendorID; pid = d.ProductID; } catch { }
                    string product = SafeHid(delegate { return d.GetProductName(); });
                    bool isTarget = vid == VendorId && pid == ProductId;
                    targetFound |= isTarget;
                    Debug.WriteLine(string.Format("  HID[{0}] VID=0x{1:X4} PID=0x{2:X4} product='{3}'{4}",
                        count, vid, pid, product, isTarget ? "   <-- TARGET" : ""));
                }
                Debug.WriteLine("  HID devices found: " + count + ". Expected VID/PID present: " + targetFound + ".");
                Debug.WriteLine("----------------------------------------------------------------");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("pCUE: tach HID diagnostics enumeration failed: " + ex.Message);
            }
        }

        // Logs the first raw input report so the significant-byte offset can be verified on real hardware.
        private void LogFirstReport(byte[] buffer, int count)
        {
            try
            {
                int show = Math.Min(count, 12);
                string hex = string.Join(" ", buffer.Take(show).Select(b => b.ToString("x2")));
                Debug.WriteLine(string.Format("pCUE: tach first report ({0} bytes): {1}{2} (significant byte index {3} = 0x{4:x2})",
                    count, hex, count > show ? " ..." : "", SignificantByteIndex, buffer[Math.Min(SignificantByteIndex, count - 1)]));
            }
            catch { }
        }

        private static string SafeHid(Func<string> read)
        {
            try { string s = read(); return (s ?? string.Empty).Trim(); }
            catch { return ""; }
        }

        private static double RoundI(double number, double roundingInterval)
        {
            return (double)((decimal)roundingInterval *
                Math.Round((decimal)number / (decimal)roundingInterval, MidpointRounding.AwayFromZero));
        }

        private static string Reverse(string s)
        {
            char[] c = s.ToCharArray();
            Array.Reverse(c);
            return new string(c);
        }
    }
}

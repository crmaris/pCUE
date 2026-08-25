using System;
using System.Linq;

namespace pCUE
{
    /// <summary>
    /// Thrown by <see cref="CommanderProDevice.Connect"/>. <see cref="DeviceFound"/> separates
    /// "a USB device answered but it is not a Commander PRO" from "nothing usable there", so the
    /// UI can show the same two outcomes it always has.
    /// </summary>
    public sealed class CommanderProOpenException : Exception
    {
        public bool DeviceFound { get; }
        public CommanderProOpenException(string message, bool deviceFound) : base(message)
        {
            DeviceFound = deviceFound;
        }
    }

    /// <summary>
    /// Owns the single USB-HID session to the Corsair Commander PRO: connect/disconnect, the
    /// serialized read/write primitives, and the device's response status byte.
    ///
    /// Extracted from MainWindow.xaml.cs so the protocol is testable without WPF. Behavior is
    /// preserved from the code that lived there, with one deliberate upgrade: every WRITE now
    /// checks the reply's status byte (0x00 OK / 0x01 error) and returns false when the device
    /// rejected the command. pCUE historically discarded that byte, so an unsupported command -
    /// e.g. a fixed-RPM target on a 3-pin/DC channel - looked exactly like success.
    ///
    /// Threading: every device transaction takes <see cref="_ioLock"/>, so the background poll
    /// loop, UI-thread commands and remote-API worker threads can never overlap on the stream.
    /// A null <see cref="_stream"/> makes any in-flight call bail out; closing the stream
    /// interrupts a blocking transfer (timeouts are set at open).
    /// </summary>
    public sealed class CommanderProDevice
    {
        public const int FanChannels = 6;
        private const int VendorId = 0x1b1c;   // Corsair
        private const int ProductId = 0x0c10;  // Commander PRO

        private readonly HidSharp.HidDeviceLoader _loader = new HidSharp.HidDeviceLoader();
        private readonly object _ioLock = new object();
        private readonly byte[] _out = new byte[CorsairLightingProtocolConstants.COMMAND_SIZE];
        private readonly byte[] _in = new byte[CorsairLightingProtocolConstants.RESPONSE_SIZE];

        private HidSharp.HidDevice _device;
        private HidSharp.HidStream _stream;

        // Duty pCUE last commanded per channel this session, -1 when none. The RPM hold starts
        // from this rather than a fixed kick percentage. It is only updated on a write the device
        // ACCEPTED, so it always reflects what the hardware is actually doing.
        private readonly int[] _lastCommandedDuty = new int[FanChannels] { -1, -1, -1, -1, -1, -1 };

        public bool IsConnected { get; private set; }
        public string FirmwareVersion { get; private set; } = "";

        public int LastCommandedDuty(int channel)
        {
            return (channel >= 0 && channel < FanChannels) ? _lastCommandedDuty[channel] : -1;
        }

        // ---------------------------------------------------------------- connection

        /// <summary>
        /// Opens the device, reads its firmware version and leaves the session ready.
        /// Throws InvalidOperationException with an operator-readable reason on failure.
        /// </summary>
        public void Connect()
        {
            lock (_ioLock)
            {
                if (IsConnected) return;

                HidSharp.HidDevice device;
                try { device = _loader.GetDevices(VendorId, ProductId, null, null).First(); }
                catch (Exception)
                {
                    throw new CommanderProOpenException("Cannot open Commander Pro! Is it connected?", false);
                }

                if (device.GetProductName() != "Commander PRO")
                    throw new CommanderProOpenException("Cannot open Commander Pro!", true);

                HidSharp.HidStream stream;
                if (!device.TryOpen(out stream))
                    throw new CommanderProOpenException("Cannot open Commander Pro! Is it connected?", false);

                // Bound any blocking HID transfer so a stalled device cannot hang the poll loop
                // (or a UI command waiting on the lock) indefinitely.
                stream.ReadTimeout = 1000;
                stream.WriteTimeout = 1000;

                _device = device;
                _stream = stream;
                IsConnected = true;

                FirmwareVersion = ReadFirmwareVersionNoLock();
            }
        }

        /// <summary>Closes the session. Idempotent and safe from any thread.</summary>
        public void Disconnect()
        {
            HidSharp.HidStream local;
            lock (_ioLock)
            {
                IsConnected = false;
                local = _stream;
                _stream = null;
                _device = null;
            }
            if (local == null) return;
            try { local.Close(); } catch (Exception ex) { AppLog.Warn("HID stream close failed: " + ex.Message); }
            try { local.Dispose(); } catch { }
        }

        // ---------------------------------------------------------------- reads

        /// <summary>Which channels are populated, as the device's six detection digits ("011000").</summary>
        public string ReadFanMask()
        {
            lock (_ioLock)
            {
                if (_stream == null) return "000000";

                ClearOut();
                _out[1] = (byte)CorsairLightingProtocolConstants.READ_FAN_MASK;
                _stream.Write(_out);
                _stream.Read(_in);

                string fan_mask = "";
                for (int k = 2; k < 8; k++) fan_mask += _in[k].ToString();
                return (fan_mask.Length == 6) ? fan_mask : "000000";
            }
        }

        /// <summary>The channel's tachometer reading in RPM (0 when unpopulated or unreadable).</summary>
        public int ReadFanRpm(int channel)
        {
            lock (_ioLock)
            {
                if (_stream == null) return 0;

                ClearOut();
                _out[1] = (byte)CorsairLightingProtocolConstants.READ_FAN_SPEED;
                _out[2] = (byte)channel;
                _stream.Write(_out);
                _stream.Read(_in);

                return (_in[2] << 8) + _in[3];
            }
        }

        /// <summary>
        /// The duty percent the device is currently applying to the channel. Returns 0 when the
        /// read fails - callers must treat 0 as "unknown", not "stopped".
        /// </summary>
        public int ReadFanPower(int channel)
        {
            lock (_ioLock)
            {
                if (_stream == null) return 0;

                ClearOut();
                _out[1] = (byte)CorsairLightingProtocolConstants.READ_FAN_POWER;
                _out[2] = (byte)channel;
                _stream.Write(_out);
                _stream.Read(_in);

                // Payload data starts at _in[2], same convention as READ_FAN_SPEED's
                // _in[2]<<8|_in[3]. Verified against ground truth on the bench (2026-08-08):
                // a fan left at 33% reported 33 after an app restart.
                return _in[2] <= 100 ? _in[2] : 0;
            }
        }

        /// <summary>Firmware "major.minor.patch", or "" before connect / on failure.</summary>
        private string ReadFirmwareVersionNoLock()
        {
            try
            {
                ClearOut();
                _out[1] = (byte)CorsairLightingProtocolConstants.READ_FIRMWARE_VERSION;
                _stream.Write(_out);
                _stream.Read(_in);
                return _in[2] + "." + _in[3] + "." + _in[4];
            }
            catch (Exception ex)
            {
                AppLog.Warn("Firmware version read failed: " + ex.Message);
                return "";
            }
        }

        // ---------------------------------------------------------------- writes
        // Every write returns false when the DEVICE rejected the command (status byte 0x01).
        // A false means "the hardware said no" - not a transport error, which throws instead.

        public bool WriteFanPower(int channel, int percent)
        {
            lock (_ioLock)
            {
                if (_stream == null) return false;

                ClearOut();
                _out[1] = (byte)CorsairLightingProtocolConstants.WRITE_FAN_POWER;
                _out[2] = (byte)channel;
                _out[3] = (byte)percent;
                _stream.Write(_out);
                _stream.Read(_in);

                bool ok = LogExchange("WRITE_FAN_POWER fan=" + (channel + 1) + " duty=" + percent + "%", 4);
                if (ok && channel >= 0 && channel < FanChannels) _lastCommandedDuty[channel] = percent;
                return ok;
            }
        }

        public bool WriteFanSpeed(int channel, int rpm)
        {
            lock (_ioLock)
            {
                if (_stream == null) return false;

                ClearOut();
                _out[1] = (byte)CorsairLightingProtocolConstants.WRITE_FAN_SPEED;
                _out[2] = (byte)channel;
                _out[3] = (byte)(rpm >> 8);   // big endian, per the protocol
                _out[4] = (byte)(rpm & 0xff);
                _stream.Write(_out);
                _stream.Read(_in);

                return LogExchange("WRITE_FAN_SPEED fan=" + (channel + 1) + " rpm=" + rpm, 5);
            }
        }

        public bool WriteFanDetectionType(int channel, FanDetectionType type)
        {
            lock (_ioLock)
            {
                if (_stream == null) return false;

                ClearOut();
                _out[1] = (byte)CorsairLightingProtocolConstants.WRITE_FAN_DETECTION_TYPE;
                _out[2] = 0x02;
                _out[3] = (byte)channel;
                _out[4] = (byte)type;
                _stream.Write(_out);
                _stream.Read(_in);

                return LogExchange("WRITE_FAN_DETECTION_TYPE fan=" + (channel + 1) +
                                   " mode=" + (int)type, 5);
            }
        }

        // ---------------------------------------------------------------- internals

        private void ClearOut()
        {
            Array.Clear(_out, 0, _out.Length);
        }

        /// <summary>
        /// Traces an HID command and the device's reply, and reports the status byte. Returns
        /// true when the device accepted the command (0x00). The Commander answers every write
        /// with 0x00 OK / 0x01 error; pCUE discarded that byte for years, which made a rejected
        /// command look identical to a successful one.
        /// </summary>
        private bool LogExchange(string what, int outBytes)
        {
            // _in[0] is the HID REPORT ID, not the status. HidSharp carries the report id in byte 0
            // of both directions - which is exactly why every command here is written to _out[1] -
            // so the device's payload starts at _in[1], and the status byte is the first byte of
            // that payload. The reads corroborate it: they all take their first DATA byte from
            // _in[2], one past the status. (liquidctl, working from a buffer with no report id,
            // reads fan RPM at res[1:3] with res[0] as the status; every index here is one higher.)
            //
            // Reading _in[0] compared the report id - always 0x00, always equal to
            // PROTOCOL_RESPONSE_OK - so the check could never fail and every write was reported as
            // accepted no matter what the Commander said.
            byte status = _in.Length > 1 ? _in[1] : (byte)0xFF;

            // Only an explicit 0x01 is a refusal. Anything else unexpected means the framing is not
            // what is assumed here, and announcing "rejected" at a fan that is running perfectly
            // would be worse than missing a rejection - it is logged loudly and treated as OK, so a
            // wrong assumption degrades to the old behaviour instead of breaking every write.
            bool rejected = status == CorsairLightingProtocolConstants.PROTOCOL_RESPONSE_ERROR;
            bool unexpected = !rejected && status != CorsairLightingProtocolConstants.PROTOCOL_RESPONSE_OK;
            bool ok = !rejected;

            string verdict;
            if (rejected) verdict = "  [DEVICE REJECTED 0x" + status.ToString("x2") + "]";
            else if (unexpected) verdict = "  [UNEXPECTED STATUS 0x" + status.ToString("x2") + " - treated as OK]";
            else verdict = "  [OK]";

            string text = what
                        + "  ->  " + AppLog.Hex(_out, outBytes)
                        + "  <-  " + AppLog.Hex(_in, 6)
                        + verdict;
            if (ok && !unexpected) AppLog.Debug(text); else AppLog.Warn(text);
            return ok;
        }
    }
}

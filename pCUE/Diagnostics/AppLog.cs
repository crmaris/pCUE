using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace pCUE
{
    public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

    /// <summary>
    /// Process-wide diagnostic log: a rolling in-memory buffer (readable over the remote API) plus
    /// an optional file.
    ///
    /// This exists because the rest of the app logs through <see cref="System.Diagnostics.Debug"/>,
    /// which is annotated [Conditional("DEBUG")] and therefore compiled OUT of Release builds - the
    /// builds users actually run. So the shipped app produced no diagnostics at all, which makes a
    /// hardware problem on a remote bench effectively undebuggable. Everything here works in Release.
    ///
    /// Thread-safe: called from the UI thread, the HID poll task, the tachometer read thread, the
    /// RPM-hold loop and HTTP worker threads.
    /// </summary>
    public static class AppLog
    {
        private const int MaxLines = 4000;
        // File mirror cap: at 2 MB the log rotates to ".1" (one backup kept). A bench left in
        // Debug for a week must not fill the disk; the in-memory buffer is unaffected.
        private const long MaxLogFileBytes = 2L * 1024 * 1024;
        private const long SizeCheckIntervalBytes = 512L * 1024;
        private static long _bytesSinceSizeCheck;

        private static readonly object Gate = new object();
        private static readonly Queue<string> Lines = new Queue<string>(MaxLines);
        private static string _filePath;
        private static bool _toFile;
        //Kept open so a chatty Debug level costs one buffered write per line instead of an
        //open/append/close cycle (the old File.AppendAllText-per-line did real I/O each call).
        private static StreamWriter _fileWriter;

        /// <summary>Messages below this level are dropped. Debug is off by default (it is chatty).</summary>
        private static volatile LogLevel _level = LogLevel.Info;
        public static LogLevel Level { get { return _level; } set { _level = value; } }

        public static string FilePath { get { lock (Gate) return _filePath; } }
        public static bool FileEnabled { get { lock (Gate) return _toFile; } }

        /// <summary>Starts mirroring to %LOCALAPPDATA%\pCUE\logs\pcue_&lt;stamp&gt;.log.</summary>
        public static void EnableFile()
        {
            lock (Gate)
            {
                if (_toFile) return;
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "pCUE", "logs");
                    Directory.CreateDirectory(dir);
                    _filePath = Path.Combine(dir,
                        "pcue_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".log");
                    _fileWriter = new StreamWriter(_filePath, append: true, Encoding.UTF8) { AutoFlush = true };
                    _fileWriter.WriteLine("pCUE log started " + DateTime.Now);
                    _toFile = true;
                }
                catch (Exception ex)
                {
                    _toFile = false;
                    TryCloseWriterNoLock();
                    System.Diagnostics.Debug.WriteLine("pCUE: could not open log file: " + ex.Message);
                }
            }
        }

        public static void Debug(string message) { Write(LogLevel.Debug, message); }
        public static void Info(string message) { Write(LogLevel.Info, message); }
        public static void Warn(string message) { Write(LogLevel.Warn, message); }
        public static void Error(string message) { Write(LogLevel.Error, message); }

        public static void Write(LogLevel level, string message)
        {
            if (level < _level) return;

            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + "  " + level.ToString().ToUpperInvariant().PadRight(5)
                        + "  " + (message ?? "");

            StreamWriter writer;
            lock (Gate)
            {
                Lines.Enqueue(line);
                while (Lines.Count > MaxLines) Lines.Dequeue();
                writer = _toFile ? _fileWriter : null;
                if (writer != null)
                {
                    _bytesSinceSizeCheck += line.Length + 2;
                    if (_bytesSinceSizeCheck >= SizeCheckIntervalBytes)
                    {
                        _bytesSinceSizeCheck = 0;
                        CheckRotationNoLock();
                        writer = _toFile ? _fileWriter : null;
                    }
                }
            }

            // File I/O outside the global lock: a slow/full disk must not stall the poll,
            // HID, hold or HTTP threads that share Gate. A genuine failure disables the mirror;
            // a writer swapped by rotation is simply stale (the line is already buffered).
            if (writer != null)
            {
                try { writer.WriteLine(line); }
                catch
                {
                    lock (Gate)
                    {
                        if (ReferenceEquals(writer, _fileWriter))
                        {
                            _toFile = false;
                            TryCloseWriterNoLock();
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine(line);
        }

        private static void TryCloseWriterNoLock()
        {
            try { _fileWriter?.Dispose(); } catch { }
            _fileWriter = null;
        }

        // Call with Gate held. Rolls the file to "<path>.1" when it exceeds the cap.
        private static void CheckRotationNoLock()
        {
            try
            {
                if (!_toFile || string.IsNullOrEmpty(_filePath)) return;
                var info = new FileInfo(_filePath);
                if (!info.Exists || info.Length < MaxLogFileBytes) return;
                TryCloseWriterNoLock();
                _toFile = false;
                try
                {
                    string backup = _filePath + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(_filePath, backup);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("pCUE: log rotation failed: " + ex.Message);
                }
                try
                {
                    _fileWriter = new StreamWriter(_filePath, append: false, Encoding.UTF8) { AutoFlush = true };
                    _fileWriter.WriteLine("pCUE log rotated " + DateTime.Now);
                    _toFile = true;
                    _bytesSinceSizeCheck = 0;
                }
                catch (Exception ex)
                {
                    TryCloseWriterNoLock();
                    System.Diagnostics.Debug.WriteLine("pCUE: could not reopen log file: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("pCUE: log rotation check failed: " + ex.Message);
            }
        }

        /// <summary>Most recent lines, oldest first. Used by the remote API's /log endpoint.</summary>
        public static string[] Tail(int count)
        {
            if (count <= 0) count = 200;
            lock (Gate)
            {
                var all = Lines.ToArray();
                if (all.Length <= count) return all;
                var slice = new string[count];
                Array.Copy(all, all.Length - count, slice, 0, count);
                return slice;
            }
        }

        public static void Clear()
        {
            lock (Gate) Lines.Clear();
        }

        /// <summary>Formats bytes as hex for HID command tracing, e.g. "00 23 01 3c".</summary>
        public static string Hex(byte[] buffer, int count)
        {
            if (buffer == null) return "";
            var sb = new StringBuilder();
            int n = Math.Min(count, buffer.Length);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(buffer[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }
}

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

        private static readonly object Gate = new object();
        private static readonly Queue<string> Lines = new Queue<string>(MaxLines);
        private static string _filePath;
        private static bool _toFile;
        //Kept open so a chatty Debug level costs one buffered write per line instead of an
        //open/append/close cycle (the old File.AppendAllText-per-line did real I/O each call).
        private static StreamWriter _fileWriter;

        /// <summary>Messages below this level are dropped. Debug is off by default (it is chatty).</summary>
        public static LogLevel Level { get; set; } = LogLevel.Info;

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
            if (level < Level) return;

            string line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + "  " + level.ToString().ToUpperInvariant().PadRight(5)
                        + "  " + (message ?? "");

            lock (Gate)
            {
                Lines.Enqueue(line);
                while (Lines.Count > MaxLines) Lines.Dequeue();

                if (_toFile)
                {
                    try { _fileWriter.WriteLine(line); }
                    catch
                    {
                        //A failing file (disk full, share removed, ...) must never break the app;
                        //drop the mirror and keep the in-memory buffer.
                        _toFile = false;
                        TryCloseWriterNoLock();
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

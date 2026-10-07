using pCUE;
using System.Globalization;

namespace Pcue.Linux;

public sealed class LinuxSensors
{
    readonly string root;
    long oldTotal, oldIdle;
    public LinuxSensors(string root = "/") { this.root = root; }
    string Full(string relative) => Path.Combine(root, relative);
    public PcueCpuStatus Read()
    {
        var result = new PcueCpuStatus { monitoring = true, load = "N/A", mhz = "N/A", temperature = "N/A" };
        try
        {
            var n = File.ReadLines(Full("proc/stat")).First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(long.Parse).ToArray();
            long total = n.Sum(), idle = n[3] + n[4];
            if (oldTotal > 0 && total > oldTotal) result.load = (100.0 * (1 - (double)(idle - oldIdle) / (total - oldTotal))).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            oldTotal = total; oldIdle = idle;
        }
        catch { }
        try
        {
            var mhz = File.ReadLines(Full("proc/cpuinfo")).Where(s => s.StartsWith("cpu MHz")).Select(s => double.Parse(s[(s.IndexOf(':') + 1)..], CultureInfo.InvariantCulture)).ToArray();
            if (mhz.Length > 0) result.mhz = mhz.Average().ToString("0", CultureInfo.InvariantCulture) + " MHz";
        }
        catch { }
        try
        {
            var readings = new List<double>();
            foreach (string hw in Directory.GetDirectories(Full("sys/class/hwmon")))
            {
                string chip = File.ReadAllText(Path.Combine(hw, "name")).Trim();
                if (chip != "coretemp" && chip != "k10temp" && chip != "zenpower") continue;
                foreach (var file in Directory.GetFiles(hw, "temp*_input")) readings.Add(double.Parse(File.ReadAllText(file).Trim(), CultureInfo.InvariantCulture) / 1000);
            }
            if (readings.Count > 0) result.temperature = readings.Max().ToString("0.0", CultureInfo.InvariantCulture) + " °C";
        }
        catch { }
        return result;
    }
}

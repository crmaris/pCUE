using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Pcue.Linux;

public static class LinuxAutoUpdate
{
    const string Manifest = "https://raw.githubusercontent.com/crmaris/powenetics-updates/main/components.json";
    public static bool Managed => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('/') == "/opt/pcue-linux";
    [DllImport("libc")] static extern uint getuid();
    public static uint UserId => getuid();
    public static async Task<string> AvailableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1048576 };
            using var doc = JsonDocument.Parse(await client.GetStringAsync(Manifest));
            var entry = doc.RootElement.GetProperty("apps").GetProperty("pcue-linux");
            string text = entry.GetProperty("version").GetString();
            return Version.TryParse(text, out var next) && Version.TryParse(Program.Version, out var current) && next > current ? text : null;
        }
        catch { return null; }
    }
    public static async Task<bool> PreparePortableAsync(string release)
    {
        string ready = Path.Combine(Program.StateFolder, "update-ready-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("/usr/bin/python3") { UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "update-linux.py"));
        start.ArgumentList.Add("--portable"); start.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd('/'));
        start.ArgumentList.Add("--installed"); start.ArgumentList.Add(Program.Version);
        start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--expected-release"); start.ArgumentList.Add(release);
        start.ArgumentList.Add("--ready"); start.ArgumentList.Add(ready);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch the Linux updater.");
        try
        {
            for (int i = 0; i < 180; i++)
            {
                if (File.Exists(ready)) return true;
                if (process.HasExited) return false;
                await Task.Delay(1000);
            }
            return false;
        }
        finally { if (File.Exists(ready)) File.Delete(ready); }
    }
}

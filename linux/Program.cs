using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using System.Reflection;
using System.Text.Json;
using pCUE;
using Pcue.Cooling;

namespace Pcue.Linux;

public static class Program
{
    public static string Version => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    public static string PreviewFolder;
    public static string RemoteHost;
    public static int RemotePort = 5056;
    public static bool Serve;
    public static string StateFolder => Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is string dir && Path.IsPathRooted(dir) ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "pcue-linux");
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            for (int i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--version": Console.WriteLine("pCUE Linux " + Version); return 0;
                    case "--help": Console.WriteLine("pCUE Linux\n  pcue-linux                     Desktop (USB stays closed until Connect)\n  --inventory                    Read-only HID inventory as JSON\n  --remote HOST [--port PORT]     Connect to another pCUE; PCUE_TOKEN supplies its token\n  --serve                        Optional loopback-only protocol-v2 API on port 5056\n  --render-preview DIRECTORY     Offline illustrative dark/light desktop renders\n  --version | --help\nUSB permissions: see LINUX.md. No acoustic acquisition leases or Windows process management on Linux."); return 0;
                    case "--inventory": Console.WriteLine(JsonSerializer.Serialize(new { platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription, devices = CoolingUsbDiscovery.Find(), commanderPro = HidSharp.DeviceList.Local.GetHidDevices(0x1b1c, 0x0c10).Count() }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true })); return 0;
                    case "--render-preview": PreviewFolder = Path.GetFullPath(args[++i]); break;
                    case "--remote": RemoteHost = args[++i]; break;
                    case "--port": RemotePort = int.Parse(args[++i]); if (RemotePort < 1 || RemotePort > 65535) throw new ArgumentException("Port must be 1–65535."); break;
                    case "--serve": Serve = true; break;
                    default: throw new ArgumentException("Unknown option. Use --help.");
                }
            if (Serve && RemoteHost != null) throw new ArgumentException("A remote client cannot also serve as a local controller.");
            if (!OperatingSystem.IsLinux() && PreviewFolder == null) throw new PlatformNotSupportedException("This package targets Linux.");
            var builder = AppBuilder.Configure<LinuxApp>();
            if (PreviewFolder != null) builder = builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
            else builder = builder.UsePlatformDetect();
            builder.StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("pCUE Linux: " + ex.Message); return 1; }
    }
}
public sealed class LinuxApp : Application
{
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(Program.PreviewFolder != null); desktop.MainWindow = window;
            if (Program.PreviewFolder != null)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        Directory.CreateDirectory(Program.PreviewFolder);
                        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Dark, Avalonia.Styling.ThemeVariant.Light })
                        foreach (int width in new[] { 1120, 860 })
                        foreach (bool native in new[] { false, true })
                        {
                            RequestedThemeVariant = theme; window.Width = width;
                            window.SetPreviewMode(native);
                            await Task.Delay(120); window.UpdateLayout();
                            using var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                            image.Render(window); image.Save(Path.Combine(Program.PreviewFolder, $"{theme.Key}-{width}-{(native ? "LINK" : "PRO")}.png"));
                            window.ValidateLayout();
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                    finally { desktop.Shutdown(Environment.ExitCode); }
                }, DispatcherPriority.Background);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

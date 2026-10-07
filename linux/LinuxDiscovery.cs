using HidSharp;
using System.Security.Cryptography;
using System.Text;

// Platform-specific discovery only; the canonical wire-protocol files stay byte-identical.
namespace Pcue.Cooling;

public sealed class BoardControlBaseline
{
    public string Id { get; set; }
    public string Mode { get; set; }
    public float Value { get; set; }
    public string ControllerId { get; set; }
}
public sealed class CoolingUsbInfo
{
    public string Id, Kind, Name, Path;
    public int Product, Outputs;
    public override string ToString() => Name;
}
public static class CoolingUsbDiscovery
{
    public static CoolingUsbInfo Describe(int vendor, int product, string path, int input, int output)
    {
        if (vendor != 0x1b1c || path == null) return null;
        if (product == 0x0c3f && LinkProtocol.ReportLength(input) && LinkProtocol.ReportLength(output) && InterfaceZero(path))
            return new() { Kind = "link", Name = "Corsair iCUE LINK System Hub", Outputs = 14, Product = product, Path = path };
        if ((product == 0x0c1c || product == 0x0c2a) && CoreProtocol.ReportLength(input) && CoreProtocol.ReportLength(output) && InterfaceZero(path))
            return new() { Kind = product == 0x0c1c ? "core" : "corext", Name = product == 0x0c1c ? "Corsair Commander CORE" : "Corsair Commander CORE XT", Outputs = product == 0x0c1c ? 7 : 6, Product = product, Path = path };
        return null;
    }
    internal static bool InterfaceZero(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var node = new DirectoryInfo("/sys/class/hidraw/" + System.IO.Path.GetFileName(path) + "/device").ResolveLinkTarget(true);
            for (var d = node as DirectoryInfo; d != null && d.FullName.StartsWith("/sys/"); d = d.Parent)
                if (File.Exists(d.FullName + "/bInterfaceNumber")) return File.ReadAllText(d.FullName + "/bInterfaceNumber").Trim() == "00";
        }
        catch { }
        return false; // Never guess which composite interface can accept writes.
    }
    internal static string StableKey(string path)
    {
        var node = new DirectoryInfo("/sys/class/hidraw/" + System.IO.Path.GetFileName(path) + "/device").ResolveLinkTarget(true) as DirectoryInfo;
        for (var d = node; d != null && d.FullName.StartsWith("/sys/"); d = d.Parent)
            if (File.Exists(d.FullName + "/bInterfaceNumber"))
            {
                var usb = d.Parent;
                string serial = File.Exists(usb.FullName + "/serial") ? File.ReadAllText(usb.FullName + "/serial").Trim() : "";
                return usb.FullName + "|" + serial + "|" + File.ReadAllText(d.FullName + "/bInterfaceNumber").Trim();
            }
        throw new IOException("The exact USB interface identity is unavailable.");
    }
    public static string Identity(string kind, string key) => kind + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    public static CoolingUsbInfo[] Find()
    {
        var result = new List<CoolingUsbInfo>();
        foreach (var d in DeviceList.Local.GetHidDevices())
        {
            try
            {
                var info = Describe(d.VendorID, d.ProductID, d.DevicePath, d.GetMaxInputReportLength(), d.GetMaxOutputReportLength());
                if (d.VendorID == 0x0c70 && d.ProductID == 0xf011 && OctoReports.SettingsLength(d.GetMaxFeatureReportLength()) && OctoReports.StatusLength(d.GetMaxInputReportLength()))
                    info = new() { Kind = "octo", Name = "Aquacomputer OCTO", Outputs = 8, Product = d.ProductID, Path = d.DevicePath };
                if (info == null) continue;
                info.Id = Identity(info.Kind, StableKey(info.Path)); result.Add(info);
            }
            catch { /* Inaccessible/unknown interfaces never become selectable outputs. */ }
        }
        return result.OrderBy(d => d.Id).ToArray();
    }
    public static INativeCoolingController Open(CoolingUsbInfo info) => info.Kind switch
    {
        "core" => new CoreController(true, new CoreHidTransport(info.Path, info.Product)),
        "corext" => new CoreController(false, new CoreHidTransport(info.Path, info.Product)),
        "link" => new LinkController(new LinkHidTransport(info.Path)),
        "octo" => new OctoCoolingController(new OctoHidTransport(info.Path)),
        _ => throw new IOException("Unsupported Linux controller.")
    };
}

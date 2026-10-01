using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HidSharp;

namespace Pcue.Cooling
{
    public sealed class CoolingUsbInfo
    {
        public string Id, Kind, Name, Path;
        public string BridgeSerial, ChildSerial;
        public int Product, Outputs;
        public override string ToString() { return Name + " · " + Id.Substring(Id.IndexOf(':') + 1, 8); }
    }
    public static class CoolingUsbDiscovery
    {
        public static string Identity(string kind, string path)
        {
            using (var hash = SHA256.Create()) return kind + ":" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }
        public static CoolingUsbInfo Describe(int vendor, int product, string path, int input, int output)
        {
            if (vendor == 0x373f && product == 0x0010 && path != null && System.Text.RegularExpressions.Regex.IsMatch(path, "&mi_00(?:[&#]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && input == 65 && output == 65)
                return new CoolingUsbInfo { Id = Identity("bequiet", path), Kind = "bequiet", Product = product, Path = path, Name = "be quiet IO cooling controller", Outputs = 2 };
            if (vendor != 0x1b1c || (product != 0x0c1c && product != 0x0c2a) || path == null || !CoreProtocol.ReportLength(input) || !CoreProtocol.ReportLength(output)) return null;
            // Composite controllers expose other USB interfaces: only interface zero
            // carries this protocol. A non-composite HID path has no MI segment.
            string lower = path.ToLowerInvariant(); int at = lower.IndexOf("&mi_");
            if (at >= 0 && (at + 6 > lower.Length || lower.Substring(at + 4, 2) != "00")) return null;
            string kind = product == 0x0c1c ? "core" : "corext";
            return new CoolingUsbInfo { Id = Identity(kind, path), Kind = kind, Product = product, Path = path, Name = product == 0x0c1c ? "Corsair Commander CORE" : "Corsair Commander CORE XT", Outputs = product == 0x0c1c ? 7 : 6 };
        }
        public static CoolingUsbInfo[] Find()
        {
            var result = new List<CoolingUsbInfo>();
            foreach (var d in DeviceList.Local.GetHidDevices().Where(d => d.VendorID == 0x1b1c || d.VendorID == 0x373f))
            {
                try { var info = Describe(d.VendorID, d.ProductID, d.DevicePath, d.GetMaxInputReportLength(), d.GetMaxOutputReportLength()); if (info != null) { if (info.Kind == "bequiet") { info.BridgeSerial = d.GetSerialNumber(); if (string.IsNullOrWhiteSpace(info.BridgeSerial)) continue; } result.Add(info); } } catch { }
            }
            return result.OrderBy(d => d.Id).ToArray();
        }
        public static INativeCoolingController Open(CoolingUsbInfo info)
        {
            if (info != null && info.Path != null && info.Kind == "bequiet" && info.Product == 0x0010 && info.Id == Identity(info.Kind, info.Path)) return new BeQuietIoController(info.Path, info.BridgeSerial, info.ChildSerial);
            if (info == null || info.Path == null || (info.Kind != "core" && info.Kind != "corext") || info.Id != Identity(info.Kind, info.Path) || info.Product != (info.Kind == "core" ? 0x0c1c : 0x0c2a)) throw new System.IO.InvalidDataException("CORE identity/product/path mismatch.");
            return new CoreController(info.Kind == "core", new CoreHidTransport(info.Path, info.Product));
        }
    }
}

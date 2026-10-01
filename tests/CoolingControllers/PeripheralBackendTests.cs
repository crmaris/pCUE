using System;
using System.IO;
using System.Linq;
using Pcue.Cooling;

static class PeripheralBackendTests
{
    static int checks;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    static void Reject(Action work) { try { work(); } catch (InvalidDataException) { checks++; return; } catch (IOException) { checks++; return; } throw new Exception("Unsafe peripheral operation accepted."); }
    public static byte[] Settings(int length)
    {
        var report = new byte[length]; new Random(71).NextBytes(report); report[0] = 3; OctoReports.Word(report, 1, 1);
        for (int i = 0; i < 8; i++) { report[OctoReports.Offset(report, i)] = (byte)(i % 3); OctoReports.Word(report, OctoReports.Offset(report, i) + 1, (30 + i) * 100); }
        OctoReports.Word(report, length - 2, OctoReports.Checksum(report)); return report;
    }
    public sealed class Octo : IOctoTransport
    {
        public byte[] Report, Telemetry = new byte[327];
        public int Writes; public bool Ignore; public Func<bool> LateGuard;
        public Octo(int length) { Report = PeripheralBackendTests.Settings(length); Telemetry[0] = 1; OctoReports.Word(Telemetry, 13, 1036); OctoReports.Word(Telemetry, 61, 32767); for (int i = 0; i < 8; i++) OctoReports.Word(Telemetry, 133 + 13 * i, 800 + i); }
        public byte[] Settings() { return (byte[])Report.Clone(); }
        public byte[] Status() { return (byte[])Telemetry.Clone(); }
        public void Send(byte[] report, Func<bool> allowed) { if (LateGuard != null) LateGuard(); if (allowed != null && !allowed()) throw new IOException("Stopped"); Writes++; if (!Ignore) Report = (byte[])report.Clone(); }
        public void Dispose() { }
    }
    public sealed class Board : IBoardControlPort
    {
        public string Id { get; set; }
        public string Name { get { return "Simulated mainboard header"; } }
        public string Mode { get; set; }
        public float SoftwareValue { get; set; }
        public float Minimum { get; set; }
        public float Maximum { get; set; }
        public float? Duty { get; set; }
        public int? Rpm { get { return 1200; } }
        public int Writes; public bool Ignore, MissingReadback;
        public Board(string id, string mode, float value) { Id = id; Mode = mode; SoftwareValue = value; Duty = value; Minimum = 0; Maximum = 100; }
        public void Update() { }
        public void Software(float value) { Writes++; if (!Ignore) { Mode = "Software"; SoftwareValue = value; Duty = MissingReadback ? null : (float?)value; } }
        public void Default() { Writes++; if (!Ignore) Mode = "Default"; }
    }
    public static void Run()
    {
        foreach (int length in new[] { 1613, 1631 }) {
            var fake = new Octo(length); var original = (byte[])fake.Report.Clone();
            using (var controller = new OctoCoolingController(fake)) {
                var state = controller.Read(); Check(state.Rpm[7] == 807 && state.Temperatures[0] == null && state.Firmware == "1036" && state.Connected.All(v => v), "OCTO telemetry fields");
                Reject(delegate { controller.Apply(new[] { 40,-1,-1,-1,-1,-1,-1,-1 }, Enumerable.Repeat(-1,8).ToArray(), delegate { return true; }); }); Check(fake.Writes == 0, "OCTO writes before capture");
                bool[] selected = { true, false, false, true, false, false, false, false };
                var baseline = controller.Capture(selected); var desired = new[] { 44,-1,-1,77,-1,-1,-1,-1 };
                controller.Apply(desired, Enumerable.Repeat(-1,8).ToArray(), delegate { return true; }); Check(fake.Writes == 1, "OCTO write count");
                for (int i = 0; i < length - 2; i++) if (!Enumerable.Range(0,8).Any(c => selected[c] && i >= OctoReports.Offset(original,c) && i < OctoReports.Offset(original,c)+3)) Check(fake.Report[i] == original[i], "Unselected OCTO byte changed");
                controller.Apply(desired, desired, delegate { return true; }); Check(fake.Writes == 1, "Unchanged OCTO tick wrote");
                byte[] corrupt = (byte[])baseline.Report.Clone(); corrupt[1000] ^= 1;
                Reject(delegate { controller.Restore(new CoreBaseline { Selected=selected, Report=corrupt }); }); Check(fake.Writes == 1, "Corrupt OCTO recovery wrote");
                var unsafeSelection = (int[])desired.Clone(); unsafeSelection[1]=50; Reject(delegate { controller.Apply(unsafeSelection,desired,delegate {return true;}); });
                fake.Report[OctoReports.Offset(fake.Report,1)] = 9; OctoReports.Word(fake.Report,fake.Report.Length-2,OctoReports.Checksum(fake.Report));
                controller.Restore(baseline); Check(fake.Report[OctoReports.Offset(fake.Report,1)] == 9 && fake.Report[OctoReports.Offset(fake.Report,3)] == baseline.Report[OctoReports.Offset(fake.Report,3)], "OCTO restore did not preserve unrelated current mode");
                Check(baseline.Report.SequenceEqual(original), "OCTO baseline mutated");
                int savedSerial = OctoReports.Word(fake.Telemetry,3); OctoReports.Word(fake.Telemetry,3,1234); int identityWrites=fake.Writes;
                Reject(delegate {controller.Restore(baseline);}); Check(fake.Writes==identityWrites,"OCTO changed serial received a recovery write"); OctoReports.Word(fake.Telemetry,3,savedSerial);
                controller.Capture(selected); bool allowed=true; fake.LateGuard=delegate {allowed=false;return false;}; int before=fake.Writes;
                Reject(delegate {controller.Apply(desired,Enumerable.Repeat(-1,8).ToArray(),delegate{return allowed;});}); Check(fake.Writes==before,"Late OCTO cancellation wrote"); fake.LateGuard=null;
                controller.Restore(baseline); controller.Capture(selected); fake.Ignore=true; before=fake.Writes;
                Reject(delegate {controller.Apply(desired,Enumerable.Repeat(-1,8).ToArray(),delegate{return true;});}); Check(fake.Writes-before==3,"OCTO retry bound"); fake.Ignore=false; controller.Restore(baseline);
            }
        }
        var software = new Board("board/fan/0", "Software", 33.25f); var bios = new Board("board/fan/2", "Undefined", 0);
        using (var controller = new MotherboardCoolingController("board:simulated", new IBoardControlPort[] {software,null,bios}, null)) {
            var state=controller.Read(); Check(state.OutputNames[0]==software.Name && state.Connected.SequenceEqual(new[]{true,false,true}),"Board sparse topology");
            Reject(delegate {controller.Capture(new[]{false,true,false});});
            var selected=new[]{true,false,true}; var baseline=controller.Capture(selected); int[] desired={45,-1,80}, previous={-1,-1,-1};
            controller.Apply(desired,previous,delegate{return true;}); Check(software.Duty==45 && bios.Duty==80,"Board percentages");
            controller.Apply(desired,desired,delegate{return true;}); Check(software.Writes==1 && bios.Writes==1,"Unchanged board tick wrote");
            software.Duty=50; int before=bios.Writes; Reject(delegate{controller.Apply(new[]{60,-1,90},desired,delegate{return true;});}); Check(bios.Writes==before,"Board conflict touched sibling"); software.Duty=45;
            baseline.Board[2].Id="other/header"; Reject(delegate{controller.Restore(baseline);}); Check(software.Writes==1,"Board invalid sibling recovery wrote first header"); baseline.Board[2].Id=bios.Id;
            controller.Restore(baseline); Check(software.SoftwareValue==33.25f && bios.Mode=="Default","Board fractional software/default recovery");
            baseline=controller.Capture(selected); bool allow=true; Reject(delegate{controller.Apply(desired,previous,delegate{bool was=allow;allow=false;return was;});}); Check(software.Duty==45 && bios.Mode=="Default","Board late sibling cancellation"); controller.Restore(baseline);
            baseline=controller.Capture(selected); bios.MissingReadback=true; Reject(delegate{controller.Apply(desired,previous,delegate{return true;});}); bios.MissingReadback=false; controller.Restore(baseline); Check(software.SoftwareValue==33.25f && bios.Mode=="Default","Board partial-write recovery");
            bios.Mode="Other"; Reject(delegate{controller.Capture(selected);}); bios.Mode="Default";
            bios.Maximum=75; before=software.Writes; controller.Capture(selected); Reject(delegate{controller.Apply(desired,previous,delegate{return true;});}); Check(software.Writes==before,"Board range validation was not atomic"); bios.Maximum=100;
            before=bios.Writes; Reject(delegate{BoardControlPolicy.Set(bios,float.NaN,delegate{return true;});}); Reject(delegate{BoardControlPolicy.Set(bios,30,delegate{return false;});}); Check(bios.Writes==before,"Invalid/cancelled board command wrote");
            var saved=BoardControlPolicy.Capture(bios,"board:simulated"); bios.Ignore=true; bios.Mode="Software"; Reject(delegate{BoardControlPolicy.Restore(bios,saved);}); bios.Ignore=false; BoardControlPolicy.Restore(bios,saved); Check(bios.Mode=="Default","Board default handback retry");
        }
        Console.WriteLine("PASS: "+checks+" shared OCTO/mainboard conformance checks (simulated; no hardware).");
    }
}

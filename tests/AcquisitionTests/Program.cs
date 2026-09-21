using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using pCUE;

internal static class Program
{
    static int failures, passed;
    static void Main()
    {
        Test("lease is idempotent only for identical parameters", () => {
            using (var f = new Fixture()) {
                var first = f.Lease(); var repeat = f.Controller.ExecuteAsync("lease", f.LeaseRequest).Result;
                Check(first.ok && repeat.ok && first.leaseToken == repeat.leaseToken);
                f.LeaseRequest.limits.maximumSetpoint = 70;
                Check(!f.Controller.ExecuteAsync("lease", f.LeaseRequest).Result.ok && f.Hardware.Writes.Count == 0);
            }
        });
        Test("invalid limits never acquire or write", () => {
            using (var f = new Fixture()) { f.LeaseRequest.limits.maximumStep = 1.5; Check(!f.Lease().ok && !f.Hardware.Owned && f.Hardware.Writes.Count == 0); }
        });
        Test("nonzero duty refuses lease", () => {
            using (var f = new Fixture()) { f.Hardware.Duty = 20; Check(!f.Lease().ok && !f.Hardware.Owned && f.Hardware.Writes.Count == 0); }
        });
        Test("rotating fan refuses lease", () => {
            using (var f = new Fixture()) { f.Sample(0, 50); Check(!f.Lease().ok && f.Hardware.Writes.Count == 0); }
        });
        Test("external ownership required", () => {
            using (var f = new Fixture()) { f.Exclusive = false; Check(!f.Lease().ok && f.Hardware.Writes.Count == 0); }
        });
        Test("wrong token cannot actuate", () => {
            using (var f = new Fixture()) { f.Lease(); var r = f.Request(); r.leaseToken = "wrong"; r.setpoint = 10; Check(!f.Controller.ExecuteAsync("target", r).Result.ok && f.Hardware.Writes.Count == 0); }
        });
        Test("caller cannot change leased limits", () => {
            using (var f = new Fixture()) { f.Lease(); f.LeaseRequest.limits.maximumSetpoint = 100; Check(!f.Target(90).ok && f.Hardware.Writes.Count == 0); }
        });
        Test("setpoint and RPM are mutually exclusive", () => {
            using (var f = new Fixture()) { f.Lease(); var r = f.Request(); r.setpoint = 10; r.rpm = 1000; Check(!f.Controller.ExecuteAsync("target", r).Result.ok && f.Hardware.Writes.Count == 0); }
        });
        Test("approach uses explicit startup and bounded steps", () => {
            using (var f = new Fixture()) { f.Lease(); Check(f.Target(30).ok); Check(f.Hardware.Duty == 10); f.Advance(300, 500); Check(f.Hardware.Duty <= 13); f.Advance(600, 600); Check(f.Hardware.Duty <= 16); Check(f.Hardware.Writes.All(v => v <= 30)); }
        });
        Test("duplicate samples cannot prove stability", () => {
            using (var f = new Fixture()) { f.Lease(); f.Target(10); f.Advance(500, 1000); Interlocked.Exchange(ref f.Clock, 5000); for (int n=0;n<5;n++) f.Renew(); Check(f.Controller.Status.phase != "Stable"); }
        });
        Test("freeze holds duty with fresh RPM supervision", () => {
            using (var f = new Fixture()) { f.Stable(); Check(f.Send("freeze").ok); int count = f.Hardware.Writes.Count; f.Advance(3000,1005); Check(f.Controller.Status.phase == "Frozen" && f.Hardware.Writes.Count == count); }
        });
        Test("frozen RPM drift revokes and attempts zero", () => {
            using (var f = new Fixture()) { f.Stable(); f.Send("freeze"); f.Advance(3000,1100); Check(f.Controller.Status.phase == "Fault" && !f.Controller.IsLeased && f.Hardware.Duty == 0); }
        });
        Test("stale sample blocks target before any startup", () => {
            using (var f = new Fixture()) { f.Lease(); f.Sample(100,0, age:2000); Check(!f.Target(10).ok && f.Hardware.Writes.All(v => v == 0) && !f.Controller.IsLeased); }
        });
        Test("session change revokes before target", () => {
            using (var f = new Fixture()) { f.Lease(); f.Session = "replacement"; f.Sample(100,0); Check(!f.Target(10).ok && f.Hardware.Writes.All(v => v == 0)); }
        });
        Test("battery low revokes", () => {
            using (var f = new Fixture()) { f.Lease(); f.Sample(100,0,battery:true); f.Renew(); Check(!f.Controller.IsLeased && f.Hardware.Duty == 0); }
        });
        Test("changed tach assignment revokes", () => {
            using (var f = new Fixture()) { f.Lease(); f.Context = false; f.Renew(); Check(!f.Controller.IsLeased && f.Hardware.Duty == 0); }
        });
        Test("lease expiry uses monotonic clock and zero policy", () => {
            using (var f = new Fixture()) { f.Lease(); f.Target(10); f.Sample(121000,1000); Check(!f.Renew().ok && f.Controller.Status.phase == "Expired" && f.Hardware.Duty == 0 && !f.Hardware.Owned); }
        });
        Test("ambient is never inferred from PWM zero", () => {
            using (var f = new Fixture()) { f.Lease(); f.Advance(500,0); f.Advance(1500,0); Check(!f.Controller.Status.ambientConfirmed); var json = new JavaScriptSerializer().Serialize(f.Controller.Status); Check(json.Contains("\"confirmedOutputOff\":false") && json.Contains("\"outputOn\":null")); }
        });
        Test("ambient confirmation needs stable zero and records provenance", () => {
            using (var f = new Fixture()) { f.Lease(); Check(!f.Confirm().ok); f.Advance(500,0); f.Advance(1500,0); Check(f.Confirm().ok && f.Controller.Status.ambientConfirmed); Check(f.Controller.Status.ambientConfirmation.basis == "operator"); f.Target(10); Check(!f.Controller.Status.ambientConfirmed && f.Controller.Status.ambientConfirmation == null); }
        });
        Test("ambient confirmation rejects control characters", () => {
            using (var f = new Fixture()) { f.Lease(); f.Advance(500,0); f.Advance(1500,0); var r=f.Request(); r.operatorName="Name\nSpoof"; r.reason="Physically off"; Check(!f.Controller.ExecuteAsync("confirm-ambient",r).Result.ok); }
        });
        Test("readback failure leaves a visible fault", () => {
            using (var f = new Fixture()) { f.Lease(); f.Target(10); f.Hardware.RejectWrites=true; f.Sample(121000,1000); f.Renew(); Check(f.Controller.Status.phase == "Fault" && f.Controller.Status.fault.Contains("physical fixture") && !f.Controller.IsLeased); }
        });
        Test("release failure is a rejected acknowledgement", () => {
            using (var f = new Fixture()) { f.Lease(); f.Target(10); f.Hardware.RejectWrites=true; var result=f.Send("release"); Check(!result.ok && result.status.phase=="Fault" && !f.Hardware.Owned); }
        });
        Test("output-off failure revokes instead of acknowledging success", () => {
            using (var f = new Fixture()) { f.Lease(); f.Target(10); f.Hardware.RejectWrites=true; var result=f.Send("output-off"); Check(!result.ok && result.status.phase=="Fault" && !f.Hardware.Owned); }
        });
        Test("blocking readback cannot authorize a post-expiry startup", () => {
            using (var f = new Fixture()) { f.Lease(); f.Hardware.OnRead=()=>Interlocked.Exchange(ref f.Clock,121000); var result=f.Target(10); Check(!result.ok && result.status.phase=="Expired" && f.Hardware.Writes.All(value=>value==0)); }
        });
        Test("dispose finishes the final zero action before returning", () => {
            var f=new Fixture(); f.Lease(); f.Target(10); f.Controller.Dispose(); Check(!f.Hardware.Owned && f.Hardware.Duty==0 && f.Controller.Status.phase=="Released");
        });
        Test("mutex ownership stays exclusive and releases across threads", () => {
            string name = "Local\\pCUE.AcquisitionTests." + Guid.NewGuid().ToString("N");
            var held = BenchTachometerOwnership.Acquire(name); bool refused=false;
            try { using (BenchTachometerOwnership.Acquire(name)) { } } catch(InvalidOperationException) { refused=true; }
            Check(refused); Task.Run(() => held.Dispose()).Wait(); using (BenchTachometerOwnership.Acquire(name)) { }
        });
        Console.WriteLine($"Acquisition checks: {passed} passed, {failures} failed (fake hardware only)."); Environment.ExitCode = failures == 0 ? 0 : 1;
    }
    static void Test(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch(Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); } }
    static void Check(bool condition) { if(!condition) throw new InvalidOperationException("Assertion failed."); }
    sealed class Fixture : IDisposable
    {
        public long Clock; public bool Exclusive=true, Context=true; public string Session="session-one"; long sequence;
        AcquisitionRpm sample; string token;
        public readonly FakeHardware Hardware=new FakeHardware();
        public readonly PwmAcquisitionController Controller;
        public readonly PwmAcquisitionRequest LeaseRequest=new PwmAcquisitionRequest {
            operationId=Guid.NewGuid().ToString("D"), owner="offline tests",channel=2,leaseSeconds=120,
            limits=new PwmAcquisitionLimits { minimumSetpoint=10,maximumSetpoint=60,startSetpoint=10,maximumStep=5,maximumSlewPerSecond=10,
                settleMilliseconds=200,stabilityMilliseconds=1000,maximumSampleAgeMilliseconds=1500,approachTimeoutSeconds=30,maximumRpm=3000 }
        };
        public Fixture() { Sample(0,0); Controller=new PwmAcquisitionController(Hardware,()=>Volatile.Read(ref sample),c=>Context&&c==2,()=>Exclusive,()=>2,()=>Interlocked.Read(ref Clock),1000); }
        public void Sample(long time,double rpm,double age=0,bool battery=false) { Interlocked.Exchange(ref Clock,time); Volatile.Write(ref sample,new AcquisitionRpm { value=rpm,sampleSessionId=Session,sampleSequence=Interlocked.Increment(ref sequence),sampleAgeMs=age,batteryLow=battery,sampleUtc=DateTime.UtcNow.ToString("O") }); }
        public PwmAcquisitionResponse Lease() { var r=Controller.ExecuteAsync("lease",LeaseRequest).Result; token=r.leaseToken; return r; }
        public PwmAcquisitionRequest Request() { return new PwmAcquisitionRequest { operationId=LeaseRequest.operationId,leaseToken=token,leaseSeconds=120 }; }
        public PwmAcquisitionResponse Send(string action) { return Controller.ExecuteAsync(action,Request()).Result; }
        public PwmAcquisitionResponse Renew() { return Send("renew"); }
        public void Advance(long time,double rpm) { Sample(time,rpm); Renew(); }
        public PwmAcquisitionResponse Target(double duty) { var r=Request();r.setpoint=duty;return Controller.ExecuteAsync("target",r).Result; }
        public PwmAcquisitionResponse Confirm() { var r=Request();r.operatorName="Bench operator";r.reason="DUT physically powered off at fixture";return Controller.ExecuteAsync("confirm-ambient",r).Result; }
        public void Stable() { Check(Lease().ok);Check(Target(10).ok);Advance(500,1000);Advance(1000,1000);Advance(1600,1000);Check(Controller.Status.phase=="Stable"); }
        public void Dispose() { Controller.Dispose(); }
    }
    sealed class FakeHardware : IPwmAcquisitionHardware
    {
        public bool IsConnected { get; set; }=true; public bool Owned {get;private set;} public int Duty; public bool RejectWrites;
        public readonly List<int> Writes=new List<int>(); object owner; public Action OnRead;
        public bool TryAcquireAcquisition(object value) { if(Owned)return false;owner=value;Owned=true;return true; }
        public void ReleaseAcquisition(object value) { if(ReferenceEquals(owner,value)) {Owned=false;owner=null;} }
        public int? ReadAcquisitionPower(int channel) { OnRead?.Invoke(); return Duty; }
        public bool WriteAcquisitionPower(object value,int channel,int duty) { if(!ReferenceEquals(owner,value)||RejectWrites)return false;Writes.Add(duty);Duty=duty;return true; }
    }
}

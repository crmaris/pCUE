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
        Test("raw Commander RPM validates status and length while genuine zero stays valid", () => {
            int rpm; Check(CommanderAcquisitionFrames.TryReadRpm(new byte[]{0,0,0,0},4,out rpm)&&rpm==0);
            Check(CommanderAcquisitionFrames.TryReadRpm(new byte[]{0,0,3,232},4,out rpm)&&rpm==1000);
            Check(!CommanderAcquisitionFrames.TryReadRpm(new byte[]{0,1,0,0},4,out rpm));
            Check(!CommanderAcquisitionFrames.TryReadRpm(new byte[]{0,0,0,0},3,out rpm));
            Check(!CommanderAcquisitionFrames.TryReadRpm(new byte[]{0,0,0},4,out rpm));
        });
        Test("raw Commander mode validates the complete fan mask", () => {
            byte[] frame={0,0,1,2,0,0,0,0}; Check(CommanderAcquisitionFrames.ReadDriveMode(frame,8,0)=="dc-percent");
            Check(CommanderAcquisitionFrames.ReadDriveMode(frame,8,1)=="pwm"); Check(CommanderAcquisitionFrames.ReadDriveMode(frame,8,2)==null);
            Check(CommanderAcquisitionFrames.ReadDriveMode(frame,7,1)==null); frame[1]=1; Check(CommanderAcquisitionFrames.ReadDriveMode(frame,8,1)==null);
        });
        Test("four-pin acquisition works without an external tachometer", () => {
            using(var f=new Fixture()) { f.Exclusive=false; f.FailExternal=true; f.Stable(); Check(f.Send("freeze").ok);
                Check(f.Controller.Status.rpm.source=="commander-internal" && f.Controller.Status.rpm.batteryLow==null && f.ExternalReads==0 && f.Hardware.InternalReads>0); }
        });
        Test("three-pin acquisition never substitutes Commander RPM for external feedback", () => {
            using(var f=new Fixture()) { f.ConfiguredMode=f.Hardware.DriveMode="dc-percent"; f.LeaseRequest.driveMode="dc-percent";
                f.Hardware.InternalSample=()=>throw new InvalidOperationException("Internal feedback must not be used"); f.Stable();
                Check(f.Send("freeze").ok && f.Controller.Status.rpm.source=="external-hid" && f.Hardware.InternalReads==0); }
        });
        Test("undefined internal RPM is never zero-RPM lease evidence", () => {
            using(var f=new Fixture()) { f.Hardware.InternalSample=()=>new AcquisitionRpm { source="commander-internal" };
                Check(!f.Lease().ok && !f.Hardware.Owned && f.Hardware.Writes.Count==0); }
        });
        Test("channel preflight reads without writes and cannot retarget an active lease", () => {
            using(var f=new Fixture()) { f.Exclusive=false; var idle=f.Controller.ReadStatusAsync(2).Result;
                Check(idle.channel==2 && idle.driveMode=="pwm" && idle.rpm.source=="commander-internal" && f.Hardware.Writes.Count==0);
                f.Lease(); var active=f.Controller.ReadStatusAsync(3).Result; Check(active.channel==2 && f.Controller.IsLeased && f.Hardware.Writes.Count==0); }
        });
        Test("unmatched configured and detected mode cannot report a ready preflight", () => {
            using(var f=new Fixture()) { f.Hardware.DriveMode="dc-percent"; var status=f.Controller.ReadStatusAsync(2).Result;
                Check(status.driveMode==null && new JavaScriptSerializer().Serialize(status).Contains("\"exclusiveFeedbackOwnership\":false") && f.Hardware.Writes.Count==0); }
        });
        Test("internal feedback source changes fault rather than fall back", () => {
            using(var f=new Fixture()) { f.Stable(); f.Hardware.InternalSample=()=>new AcquisitionRpm { source="external-hid",value=1000,sampleSessionId=f.Session,sampleSequence=100,sampleAgeMs=0,batteryLow=false };
                f.Renew(); Check(!f.Controller.IsLeased && f.Hardware.Duty==0 && f.Controller.Status.phase=="Fault"); }
        });
        Test("feedback aging during final mode inspection prevents energizing", () => {
            using(var f=new Fixture()) { f.Lease(); f.Hardware.BeforeWriteModeCheck=()=>Interlocked.Exchange(ref f.Clock,2000);
                Check(!f.Target(10).ok && f.Hardware.Writes.All(v=>v==0)); }
        });
        Test("three-pin DC percent approaches and freezes with truthful mode", () => {
            using (var f = new Fixture()) {
                f.ConfiguredMode=f.Hardware.DriveMode="dc-percent"; f.LeaseRequest.driveMode="dc-percent";
                f.Stable(); Check(f.Send("freeze").ok); f.Advance(3000,1000);
                var json=new JavaScriptSerializer().Serialize(f.Controller.Status);
                Check(f.Controller.Status.driveMode=="dc-percent" && f.Controller.Status.phase=="Frozen" &&
                    json.Contains("\"unit\":\"percent\"") && json.Contains("\"explicitDriveMode\":true") &&
                    json.Contains("\"confirmedOutputOff\":false"));
                Check(f.Send("release").ok && f.Hardware.Duty==0);
            }
        });
        Test("three-pin RPM target is software percent control", () => {
            using(var f=new Fixture()) {
                f.ConfiguredMode=f.Hardware.DriveMode="dc-percent"; f.LeaseRequest.driveMode="dc-percent"; Check(f.Lease().ok);
                var request=f.Request(); request.rpm=1000; Check(f.Controller.ExecuteAsync("target",request).Result.ok);
                f.Advance(500,900); Check(f.Hardware.Writes.Count>0 && f.Hardware.Writes.All(v=>v>=10&&v<=60));
            }
        });
        Test("four-pin RPM target uses Commander fixed RPM and freezes without percent writes", () => {
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100; Check(f.Lease().ok);
                var request=f.Request(); request.rpm=1000;
                Check(f.Controller.ExecuteAsync("target",request).Result.ok);
                Check(f.Hardware.RpmWrites.SequenceEqual(new[]{1000}) && f.Hardware.Writes.Count==0);
                var starting=new JavaScriptSerializer().Serialize(f.Controller.Status);
                Check(starting.Contains("\"hardwareFixedRpm\":true") && starting.Contains("\"controlMode\":\"hardware-rpm\"") &&
                    starting.Contains("\"commandedValue\":1000") && starting.Contains("\"unit\":\"rpm\""));
                long t=0; for(int i=0;i<12 && f.Controller.Status.phase!="Stable";i++) {t+=500;f.Advance(t,1000);f.WaitPhase("Stable",100);}
                Check(f.Controller.Status.phase=="Stable"); Check(f.Send("freeze").ok);
                f.Advance(t+500,1000);
                var frozen=new JavaScriptSerializer().Serialize(f.Controller.Status);
                Check(f.Controller.Status.phase=="Frozen" && frozen.Contains("\"commandedValue\":1000") &&
                    frozen.Contains("\"controlMode\":\"hardware-rpm\"") && f.Hardware.Writes.Count==0);
                Check(f.Send("output-off").ok && f.Hardware.Duty==0 && f.Hardware.Writes.Last()==0);
                var stopped=new JavaScriptSerializer().Serialize(f.Controller.Status);
                Check(stopped.Contains("\"controlMode\":\"percent\"") && stopped.Contains("\"commandedValue\":0") && stopped.Contains("\"outputOn\":null"));
            }
        });
        Test("fixed RPM remains supervised when percent power readback is unavailable", () => {
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100;
                f.Hardware.InvalidPowerDuringRpm=true; f.Hardware.InvalidPowerValue=null;
                Check(f.Lease().ok);
                var request=f.Request(); request.rpm=1000;
                Check(f.Controller.ExecuteAsync("target",request).Result.ok);
                long t=0;
                for(int i=0;i<12 && f.Controller.Status.phase!="Stable";i++) {
                    t+=500; f.Advance(t,1000); f.WaitPhase("Stable",100);
                }
                Check(f.Controller.Status.phase=="Stable" && f.Hardware.PowerReadsDuringRpm>0);
                Check(f.Send("freeze").ok);
                f.Advance(t+500,1000);
                Check(f.Controller.Status.phase=="Frozen" && f.Hardware.RpmWrites.SequenceEqual(new[]{1000}));
                Check(f.Send("output-off").ok && f.Hardware.Duty==0 && !f.Hardware.RpmControl);
            }
        });
        Test("fixed RPM stability starts at the first in-target sample", () => {
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100;
                f.LeaseRequest.limits.rpmTolerance=50;
                Check(f.Lease().ok);
                var request=f.Request(); request.rpm=1400;
                Check(f.Controller.ExecuteAsync("target",request).Result.ok);
                f.Advance(500,1458);
                Check(f.Controller.Status.phase=="Approaching");
                f.Advance(1000,1409);
                f.Advance(1500,1409);
                f.Advance(2100,1409);
                Check(f.Controller.Status.phase=="Stable");
                f.Advance(2600,1406);
                Check(f.Controller.Status.phase=="Stable");
                f.Advance(3100,1470);
                Check(f.Controller.Status.phase=="Approaching");
                Check(f.Send("output-off").ok);
            }
        });
        Test("fixed RPM rejects a present duty beyond the declared envelope", () => {
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100;
                f.Hardware.InvalidPowerDuringRpm=true; f.Hardware.InvalidPowerValue=101;
                Check(f.Lease().ok);
                var request=f.Request(); request.rpm=1000;
                f.Controller.ExecuteAsync("target",request).Wait();
                Check(f.Controller.Status.phase=="Fault" && !f.Controller.IsLeased && f.Hardware.Duty==0);
            }
        });
        Test("unavailable power readback after fixed RPM zero is a fault", () => {
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100; Check(f.Lease().ok);
                var request=f.Request(); request.rpm=1000;
                Check(f.Controller.ExecuteAsync("target",request).Result.ok);
                f.Hardware.PowerRead=()=>null;
                var result=f.Send("output-off");
                Check(!result.ok && result.status.phase=="Fault" && !f.Controller.IsLeased);
            }
        });
        Test("percent mode still faults on unavailable power readback", () => {
            using(var f=new Fixture()) {
                Check(f.Lease().ok && f.Target(10).ok);
                f.Hardware.PowerRead=()=>null;
                f.Renew();
                Check(f.Controller.Status.phase=="Fault" && !f.Controller.IsLeased);
            }
        });
        Test("fixed RPM validates duty envelope and integer target before any write", () => {
            using(var f=new Fixture()) {
                Check(f.Lease().ok); var r=f.Request(); r.rpm=1000;
                Check(!f.Controller.ExecuteAsync("target",r).Result.ok && f.Hardware.RpmWrites.Count==0);
            }
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100; Check(f.Lease().ok);
                var r=f.Request(); r.rpm=1000.5;
                Check(!f.Controller.ExecuteAsync("target",r).Result.ok && f.Hardware.RpmWrites.Count==0);
            }
        });
        Test("uncertain fixed RPM acknowledgement zeroes output and retains lease for release", () => {
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100; Check(f.Lease().ok);
                f.Hardware.RejectRpmWrites=true; var r=f.Request(); r.rpm=1000;
                Check(!f.Controller.ExecuteAsync("target",r).Result.ok && f.Controller.IsLeased &&
                    f.Controller.Status.phase=="Off" && f.Hardware.Writes.Last()==0);
                Check(f.Send("release").ok && !f.Controller.IsLeased);
            }
        });
        Test("fixed RPM can retarget and switch to explicit percent through verified zero", () => {
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100; Check(f.Lease().ok);
                var r=f.Request(); r.rpm=900; Check(f.Controller.ExecuteAsync("target",r).Result.ok);
                r.rpm=950; Check(f.Controller.ExecuteAsync("target",r).Result.ok);
                Check(f.Hardware.RpmWrites.SequenceEqual(new[]{900,950}));
                Check(f.Target(20).ok && f.Hardware.Writes.Count>=2 && f.Hardware.Writes[0]==0 &&
                    f.Hardware.Writes[1]==f.LeaseRequest.limits.startSetpoint && f.Controller.Status.targetRpm==null);
                var status=new JavaScriptSerializer().Serialize(f.Controller.Status);
                Check(status.Contains("\"controlMode\":\"percent\"") && status.Contains("\"unit\":\"percent\""));
            }
        });
        Test("fixed RPM final mode fence refuses a changed drive mode", () => {
            using(var f=new Fixture()) {
                f.LeaseRequest.limits.maximumSetpoint=100; Check(f.Lease().ok);
                f.Hardware.BeforeWriteModeCheck=()=>f.Hardware.DriveMode="dc-percent";
                var r=f.Request(); r.rpm=900;
                Check(!f.Controller.ExecuteAsync("target",r).Result.ok && f.Hardware.RpmWrites.Count==0 && f.Hardware.Writes.Last()==0);
            }
        });
        Test("three-pin RPM retains software duty loop without hardware RPM", () => {
            using(var f=new Fixture()) {
                f.ConfiguredMode=f.Hardware.DriveMode="dc-percent"; f.LeaseRequest.driveMode="dc-percent";
                Check(f.Lease().ok); var r=f.Request(); r.rpm=900;
                Check(f.Controller.ExecuteAsync("target",r).Result.ok && f.Hardware.Writes.Count>0 && f.Hardware.RpmWrites.Count==0);
            }
        });
        Test("omitted mode retains four-pin-only lease contract", () => {
            using(var f=new Fixture()) { Check(f.Lease().ok && f.Controller.Status.driveMode=="pwm"); }
            using(var f=new Fixture()) { f.ConfiguredMode=f.Hardware.DriveMode="dc-percent"; Check(!f.Lease().ok && f.Hardware.Writes.Count==0); }
        });
        Test("requested mode must match both configured and detected type", () => {
            using(var f=new Fixture()) { f.LeaseRequest.driveMode="dc-percent"; Check(!f.Lease().ok && f.Hardware.Writes.Count==0); }
            using(var f=new Fixture()) { f.ConfiguredMode="dc-percent"; f.LeaseRequest.driveMode="dc-percent"; Check(!f.Lease().ok && !f.Hardware.Owned && f.Hardware.Writes.Count==0); }
            using(var f=new Fixture()) { f.Hardware.DriveMode="dc-percent"; Check(!f.Lease().ok && !f.Hardware.Owned && f.Hardware.Writes.Count==0); }
        });
        Test("auto unknown and invalid mode cannot acquire", () => {
            using(var f=new Fixture()) { f.ConfiguredMode=null; Check(!f.Lease().ok && f.Hardware.Writes.Count==0); }
            using(var f=new Fixture()) { f.Hardware.DriveMode=null; Check(!f.Lease().ok && f.Hardware.Writes.Count==0); }
            using(var f=new Fixture()) { f.LeaseRequest.driveMode="auto"; Check(!f.Lease().ok && f.Hardware.Writes.Count==0); }
        });
        Test("changing lease drive mode is not an idempotent retry", () => {
            using(var f=new Fixture()) { Check(f.Lease().ok); f.LeaseRequest.driveMode="dc-percent"; Check(!f.Lease().ok && f.Hardware.Writes.Count==0); }
        });
        Test("detected mode change while frozen faults and still writes zero", () => {
            using(var f=new Fixture()) { f.Stable(); Check(f.Send("freeze").ok); f.Hardware.DriveMode="dc-percent"; f.Renew(); Check(!f.Controller.IsLeased && f.Controller.Status.phase=="Fault" && f.Hardware.Duty==0); }
        });
        Test("configuration change before startup prevents energizing", () => {
            using(var f=new Fixture()) { f.Lease(); f.ConfiguredMode="dc-percent"; Check(!f.Target(10).ok && f.Hardware.Writes.All(v=>v==0)); }
        });
        Test("mode changes at the final hardware fence prevent a nonzero write", () => {
            using(var f=new Fixture()) { f.Lease(); f.Hardware.BeforeWriteModeCheck=()=>f.Hardware.DriveMode="dc-percent"; Check(!f.Target(10).ok && f.Hardware.Writes.All(v=>v==0) && !f.Controller.IsLeased); }
        });
        Test("lease expiry during final mode read prevents a nonzero write", () => {
            using(var f=new Fixture()) { f.Lease(); f.Hardware.BeforeWriteModeCheck=()=>Interlocked.Exchange(ref f.Clock,121000); Check(!f.Target(10).ok && f.Hardware.Writes.All(v=>v==0) && !f.Controller.IsLeased); }
        });
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
            using (var f = new Fixture()) { f.ConfiguredMode=f.Hardware.DriveMode="dc-percent"; f.LeaseRequest.driveMode="dc-percent"; f.Exclusive = false; Check(!f.Lease().ok && f.Hardware.Writes.Count == 0); }
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
            using (var f = new Fixture()) { f.Stable(); Check(f.Send("freeze").ok); int count = f.Hardware.Writes.Count; f.Advance(3000,1005); Check(f.WaitPhase("Frozen") && f.Hardware.Writes.Count == count); }
        });
        Test("frozen RPM drift revokes and attempts zero", () => {
            using (var f = new Fixture()) { f.Stable(); f.Send("freeze"); f.Advance(3000,1100); Check(f.WaitPhase("Fault") && !f.Controller.IsLeased && f.Hardware.Duty == 0); }
        });
        Test("stale sample blocks target before any startup", () => {
            using (var f = new Fixture()) { f.Lease(); f.Sample(100,0, age:2000); Check(!f.Target(10).ok && f.Hardware.Writes.All(v => v == 0) && !f.Controller.IsLeased); }
        });
        Test("session change revokes before target", () => {
            using (var f = new Fixture()) { f.Lease(); f.Session = "replacement"; f.Sample(100,0); Check(!f.Target(10).ok && f.Hardware.Writes.All(v => v == 0)); }
        });
        Test("battery low revokes", () => {
            using (var f = new Fixture()) { f.ConfiguredMode=f.Hardware.DriveMode="dc-percent"; f.LeaseRequest.driveMode="dc-percent"; f.Lease(); f.Sample(100,0,battery:true); f.Renew(); Check(!f.Controller.IsLeased && f.Hardware.Duty == 0); }
        });
        Test("changed tach assignment revokes", () => {
            using (var f = new Fixture()) { f.Lease(); f.Context = false; f.Renew(); Check(!f.Controller.IsLeased && f.Hardware.Duty == 0); }
        });
        Test("lease expiry uses monotonic clock and zero policy", () => {
            using (var f = new Fixture()) { f.Lease(); f.Target(10); f.Sample(121000,1000);
                var r = f.Renew();
                Check(!r.ok, "expired renew refused (ok=" + r.ok + ")");
                Check(f.WaitPhase("Expired", 2000), "phase Expired (phase=" + f.Controller.Status.phase + ")");
                Check(f.Hardware.Duty == 0, "duty zero (duty=" + f.Hardware.Duty + ")");
                Check(!f.Hardware.Owned, "ownership released"); }
        });
        Test("ambient is never inferred from PWM zero", () => {
            using (var f = new Fixture()) { Check(new JavaScriptSerializer().Serialize(f.Controller.Status).Contains("\"stoppedPwmAmbient\":false")); f.Lease(); f.Advance(500,0); f.Advance(1500,0); Check(!f.Controller.Status.ambientConfirmed); var json = new JavaScriptSerializer().Serialize(f.Controller.Status); Check(json.Contains("\"confirmedOutputOff\":false") && json.Contains("\"outputOn\":null") && json.Contains("\"stoppedPwmAmbient\":true")); }
        });
        Test("ambient confirmation needs stable zero and records provenance", () => {
            using (var f = new Fixture()) { f.Lease(); Check(!f.Confirm().ok); f.Advance(500,0); f.Advance(1500,0); Check(f.Confirm().ok && f.Controller.Status.ambientConfirmed); Check(f.Controller.Status.ambientConfirmation.basis == "operator"); Check(new JavaScriptSerializer().Serialize(f.Controller.Status).Contains("\"outputOn\":false")); f.Target(10); Check(!f.Controller.Status.ambientConfirmed && f.Controller.Status.ambientConfirmation == null); }
        });
        Test("explicit stopped PWM ambient requires stable zero and keeps electrical state unknown", () => {
            using (var f = new Fixture()) {
                Check(f.Lease().ok);
                Check(!f.ConfirmStoppedPwm().ok); // one fresh zero is insufficient
                f.Advance(500,0); Check(!f.ConfirmStoppedPwm().ok); // stability span is insufficient
                f.Advance(1500,0); Check(f.ConfirmStoppedPwm().ok);
                var status = f.Controller.Status;
                var json = new JavaScriptSerializer().Serialize(status);
                Check(status.ambientConfirmed && status.ambientConfirmation.basis == "stopped-pwm-energized");
                Check(json.Contains("\"commandedValue\":0") && json.Contains("\"outputOn\":null") &&
                    json.Contains("\"confirmedOutputOff\":false") && json.Contains("\"stoppedPwmAmbient\":true"));
                Check(f.Target(10).ok && !f.Controller.Status.ambientConfirmed);
            }
        });
        Test("stopped PWM confirmation rejects unknown basis and three-pin drive", () => {
            using (var f = new Fixture()) {
                f.Lease(); f.Advance(500,0); f.Advance(1500,0);
                var request=f.Request(); request.basis="energized"; request.operatorName="Bench operator"; request.reason="Fan stopped";
                Check(!f.Controller.ExecuteAsync("confirm-ambient",request).Result.ok && !f.Controller.Status.ambientConfirmed);
            }
            using (var f = new Fixture()) {
                f.ConfiguredMode=f.Hardware.DriveMode="dc-percent"; f.LeaseRequest.driveMode="dc-percent";
                f.Lease(); f.Advance(500,0); f.Advance(1500,0);
                Check(new JavaScriptSerializer().Serialize(f.Controller.Status).Contains("\"stoppedPwmAmbient\":false"));
                Check(!f.ConfirmStoppedPwm().ok && !f.Controller.Status.ambientConfirmed);
            }
        });
        Test("stopped PWM confirmation needs a fresh zero-duty readback", () => {
            using (var f = new Fixture()) {
                f.Lease(); f.Advance(500,0); f.Advance(1500,0);
                int reads=0; f.Hardware.PowerRead=()=>++reads == 1 ? 0 : (int?)null;
                Check(!f.ConfirmStoppedPwm().ok && !f.Controller.Status.ambientConfirmed);
            }
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
    static void Check(bool condition, string message) { if(!condition) throw new InvalidOperationException("Assertion failed: " + message); }
    sealed class Fixture : IDisposable
    {
        public long Clock; public bool Exclusive=true, Context=true, FailExternal; public int ExternalReads; public string Session="session-one", ConfiguredMode="pwm"; long sequence;
        AcquisitionRpm sample; string token;
        public readonly FakeHardware Hardware=new FakeHardware();
        public readonly PwmAcquisitionController Controller;
        public readonly PwmAcquisitionRequest LeaseRequest=new PwmAcquisitionRequest {
            operationId=Guid.NewGuid().ToString("D"), owner="offline tests",channel=2,leaseSeconds=120,
            limits=new PwmAcquisitionLimits { minimumSetpoint=10,maximumSetpoint=60,startSetpoint=10,maximumStep=5,maximumSlewPerSecond=10,
                settleMilliseconds=200,stabilityMilliseconds=1000,maximumSampleAgeMilliseconds=1500,approachTimeoutSeconds=30,maximumRpm=3000 }
        };
        public Fixture() {
            Sample(0,0);
            Hardware.InternalSample=()=> {
                var s=Volatile.Read(ref sample);
                return new AcquisitionRpm { value=s.value, source="commander-internal", sampleSessionId=s.sampleSessionId,
                    sampleSequence=s.sampleSequence,sampleUtc=s.sampleUtc,sampleAgeMs=s.sampleAgeMs,batteryLow=null };
            };
            Controller=new PwmAcquisitionController(Hardware,()=> { Interlocked.Increment(ref ExternalReads); if(FailExternal)throw new InvalidOperationException("External tachometer unavailable"); return Volatile.Read(ref sample); },c=>Context&&c==2,()=>Exclusive,()=>2,()=>Interlocked.Read(ref Clock),1000,c=>ConfiguredMode);
        }
        public void Sample(long time,double rpm,double age=0,bool battery=false) { Interlocked.Exchange(ref Clock,time); Volatile.Write(ref sample,new AcquisitionRpm { value=rpm,sampleSessionId=Session,sampleSequence=Interlocked.Increment(ref sequence),sampleAgeMs=age,batteryLow=battery,sampleUtc=DateTime.UtcNow.ToString("O") }); }
        public PwmAcquisitionResponse Lease() { var r=Controller.ExecuteAsync("lease",LeaseRequest).Result; token=r.leaseToken; return r; }
        public PwmAcquisitionRequest Request() { return new PwmAcquisitionRequest { operationId=LeaseRequest.operationId,leaseToken=token,leaseSeconds=120 }; }
        public PwmAcquisitionResponse Send(string action) { return Controller.ExecuteAsync(action,Request()).Result; }
        public PwmAcquisitionResponse Renew() { return Send("renew"); }
        public void Advance(long time,double rpm) { Sample(time,rpm); Renew(); }
        public PwmAcquisitionResponse Target(double duty) { var r=Request();r.setpoint=duty;return Controller.ExecuteAsync("target",r).Result; }
        public PwmAcquisitionResponse Confirm() { var r=Request();r.operatorName="Bench operator";r.reason="DUT physically powered off at fixture";return Controller.ExecuteAsync("confirm-ambient",r).Result; }
        public PwmAcquisitionResponse ConfirmStoppedPwm() { var r=Request();r.basis="stopped-pwm-energized";r.operatorName="Bench operator";r.reason="Owner approved energized stopped-PWM ambient";return Controller.ExecuteAsync("confirm-ambient",r).Result; }
        public void Stable() {
            Check(Lease().ok);Check(Target(10).ok);
            // Convergence loop, not a fixed script: the worker's background pass (every ~100 ms
            // real time) may observe a fresh sample sequence before the Renew queued for it runs,
            // consuming that sequence so the Renew's evaluation skips counting. Each extra Advance
            // supplies a new sequence; three counted evaluations promote to Stable.
            long t = 0;
            for (int i = 0; i < 12; i++) {
                t += 500; Advance(t, 1000);
                if (WaitPhase("Stable", 1000)) return;
            }
            try {
                var st = Controller.Status;
                Console.Error.WriteLine("STABLE-DIAG phase=" + st.phase + " fault=" + st.fault +
                    " leased=" + Controller.IsLeased + " clock=" + Interlocked.Read(ref Clock) +
                    " writes=" + string.Join(",", Hardware.Writes) + " duty=" + Hardware.Duty +
                    " extReads=" + ExternalReads);
            } catch (Exception ex) { Console.Error.WriteLine("STABLE-DIAG failed: " + ex.Message); }
            Check(false);
        }
        // Worker passes run async to Renew(): wait for a phase instead of racing it. Terminal
        // phases fail fast (a Fault never becomes Stable); otherwise allow up to ~10 s for a
        // stall - a freshly-built exe can sit in AV scan with its worker frozen on first run.
        // Worker passes run async to Renew(): wait for a phase instead of racing it. Terminal
        // phases fail fast (a Fault never becomes Stable); otherwise allow a stall budget - a
        // freshly-built exe can sit in AV scan with its worker frozen on first run.
        public bool WaitPhase(string phase, int timeoutMs = 10000) { int n = timeoutMs / 50; for(int i=0;i<n;i++) { string p=Controller.Status.phase; if(p==phase) return true; if(p=="Fault"||p=="Expired"||p=="Released") return false; Thread.Sleep(50); } return Controller.Status.phase==phase; }
        public void Dispose() { Controller.Dispose(); }
    }
    sealed class FakeHardware : IPwmAcquisitionHardware
    {
        public bool IsConnected { get; set; }=true; public bool Owned {get;private set;} public int Duty; public bool RejectWrites, RejectRpmWrites;
        public bool RpmControl, InvalidPowerDuringRpm; public int? InvalidPowerValue; public int PowerReadsDuringRpm;
        public readonly List<int> Writes=new List<int>(), RpmWrites=new List<int>(); object owner; public Action OnRead, BeforeWriteModeCheck; public string DriveMode="pwm";
        public Func<AcquisitionRpm> InternalSample; public Func<int?> PowerRead; public int InternalReads;
        public bool TryAcquireAcquisition(object value) { if(Owned)return false;owner=value;Owned=true;return true; }
        public void ReleaseAcquisition(object value) { if(ReferenceEquals(owner,value)) {Owned=false;owner=null;} }
        public int? ReadAcquisitionPower(int channel) { OnRead?.Invoke();
            if(RpmControl) {PowerReadsDuringRpm++; if(InvalidPowerDuringRpm)return InvalidPowerValue;}
            return PowerRead == null ? Duty : PowerRead(); }
        public string ReadAcquisitionDriveMode(int channel) { return DriveMode; }
        public AcquisitionRpm ReadAcquisitionRpm(int channel) { InternalReads++; return InternalSample(); }
        public bool WriteAcquisitionPower(object value,int channel,int duty,string expectedDriveMode,Func<bool> writeAllowed) {
            if(!ReferenceEquals(owner,value)||RejectWrites)return false;
            if(duty!=0) { BeforeWriteModeCheck?.Invoke(); if(expectedDriveMode!=DriveMode||writeAllowed==null||!writeAllowed())return false; }
            Writes.Add(duty);Duty=duty;RpmControl=false;return true;
        }
        public bool WriteAcquisitionRpm(object value,int channel,int rpm,Func<bool> writeAllowed) {
            if(!ReferenceEquals(owner,value)||DriveMode!="pwm"||RejectRpmWrites||rpm<=0||rpm>65535)return false;
            BeforeWriteModeCheck?.Invoke(); if(DriveMode!="pwm"||writeAllowed==null||!writeAllowed())return false;
            RpmWrites.Add(rpm);Duty=35;RpmControl=true;return true;
        }
    }
}

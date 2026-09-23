using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace pCUE
{
    // Independent of ordinary RPM hold: no dithering, no fallback tach source, no loss-of-signal escalation.
    // The actor owns Commander writes while leased; monitoring continues when duty is frozen.
    public sealed class PwmAcquisitionController : IDisposable
    {
        private readonly IPwmAcquisitionHardware hardware;
        private readonly Func<AcquisitionRpm> readSample;
        private readonly Func<int, bool> contextReady;
        private readonly Func<bool> exclusiveTach;
        private readonly Func<int> configuredChannel;
        private readonly Func<int, string> configuredDriveMode;
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Thread worker;
        private readonly object hardwareOwner = new object();
        private volatile bool active, disposed;
        private int disposeStarted;
        private PwmAcquisitionStatus snapshot;
        private PwmAcquisitionLimits limits;
        private string operationId, owner, token, originalLease, fault, sampleSession;
        private string driveMode;
        private AcquisitionRpm lastInternalSample;
        private int lastInternalChannel;
        private long lastInternalAt;
        private int channel, leaseSeconds;
        private long renewedAt, approachAt, lastWriteAt, stableAt, zeroAt, lastSequence = -1, zeroSequence = -1;
        private int stableSamples, zeroSamples;
        private double? targetRpm, setpoint, stableReference;
        private int? commanded;
        private bool frozen, kick;
        private string phase = "Idle";
        private PwmAmbientConfirmation confirmation;
        private readonly Func<long> timestamp;
        private readonly double clockFrequency;

        public PwmAcquisitionController(IPwmAcquisitionHardware hardware, Func<AcquisitionRpm> readSample,
            Func<int, bool> contextReady, Func<bool> exclusiveTach, Func<int> configuredChannel = null, Func<long> timestamp = null, double clockFrequency = 0,
            Func<int, string> configuredDriveMode = null)
        {
            this.hardware = hardware; this.readSample = readSample; this.contextReady = contextReady; this.exclusiveTach = exclusiveTach;
            this.configuredChannel = configuredChannel ?? (() => channel);
            this.configuredDriveMode = configuredDriveMode ?? (_ => "pwm");
            this.timestamp = timestamp ?? Stopwatch.GetTimestamp; this.clockFrequency = clockFrequency > 0 ? clockFrequency : Stopwatch.Frequency;
            Publish();
            worker = new Thread(Work) { IsBackground = true, Name = "PWM acoustic acquisition" }; worker.Start();
        }
        public bool IsLeased { get { return active; } }
        public PwmAcquisitionStatus Status { get { return Volatile.Read(ref snapshot); } }
        public Task<PwmAcquisitionStatus> ReadStatusAsync(int requestedChannel)
        {
            if (requestedChannel < 1 || requestedChannel > 6) throw new ArgumentOutOfRangeException(nameof(requestedChannel));
            var result = new TaskCompletionSource<PwmAcquisitionStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (disposed) { result.SetException(new ObjectDisposedException(nameof(PwmAcquisitionController))); return result.Task; }
            try { queue.Add(() => {
                try {
                    if (active) { result.SetResult(Status); return; }
                    string mode = configuredDriveMode(requestedChannel);
                    if (!hardware.IsConnected || !contextReady(requestedChannel) ||
                        (mode != "pwm" && mode != "dc-percent") || hardware.ReadAcquisitionDriveMode(requestedChannel - 1) != mode)
                        mode = null;
                    result.SetResult(BuildStatus(requestedChannel, mode,
                        mode == null ? new AcquisitionRpm() : ReadFeedback(mode, requestedChannel)));
                } catch (Exception ex) { result.SetException(ex); }
            }); } catch (InvalidOperationException) { result.TrySetException(new ObjectDisposedException(nameof(PwmAcquisitionController))); }
            return result.Task;
        }
        private double Milliseconds(long from) { return (timestamp() - from) * 1000.0 / clockFrequency; }
        public Task<PwmAcquisitionResponse> ExecuteAsync(string action, PwmAcquisitionRequest request)
        {
            var completion = new TaskCompletionSource<PwmAcquisitionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (disposed) { completion.SetException(new ObjectDisposedException(nameof(PwmAcquisitionController))); return completion.Task; }
            try
            {
                queue.Add(() =>
                {
                    if (disposed) { completion.TrySetException(new ObjectDisposedException(nameof(PwmAcquisitionController))); return; }
                    try
                    {
                        Tick(); var leaseResult = Execute(action, request); Tick(); Publish();
                        completion.TrySetResult(new PwmAcquisitionResponse { ok = true, status = Status, leaseToken = leaseResult });
                    }
                    catch (Exception ex)
                    {
                        Publish(); completion.TrySetResult(new PwmAcquisitionResponse { ok = false, status = Status,
                            error = ex is ArgumentException || ex is InvalidOperationException ? ex.Message : "Acquisition request failed." });
                    }
                });
            }
            catch (InvalidOperationException) { completion.TrySetException(new ObjectDisposedException(nameof(PwmAcquisitionController))); }
            return completion.Task;
        }
        public Task RevokeAsync(string reason)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (disposed) { done.SetResult(true); return done.Task; }
            try { queue.Add(() => { Terminate("Revoked", reason); Publish(); done.TrySetResult(true); }); }
            catch (InvalidOperationException) { done.TrySetResult(true); }
            return done.Task;
        }
        private void Work()
        {
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out Action action, 100)) action();
                try { Tick(); }
                catch { Terminate("Fault", "Unexpected acquisition worker error."); }
                Publish();
            }
            Terminate("Released", "Application closed; duty-zero was attempted. Electrical power-off is not verified."); Publish();
        }
        private string Execute(string action, PwmAcquisitionRequest request)
        {
            if (request == null) throw new ArgumentException("A JSON acquisition request is required.");
            if (action == "lease")
            {
                if (!Guid.TryParseExact(request.operationId, "D", out _) || string.IsNullOrWhiteSpace(request.owner) || request.owner.Length > 80 ||
                    request.channel < 1 || request.channel > 6 || request.leaseSeconds < 10 || request.leaseSeconds > 120 || request.limits == null)
                    throw new ArgumentException("A UUID operation, owner, channel, lease duration and explicit DUT limits are required.");
                request.limits.Validate();
                string requestedMode = request.driveMode ?? "pwm";
                if (requestedMode != "pwm" && requestedMode != "dc-percent")
                    throw new ArgumentException("Drive mode must be pwm (four-pin) or dc-percent (three-pin).");
                var fingerprint = new JavaScriptSerializer().Serialize(new { request.operationId, request.owner, request.channel, request.leaseSeconds, request.limits, driveMode = requestedMode });
                if (active)
                {
                    if (fingerprint == originalLease) return token;
                    throw new InvalidOperationException("The fixture is already leased or the lease parameters changed.");
                }
                if (request.operationId == operationId) throw new InvalidOperationException("This operation has ended. Reconcile its retained status.");
                if (!hardware.IsConnected || !FeedbackOwned(requestedMode) || !contextReady(request.channel) || configuredDriveMode(request.channel) != requestedMode)
                    throw new InvalidOperationException("Connected Commander, correct fan mode, exclusive selected feedback and stopped ordinary hold are required.");
                if (!hardware.TryAcquireAcquisition(hardwareOwner)) throw new InvalidOperationException("Commander control is already owned.");
                try
                {
                    if (!contextReady(request.channel) || configuredDriveMode(request.channel) != requestedMode ||
                        hardware.ReadAcquisitionDriveMode(request.channel - 1) != requestedMode)
                        throw new InvalidOperationException("Explicit channel mode and detected fan type must match the requested drive mode.");
                    if (hardware.ReadAcquisitionPower(request.channel - 1) != 0)
                        throw new InvalidOperationException("Set the selected fan to confirmed zero duty before acquiring it.");
                    var sample = ReadFeedback(requestedMode, request.channel);
                    if (!Fresh(sample, request.limits, requestedMode) || sample.value != 0)
                        throw new InvalidOperationException("A fresh genuine zero-RPM reading from the selected feedback source is required before lease acquisition.");
                    limits = new JavaScriptSerializer().Deserialize<PwmAcquisitionLimits>(new JavaScriptSerializer().Serialize(request.limits));
                    operationId = request.operationId; owner = request.owner; originalLease = fingerprint;
                    using (var random = RandomNumberGenerator.Create()) { var bytes = new byte[32]; random.GetBytes(bytes); token = BitConverter.ToString(bytes).Replace("-", ""); }
                    channel = request.channel; driveMode = requestedMode; leaseSeconds = request.leaseSeconds; renewedAt = timestamp();
                    sampleSession = sample.sampleSessionId; commanded = 0; active = true; phase = "Off"; fault = null;
                    confirmation = null; targetRpm = null; setpoint = null; frozen = false; kick = false;
                    ResetStability(); zeroSamples = 0; zeroAt = 0; zeroSequence = -1;
                    return token;
                }
                catch { hardware.ReleaseAcquisition(hardwareOwner); throw; }
            }
            RequireLease(request);
            switch (action)
            {
                case "renew":
                    if (request.leaseSeconds < 10 || request.leaseSeconds > 120) throw new ArgumentException("Lease duration must be 10–120 seconds.");
                    renewedAt = timestamp(); leaseSeconds = request.leaseSeconds; return null;
                case "target": SetTarget(request); return null;
                case "freeze":
                    if (frozen && phase == "Frozen") return null;
                    var sample = ReadFeedback(driveMode, channel);
                    if (phase != "Stable" || !Fresh(sample, limits, driveMode) || sample.value <= 0 || sample.sampleSessionId != sampleSession)
                        throw new InvalidOperationException("Distinct fresh stable RPM samples are required before capture.");
                    frozen = true; if (!targetRpm.HasValue) targetRpm = sample.value; phase = "Frozen"; return null;
                case "output-off":
                    try { ForceZero(); }
                    catch { Terminate("Fault", "Zero duty could not be confirmed; physical fixture attention is required."); throw; }
                    phase = "Off"; targetRpm = null; setpoint = null; frozen = false; ResetStability(); return null;
                case "confirm-ambient":
                    var ambientSample = ReadFeedback(driveMode, channel);
                    if (phase != "Off" || commanded != 0 || zeroSamples < 3 || Milliseconds(zeroAt) < limits.stabilityMilliseconds ||
                        !Fresh(ambientSample, limits, driveMode) || ambientSample.value != 0 || ambientSample.sampleSessionId != sampleSession)
                        throw new InvalidOperationException("Confirmed zero duty and distinct fresh stable zero-RPM readings are required.");
                    if (string.IsNullOrWhiteSpace(request.operatorName) || request.operatorName.Length > 80 || request.operatorName.Any(char.IsControl) ||
                        string.IsNullOrWhiteSpace(request.reason) || request.reason.Length > 500 || request.reason.Any(char.IsControl))
                        throw new ArgumentException("Record the operator name and the physical DUT-off confirmation reason.");
                    confirmation = new PwmAmbientConfirmation { operatorName = request.operatorName.Trim(), reason = request.reason.Trim(),
                        confirmedUtc = DateTime.UtcNow.ToString("O"), operationId = operationId, sampleSessionId = sampleSession };
                    return null;
                case "release":
                    Terminate("Released", null);
                    if (phase != "Released") throw new InvalidOperationException(fault ?? "Fixture release could not confirm zero duty.");
                    return null;
                default: throw new ArgumentException("Unknown acquisition action.");
            }
        }
        private void RequireLease(PwmAcquisitionRequest request)
        {
            if (!active || request.operationId != operationId || !TokenMatches(token, request.leaseToken))
                throw new InvalidOperationException("The lease is absent, expired or owned by another caller.");
        }
        private static bool TokenMatches(string expected, string supplied)
        {
            if (expected == null || supplied == null) return false;
            var left = Encoding.UTF8.GetBytes(expected); var right = Encoding.UTF8.GetBytes(supplied);
            int difference = left.Length ^ right.Length;
            for (int index = 0; index < left.Length; index++) difference |= left[index] ^ (index < right.Length ? right[index] : 0);
            return difference == 0;
        }
        private void SetTarget(PwmAcquisitionRequest request)
        {
            if (request.rpm.HasValue == request.setpoint.HasValue ||
                (request.rpm.HasValue && (!PwmAcquisitionLimits.Finite(request.rpm.Value) || request.rpm <= 0 || request.rpm > limits.maximumRpm)) ||
                (request.setpoint.HasValue && (!PwmAcquisitionLimits.Finite(request.setpoint.Value) || request.setpoint != Math.Truncate(request.setpoint.Value) || request.setpoint < limits.minimumSetpoint || request.setpoint > limits.maximumSetpoint)))
                throw new ArgumentException("Choose one RPM target or whole-percent duty within the DUT limits.");
            if (frozen) throw new InvalidOperationException("The output is frozen. End the operating point before retargeting.");
            var feedback = ReadFeedback(driveMode, channel);
            if (!Fresh(feedback, limits, driveMode) || feedback.sampleSessionId != sampleSession)
                throw new InvalidOperationException("Fresh selected RPM feedback from the leased session is required before changing output.");
            confirmation = null; zeroSamples = 0; targetRpm = request.rpm; setpoint = request.setpoint;
            ResetStability(); approachAt = timestamp(); phase = "Approaching";
            if (commanded == 0)
            {
                kick = limits.kickSetpoint.HasValue;
                try { WriteDuty((int)(limits.kickSetpoint ?? limits.startSetpoint)); }
                catch { if (active) Terminate("Fault", "Commander failed while starting the operating point."); throw; }
            }
        }
        private void Tick()
        {
            if (!active || disposed) return;
            Expire(); if (!active) return;
            try
            {
                if (!hardware.IsConnected || !FeedbackOwned(driveMode) || !contextReady(channel) ||
                    configuredDriveMode(channel) != driveMode || hardware.ReadAcquisitionDriveMode(channel - 1) != driveMode)
                    throw new InvalidOperationException("Fixture connection, fan drive mode, tach assignment or ownership changed.");
                var actualDuty = hardware.ReadAcquisitionPower(channel - 1);
                if (!actualDuty.HasValue || actualDuty != commanded) throw new InvalidOperationException("Commander duty changed or cannot be verified.");
                var sample = ReadFeedback(driveMode, channel);
                if (!Fresh(sample, limits, driveMode) || sample.sampleSessionId != sampleSession) throw new InvalidOperationException("Selected RPM feedback is stale, invalid or belongs to a different connection.");
                if (sample.value > limits.maximumRpm) throw new InvalidOperationException("RPM exceeded the per-DUT limit.");
                if (phase == "Off")
                {
                    if (sample.value != 0) { confirmation = null; zeroSamples = 0; zeroAt = 0; }
                    else if (sample.sampleSequence != zeroSequence)
                    { zeroSequence = sample.sampleSequence.Value; if (zeroSamples++ == 0) zeroAt = timestamp(); }
                    return;
                }
                if (frozen)
                {
                    if (!targetRpm.HasValue || Math.Abs(sample.value.Value - targetRpm.Value) > limits.rpmTolerance)
                        throw new InvalidOperationException("RPM drifted outside tolerance during capture.");
                    return; // Protection continues; there are no duty adjustments in capture mode.
                }
                if (phase != "Stable" && Milliseconds(approachAt) > limits.approachTimeoutSeconds * 1000)
                    throw new InvalidOperationException("The operating point could not settle within its timeout.");
                if (kick)
                {
                    if (Milliseconds(lastWriteAt) < limits.kickMilliseconds) return;
                    kick = false; WriteDuty((int)limits.startSetpoint); return;
                }
                if (sample.sampleSequence == lastSequence || Milliseconds(lastWriteAt) - sample.sampleAgeMs.Value < limits.settleMilliseconds) return;
                lastSequence = sample.sampleSequence.Value;
                var actualRpm = sample.value.Value;
                double desired = setpoint ?? commanded.Value;
                if (targetRpm.HasValue && Math.Abs(targetRpm.Value - actualRpm) > limits.rpmTolerance)
                    desired = commanded.Value + Math.Sign(targetRpm.Value - actualRpm) * limits.maximumStep;
                var maximumMove = (int)Math.Floor(Math.Min(limits.maximumStep, limits.maximumSlewPerSecond * Milliseconds(lastWriteAt) / 1000));
                var next = (int)Math.Max(limits.minimumSetpoint, Math.Min(limits.maximumSetpoint,
                    Math.Max(commanded.Value - maximumMove, Math.Min(commanded.Value + maximumMove, desired))));
                if (next != commanded) { ResetStability(); phase = "Approaching"; WriteDuty(next); return; }
                bool atTarget = targetRpm.HasValue ? Math.Abs(targetRpm.Value - actualRpm) <= limits.rpmTolerance : commanded == setpoint;
                if (!stableReference.HasValue) stableReference = actualRpm;
                if (!atTarget || actualRpm <= 0 || Math.Abs(actualRpm - stableReference.Value) > limits.rpmTolerance)
                { stableSamples = 0; stableAt = 0; stableReference = actualRpm; phase = "Approaching"; return; }
                if (stableSamples++ == 0) stableAt = timestamp();
                if (stableSamples >= 3 && Milliseconds(stableAt) >= limits.stabilityMilliseconds) phase = "Stable";
            }
            catch (Exception ex) { if (active) Terminate("Fault", ex is InvalidOperationException ? ex.Message : "Fixture communication failed."); }
        }
        private void ResetStability() { stableSamples = 0; stableAt = 0; stableReference = null; lastSequence = -1; }
        private static string FeedbackSource(string mode) { return mode == "pwm" ? "commander-internal" : mode == "dc-percent" ? "external-hid" : null; }
        private bool FeedbackOwned(string mode) { return mode == "pwm" ? hardware.IsConnected : mode == "dc-percent" && exclusiveTach(); }
        private AcquisitionRpm ReadFeedback(string mode, int fan)
        {
            if (mode != "pwm") return readSample();
            lastInternalSample = hardware.ReadAcquisitionRpm(fan - 1);
            lastInternalAt = timestamp(); lastInternalChannel = fan;
            return lastInternalSample;
        }
        private AcquisitionRpm CachedInternalSample(int fan)
        {
            var sample = lastInternalChannel == fan ? lastInternalSample : null;
            if (sample == null) return new AcquisitionRpm { source = "commander-internal" };
            return new AcquisitionRpm { value = sample.value, source = sample.source, sampleSessionId = sample.sampleSessionId,
                sampleSequence = sample.sampleSequence, sampleUtc = sample.sampleUtc,
                sampleAgeMs = sample.sampleAgeMs + Milliseconds(lastInternalAt), batteryLow = sample.batteryLow };
        }
        private bool FeedbackStillFresh()
        {
            var sample = driveMode == "pwm" ? CachedInternalSample(channel) : readSample();
            return Fresh(sample, limits, driveMode) && sample.sampleSessionId == sampleSession;
        }
        private static bool Fresh(AcquisitionRpm sample, PwmAcquisitionLimits cfg, string mode)
        {
            return sample != null && sample.value.HasValue && PwmAcquisitionLimits.Finite(sample.value.Value) && sample.value >= 0 &&
                sample.source == FeedbackSource(mode) && (mode == "pwm" ? sample.batteryLow != true : sample.batteryLow == false) && !string.IsNullOrWhiteSpace(sample.sampleSessionId) &&
                sample.sampleSequence.HasValue && sample.sampleSequence >= 0 && sample.sampleAgeMs.HasValue &&
                PwmAcquisitionLimits.Finite(sample.sampleAgeMs.Value) && sample.sampleAgeMs >= 0 && sample.sampleAgeMs <= cfg.maximumSampleAgeMilliseconds;
        }
        private void WriteDuty(int value)
        {
            if (value < limits.minimumSetpoint || value > limits.maximumSetpoint) throw new InvalidOperationException("Duty exceeds the per-DUT envelope.");
            // Readback may have blocked long enough to exhaust the lease. Check at the last
            // possible point before energizing; zero-duty cleanup remains allowed after expiry.
            Expire();
            if (!active || disposed) throw new InvalidOperationException("Lease ended before the output write.");
            if (!contextReady(channel) || !FeedbackOwned(driveMode) || configuredDriveMode(channel) != driveMode)
                throw new InvalidOperationException("Fixture configuration changed before the output write.");
            confirmation = null; zeroSamples = 0;
            if (!hardware.WriteAcquisitionPower(hardwareOwner, channel - 1, value, driveMode,
                () => active && !disposed && Milliseconds(renewedAt) < leaseSeconds * 1000 &&
                    contextReady(channel) && FeedbackOwned(driveMode) && configuredDriveMode(channel) == driveMode && FeedbackStillFresh()))
                throw new InvalidOperationException("Commander rejected acquisition drive level, lease expired or fan mode changed.");
            commanded = value; lastWriteAt = timestamp();
        }
        private void ForceZero()
        {
            confirmation = null; zeroSamples = 0; zeroAt = 0; zeroSequence = -1;
            if (!hardware.WriteAcquisitionPower(hardwareOwner, channel - 1, 0, null, null) || hardware.ReadAcquisitionPower(channel - 1) != 0)
            { commanded = null; throw new InvalidOperationException("Zero duty could not be confirmed. Physically inspect the fixture."); }
            commanded = 0; lastWriteAt = timestamp();
        }
        private void Terminate(string state, string reason)
        {
            if (active)
            {
                try { ForceZero(); }
                catch { state = "Fault"; reason = "Zero duty could not be confirmed; physical fixture attention is required."; }
                finally { hardware.ReleaseAcquisition(hardwareOwner); }
            }
            active = false; token = null; frozen = false; kick = false; confirmation = null; phase = state; fault = reason;
        }
        private void Expire() { if (active && Milliseconds(renewedAt) >= leaseSeconds * 1000) Terminate("Expired", "Lease expired; zero duty attempted. Electrical power-off is not verified."); }
        private void Publish()
        {
            // Terminal acknowledgements retain the leased channel/mode even with no external tach assignment.
            int statusChannel = operationId != null ? channel : configuredChannel();
            string statusMode = operationId != null ? driveMode : configuredDriveMode(statusChannel);
            AcquisitionRpm sample;
            try { sample = statusMode == "pwm" ? CachedInternalSample(statusChannel) : readSample() ?? new AcquisitionRpm(); } catch { sample = new AcquisitionRpm(); }
            Volatile.Write(ref snapshot, BuildStatus(statusChannel, statusMode, sample));
        }
        private PwmAcquisitionStatus BuildStatus(int statusChannel, string statusMode, AcquisitionRpm sample)
        {
            bool ambient = active && phase == "Off" && commanded == 0 && limits != null && Fresh(sample, limits, driveMode) && sample.value == 0 &&
                confirmation != null && confirmation.operationId == operationId && confirmation.sampleSessionId == sample.sampleSessionId;
            return new PwmAcquisitionStatus { channel = statusChannel,
                driveMode = statusMode, phase = phase, targetRpm = targetRpm, fault = fault,
                capabilities = new { exclusiveLease = true, sensorFreshness = true, supervisedFreeze = true, confirmedOutputOff = false,
                    exclusiveTachOwnership = FeedbackOwned(statusMode), exclusiveFeedbackOwnership = FeedbackOwned(statusMode),
                    feedbackSource = FeedbackSource(statusMode), explicitDriveMode = true },
                lease = new { active, operationId, owner, expiresUtc = active ? DateTime.UtcNow.AddMilliseconds(Math.Max(0, leaseSeconds * 1000 - Milliseconds(renewedAt))).ToString("O") : null },
                rpm = sample, actuator = new { commandedValue = commanded, unit = "percent", outputOn = ambient ? (bool?)false : commanded > 0 ? (bool?)true : null, frozen, protectionActive = active, ditherActive = false },
                ambientConfirmed = ambient, ambientConfirmation = confirmation, observedUtc = DateTime.UtcNow.ToString("O") };
        }
        public void Dispose()
        {
            bool first = Interlocked.Exchange(ref disposeStarted, 1) == 0;
            if (first) { disposed = true; queue.CompleteAdding(); }
            // Commander I/O has bounded driver timeouts. Ownership and streams must not be
            // reused while the final zero-duty action still runs on this thread.
            if (Thread.CurrentThread != worker) { worker.Join(); if (first) queue.Dispose(); }
        }
    }
}

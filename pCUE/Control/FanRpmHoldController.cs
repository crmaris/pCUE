using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace pCUE
{
    /// <summary>Where the hold loop currently is.</summary>
    public enum FanHoldStatus
    {
        Idle,
        Ramping,        // far from target, first approach
        Correcting,     // drifted after having been stable
        Stabilizing,    // inside tolerance, waiting out the stabilization window
        Stable,
        Fault,
        Stopped,
    }

    /// <summary>One loop iteration's worth of state, for the UI.</summary>
    public sealed class FanHoldSnapshot
    {
        public FanHoldStatus Status { get; set; }
        public double? RawRpm { get; set; }
        public double FilteredRpm { get; set; }
        public double TargetRpm { get; set; }
        public int Duty { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Tunables. Defaults are ported from the Fan Control Application's ControllerConfig.</summary>
    public sealed class FanHoldConfig
    {
        public double TargetRpm { get; set; } = 1000;
        /// <summary>
        /// +/- RPM considered "on target". Measured on the bench, 1% duty moves a typical fan by
        /// roughly 20-25 RPM, so this is deliberately set near (slightly under) one duty step: it
        /// pushes the loop to the closest reachable duty rather than stopping early. With the old
        /// value of 50 a 1500 RPM target settled at 1464; at 20 the same fan holds 1510.
        /// A tolerance the actuator cannot resolve is safe - the resolution-limit detection parks
        /// on the closer duty instead of hunting.
        /// </summary>
        public double RpmTolerance { get; set; } = 20;

        // Duty envelope (Commander PRO fan power is a whole-number percent, 0-100).
        public int MinDuty { get; set; } = 0;
        public int MaxDuty { get; set; } = 100;
        public int StartDuty { get; set; } = 40;
        /// <summary>
        /// True when StartDuty is the duty the fan is ALREADY running at. There is then nothing to
        /// settle, so the loop can measure immediately instead of burning SettleDelayMs first -
        /// which is most of the perceived lag when nudging a target by a small amount.
        /// </summary>
        public bool StartDutyIsCurrent { get; set; } = false;

        public int CoarseDutyStep { get; set; } = 5;
        public int FineDutyStep { get; set; } = 1;
        /// <summary>|RPM error| above which the coarse step is used.</summary>
        public double CoarseErrorThreshold { get; set; } = 200;

        public int SampleIntervalMs { get; set; } = 500;
        /// <summary>
        /// Wait after each duty change before believing the tachometer again. This has to cover the
        /// fan's mechanical settling AND the handheld tachometer's own refresh - measured on the
        /// bench, a fan needs several seconds. Too short and the loop reads a stale RPM, over-
        /// corrects, and oscillates instead of converging (observed: duty 75->65 while RPM was
        /// still RISING 1605->1870).
        /// </summary>
        public int SettleDelayMs { get; set; } = 4000;
        public int StabilizationTimeMs { get; set; } = 3000;
        /// <summary>Max time to first Stable. 0 disables.</summary>
        public int TimeoutMs { get; set; } = 120000;

        /// <summary>Kept short: a long window is extra lag in an already laggy loop.</summary>
        public int RpmFilterWindow { get; set; } = 3;
        public int MaxInvalidRpmSamples { get; set; } = 8;
    }

    /// <summary>
    /// Closed-loop fan RPM hold, in software.
    ///
    /// The Commander PRO's own fixed-RPM mode (0x24) closes its loop against the tach pin on its
    /// OWN header, so it is useless for a fan with no speed-sense wire. This class supplies the
    /// missing feedback: it reads the external bench tachometer and drives the Commander's fan
    /// POWER (duty %) until the measured RPM matches the target.
    ///
    ///   target RPM -> this controller -> Commander duty % -> fan -> bench tach -> measured RPM
    ///                       ^                                                          |
    ///                       +---------------------- error ------------------------------+
    ///
    /// Strategy is ported from the Fan Control Application's FanRpmController: a simple, robust
    /// step-based proportional walk (coarse step when far from target, fine step when near),
    /// deliberately NOT an aggressive PID.
    ///
    /// One difference matters. That controller's actuator is a PSU voltage with millivolt
    /// resolution; ours is a whole-number percent, so the finest move is 1% duty - often 20-50 RPM
    /// on a real fan. A tolerance tighter than one duty step is therefore unreachable, and a naive
    /// loop would oscillate around the target forever. <see cref="ResolutionLimitReversals"/>
    /// detects that bracketing and parks on the better of the two duties instead of hunting.
    ///
    /// Threading: the loop runs on a background Task. Events are raised from that thread, so
    /// subscribers must marshal to the UI thread.
    /// </summary>
    public sealed class FanRpmHoldController
    {
        /// <summary>Direction flips at the finest step before we accept we cannot get closer.</summary>
        private const int ResolutionLimitReversals = 2;

        private readonly Action<int> _setDuty;       // duty % -> hardware
        private readonly Func<double?> _readRpm;     // fresh RPM, or null when stale/lost
        private readonly Func<bool> _canRun;         // e.g. Commander still connected

        private CancellationTokenSource _cts;
        private Task _loop;
        private int _running;                        // 0/1 guard

        private readonly object _targetLock = new object();
        private double _targetRpm;

        public FanRpmHoldController(Action<int> setDuty, Func<double?> readRpm, Func<bool> canRun)
        {
            _setDuty = setDuty ?? throw new ArgumentNullException(nameof(setDuty));
            _readRpm = readRpm ?? throw new ArgumentNullException(nameof(readRpm));
            _canRun = canRun ?? throw new ArgumentNullException(nameof(canRun));
        }

        public FanHoldStatus Status { get; private set; } = FanHoldStatus.Idle;
        public bool IsRunning => Volatile.Read(ref _running) == 1;
        /// <summary>The duty the loop last programmed - left in place when the loop stops.</summary>
        public int CurrentDuty { get; private set; }

        public event EventHandler<FanHoldSnapshot> SnapshotUpdated;
        public event EventHandler<FanHoldStatus> StatusChanged;

        /// <summary>Starts the loop. Throws if already running.</summary>
        public Task StartAsync(FanHoldConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                throw new InvalidOperationException("The RPM hold is already running.");

            lock (_targetLock) _targetRpm = config.TargetRpm;

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(config, _cts.Token));
            return _loop;
        }

        /// <summary>Requests a clean stop. Safe to call repeatedly. Never blocks on the loop.</summary>
        public void Stop()
        {
            try { _cts?.Cancel(); }
            catch (Exception ex) { Debug.WriteLine("pCUE: RPM hold cancel failed: " + ex.Message); }
        }

        /// <summary>Changes the setpoint of a running loop without restarting it.</summary>
        public void UpdateTarget(double rpm)
        {
            if (double.IsNaN(rpm) || rpm < 0) return;
            lock (_targetLock) _targetRpm = rpm;
        }

        private double GetTarget() { lock (_targetLock) return _targetRpm; }

        private async Task RunAsync(FanHoldConfig cfg, CancellationToken ct)
        {
            var samples = new Queue<double>(Math.Max(1, cfg.RpmFilterWindow));
            int duty = Clamp(cfg.StartDuty, cfg.MinDuty, cfg.MaxDuty);
            int invalidCount = 0;
            bool everStable = false;
            DateTime? inToleranceSince = null;

            // Resolution-limit tracking: we are at the floor once the finest step keeps flipping
            // direction. Remember the best duty seen so we can park on it rather than oscillate.
            int lastFineDirection = 0;
            int reversals = 0;
            int bestDuty = duty;
            double bestAbsError = double.MaxValue;

            // The last duty that actually produced a reading. Used to tell a stalled fan apart from
            // a dead sensor: if we still had RPM at a higher duty, the silence is our own doing.
            int lastGoodDuty = -1;

            string stopReason = "stopped";
            bool faulted = false;

            try
            {
                AppLog.Info("HOLD start: target=" + cfg.TargetRpm.ToString("0") + " RPM +/-" +
                            cfg.RpmTolerance.ToString("0") + ", duty[" + cfg.MinDuty + ".." + cfg.MaxDuty +
                            "] start=" + duty + "%, coarse=" + cfg.CoarseDutyStep + "% fine=" + cfg.FineDutyStep + "%");
                SetStatus(FanHoldStatus.Ramping);
                ApplyDuty(duty);

                // The fan is still turning at whatever speed it was doing before this loop began,
                // which may be nowhere near StartDuty. Give it the same settle time a mid-run
                // correction gets and throw away everything measured during it - otherwise the very
                // first error is computed from the OLD duty and the loop confidently corrects in the
                // wrong direction. (Seen on the bench: starting a 1100 RPM hold from a fan running
                // at ~1400 walked the duty DOWN to 911 RPM.)
                if (cfg.StartDutyIsCurrent)
                {
                    // The fan is already running at this duty and has been for a while, so there is
                    // nothing to settle and nothing stale to discard. Waiting anyway is pure lag on
                    // the most common case there is: nudging the target by a little.
                    AppLog.Info("HOLD starting from the fan's current duty - no settle wait needed");
                }
                else if (!await Delay(cfg.SettleDelayMs, ct)) { stopReason = "stopped before settling"; }
                else { samples.Clear(); }

                var overall = Stopwatch.StartNew();

                while (!ct.IsCancellationRequested)
                {
                    if (!_canRun())
                    {
                        faulted = true;
                        stopReason = "Commander PRO disconnected";
                        break;
                    }

                    double target = GetTarget();

                    double? raw;
                    try { raw = _readRpm(); }
                    catch (Exception ex)
                    {
                        faulted = true;
                        stopReason = "tachometer read failed: " + ex.Message;
                        break;
                    }

                    if (raw == null)
                    {
                        invalidCount++;
                        if (invalidCount >= cfg.MaxInvalidRpmSamples)
                        {
                            // Two very different faults produce a run of unreadable samples, and they
                            // need opposite responses:
                            //
                            //   the sensor died      -> leave the fan where it is (it keeps cooling)
                            //   we stalled the fan   -> put the duty back up, or it stays stopped
                            //
                            // The second is self-inflicted: asking for an RPM below what the fan can
                            // physically turn at walks the duty down until it stops, and a stopped fan
                            // reads 0, which looks exactly like a dead tachometer. Backing off to the
                            // last duty that gave a reading restarts it and names the real cause.
                            faulted = true;
                            if (lastGoodDuty > duty)
                            {
                                duty = lastGoodDuty;
                                ApplyDuty(duty);
                                stopReason = "target is below this fan's minimum speed - backed off to " +
                                             duty + "%";
                            }
                            else
                            {
                                stopReason = "lost tachometer signal - duty held at " + duty + "%";
                            }
                            break;
                        }

                        Emit(Status, null, Average(samples), target, duty,
                             "no signal " + invalidCount + "/" + cfg.MaxInvalidRpmSamples);
                        if (!await Delay(cfg.SampleIntervalMs, ct)) break;
                        continue;
                    }

                    invalidCount = 0;
                    lastGoodDuty = duty;
                    double filtered = AddSample(samples, raw.Value, cfg.RpmFilterWindow);
                    double error = target - filtered;
                    double absError = Math.Abs(error);

                    AppLog.Debug("HOLD raw=" + raw.Value.ToString("0") + " filtered=" + filtered.ToString("0") +
                                 " target=" + target.ToString("0") + " err=" + error.ToString("+0;-0;0") +
                                 " duty=" + duty + "% status=" + Status);

                    if (absError < bestAbsError) { bestAbsError = absError; bestDuty = duty; }

                    Emit(Status, raw, filtered, target, duty,
                         "err=" + error.ToString("+0;-0;0") + " RPM");

                    // Timeout only applies until we first stabilize.
                    if (!everStable && cfg.TimeoutMs > 0 && overall.ElapsedMilliseconds > cfg.TimeoutMs)
                    {
                        faulted = true;
                        stopReason = "timed out reaching " + target.ToString("0") + " RPM";
                        break;
                    }

                    if (absError <= cfg.RpmTolerance)
                    {
                        if (inToleranceSince == null)
                        {
                            inToleranceSince = DateTime.UtcNow;
                            SetStatus(FanHoldStatus.Stabilizing);
                        }

                        if ((DateTime.UtcNow - inToleranceSince.Value).TotalMilliseconds >= cfg.StabilizationTimeMs)
                        {
                            everStable = true;
                            SetStatus(FanHoldStatus.Stable);
                        }

                        if (!await Delay(cfg.SampleIntervalMs, ct)) break;
                        continue;
                    }

                    // --- out of tolerance: step ---
                    inToleranceSince = null;

                    bool fineRegion = absError <= cfg.CoarseErrorThreshold;
                    int direction = Math.Sign(error);       // >0 need more RPM
                    SetStatus(everStable ? FanHoldStatus.Correcting : FanHoldStatus.Ramping);

                    int step = fineRegion ? cfg.FineDutyStep : cfg.CoarseDutyStep;

                    // At the finest step, a direction flip means the target sits between two whole
                    // duty values. Park on whichever was closer and stop hunting.
                    if (fineRegion && step <= 1)
                    {
                        if (lastFineDirection != 0 && direction != lastFineDirection) reversals++;
                        lastFineDirection = direction;

                        // Only accept "cannot get closer" when we are genuinely near the target.
                        // Direction can also flip while still far away (overshoot during a ramp),
                        // and parking then would report Stable at an arbitrary error - the loop once
                        // announced Stable 189 RPM off a 1100 RPM target this way.
                        bool nearEnough = absError <= cfg.RpmTolerance * 3;
                        if (!nearEnough) { reversals = 0; }

                        if (nearEnough && reversals >= ResolutionLimitReversals)
                        {
                            if (duty != bestDuty) { duty = bestDuty; ApplyDuty(duty); }
                            everStable = true;
                            SetStatus(FanHoldStatus.Stable);
                            Emit(FanHoldStatus.Stable, raw, filtered, target, duty,
                                 "at 1% duty resolution limit (closest reachable)");
                            if (!await Delay(cfg.SettleDelayMs, ct)) break;
                            continue;
                        }
                    }
                    else
                    {
                        lastFineDirection = 0;
                        reversals = 0;
                    }

                    int newDuty = Clamp(duty + direction * step, cfg.MinDuty, cfg.MaxDuty);

                    if (newDuty == duty)
                    {
                        // Saturated at an end of the envelope and still off target.
                        faulted = true;
                        stopReason = duty >= cfg.MaxDuty
                            ? "fan cannot reach " + target.ToString("0") + " RPM (already at " + cfg.MaxDuty + "%)"
                            : "fan cannot go below " + filtered.ToString("0") + " RPM (already at " + cfg.MinDuty + "%)";
                        break;
                    }

                    duty = newDuty;
                    ApplyDuty(duty);
                    if (!await Delay(cfg.SettleDelayMs, ct)) break;

                    //Throw away everything measured before/at the change. Without this the moving
                    //average blends pre-change and post-change readings, so the next error is
                    //computed from an RPM the fan has already left - which is what makes a laggy
                    //plant oscillate rather than converge.
                    samples.Clear();
                }
            }
            catch (OperationCanceledException) { /* normal stop */ }
            catch (Exception ex)
            {
                faulted = true;
                stopReason = ex.Message;
                Debug.WriteLine("pCUE: RPM hold loop crashed: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
                if (faulted) AppLog.Error("HOLD fault: " + stopReason + " (duty left at " + duty + "%)");
                else AppLog.Info("HOLD stopped: " + stopReason + " (duty left at " + duty + "%)");
                SetStatus(faulted ? FanHoldStatus.Fault : FanHoldStatus.Stopped);
                Emit(Status, null, Average(samples), GetTarget(), duty, stopReason);
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        private void ApplyDuty(int duty)
        {
            CurrentDuty = duty;
            AppLog.Info("HOLD duty -> " + duty + "%");
            _setDuty(duty);
        }

        /// <summary>Cancellable delay. Returns false when cancelled, so callers can break out.</summary>
        private static async Task<bool> Delay(int ms, CancellationToken ct)
        {
            try { await Task.Delay(Math.Max(1, ms), ct).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) { return false; }
        }

        private static double AddSample(Queue<double> samples, double rpm, int window)
        {
            samples.Enqueue(rpm);
            while (samples.Count > Math.Max(1, window)) samples.Dequeue();
            return Average(samples);
        }

        private static double Average(Queue<double> samples)
        {
            return samples.Count == 0 ? 0 : samples.Average();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void SetStatus(FanHoldStatus status)
        {
            if (Status == status) return;
            Status = status;
            try { StatusChanged?.Invoke(this, status); } catch { }
        }

        private void Emit(FanHoldStatus status, double? raw, double filtered, double target, int duty, string note)
        {
            var handler = SnapshotUpdated;
            if (handler == null) return;
            try
            {
                handler(this, new FanHoldSnapshot
                {
                    Status = status,
                    RawRpm = raw,
                    FilteredRpm = filtered,
                    TargetRpm = target,
                    Duty = duty,
                    Note = note,
                });
            }
            catch { }
        }
    }
}

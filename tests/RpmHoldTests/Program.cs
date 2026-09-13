using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using pCUE;

internal static class Program
{
    private static int failures;
    private static int checks;

    private static int Main()
    {
        Check("ascending convergence", () => Converges(20, 1500));
        Check("descending convergence", () => Converges(80, 1100));
        Check("stop during a pending reading prevents later writes", StopDuringRead);
        Check("stop before first write prevents startup kick", StopBeforeWrite);
        Check("lost signal preserves accepted duty", LostSignal);
        Check("rejected write preserves accepted duty", RejectedWrite);
        Check("retarget leaves old resolution-limit duty", Retarget);
        Check("dither alternates and follows a new target", DitherRetarget);
        Check("feedback recovery requires fresh stabilization", FeedbackRecovery);
        Check("stalled fan restores last working duty", StalledFan);
        Check("disconnected actuator cannot receive startup power", DisconnectedStart);
        Check("running configuration is isolated", ConfigIsolation);
        Check("invalid configuration is rejected before writes", InvalidConfig);
        Check("tach decode is culture-independent and invalid frames expire", Decode);
        Console.WriteLine($"RPM hold tests: {checks - failures}/{checks} passed (no hardware opened).");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string name, Action test)
    {
        checks++;
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    private static FanHoldConfig Config(double target = 1000) => new FanHoldConfig
    {
        TargetRpm = target, StartDuty = 40, StartDutyIsCurrent = true,
        SampleIntervalMs = 1, SettleDelayMs = 1, StabilizationTimeMs = 4,
        TimeoutMs = 1000, RpmFilterWindow = 1, MaxInvalidRpmSamples = 3
    };

    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static void Finish(FanRpmHoldController controller, Task loop)
    {
        controller.Stop();
        Assert(loop.Wait(3000), "loop failed to stop");
    }

    private static void Converges(int start, int target)
    {
        int duty = start;
        var cfg = Config(target); cfg.StartDuty = start;
        var controller = new FanRpmHoldController(d => duty = d, () => duty * 25, () => true);
        using var stable = new ManualResetEventSlim();
        controller.StatusChanged += (_, s) => { if (s == FanHoldStatus.Stable) stable.Set(); };
        var loop = controller.StartAsync(cfg);
        try
        {
            Assert(stable.Wait(2500), "did not converge");
            Assert(Math.Abs(duty * 25 - target) <= cfg.RpmTolerance, "false Stable");
        }
        finally { Finish(controller, loop); }
    }

    private static void StopDuringRead()
    {
        int duty = 0;
        using var reading = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var controller = new FanRpmHoldController(d => duty = d,
            () => { reading.Set(); Assert(resume.Wait(2000), "read not released"); return 500; }, () => true);
        var loop = controller.StartAsync(Config());
        try
        {
            Assert(reading.Wait(2000), "read did not start");
            controller.Stop();
            duty = 17; // manual Set Speed after Stop returns
            resume.Set();
            Assert(loop.Wait(2000), "loop did not finish");
            Assert(duty == 17, "old loop overwrote manual duty with " + duty);
        }
        finally { resume.Set(); Finish(controller, loop); }
    }

    private static void StopBeforeWrite()
    {
        int writes = 0;
        var controller = new FanRpmHoldController(_ => writes++, () => 500, () => true);
        controller.StatusChanged += (_, s) => { if (s == FanHoldStatus.Ramping) controller.Stop(); };
        var loop = controller.StartAsync(Config());
        try { Assert(loop.Wait(2000), "loop did not finish"); Assert(writes == 0, "cancelled loop applied startup power"); }
        finally { Finish(controller, loop); }
    }

    private static void LostSignal()
    {
        var writes = new List<int>();
        var controller = new FanRpmHoldController(writes.Add, () => null, () => true);
        var loop = controller.StartAsync(Config());
        try
        {
            Assert(loop.Wait(2000), "missing signal did not stop loop");
            Assert(controller.Status == FanHoldStatus.Fault && writes.SequenceEqual(new[] { 40 }), "lost signal changed duty");
        }
        finally { Finish(controller, loop); }
    }

    private static void RejectedWrite()
    {
        int duty = 40;
        var controller = new FanRpmHoldController(d =>
        { if (d != 40) throw new InvalidOperationException("device rejected"); duty = d; }, () => 500, () => true);
        var loop = controller.StartAsync(Config());
        try
        {
            Assert(loop.Wait(2000), "rejection did not stop loop");
            Assert(controller.Status == FanHoldStatus.Fault && controller.CurrentDuty == duty, "reported rejected duty as accepted");
        }
        finally { Finish(controller, loop); }
    }

    private static void Retarget()
    {
        int duty = 40;
        bool changed = false;
        using var reached = new ManualResetEventSlim();
        var cfg = Config(); cfg.DitherEnabled = false; cfg.RpmTolerance = 10;
        var controller = new FanRpmHoldController(d => duty = d, () => duty * 30, () => true);
        controller.SnapshotUpdated += (_, s) =>
        {
            if (s.Status != FanHoldStatus.Stable) return;
            if (!changed) { changed = true; controller.UpdateTarget(1605); }
            else if (s.TargetRpm == 1605 && Math.Abs(s.FilteredRpm - 1605) <= 30 && duty >= 53) reached.Set();
        };
        var loop = controller.StartAsync(cfg);
        try { Assert(reached.Wait(2500), "new target remained trapped by old best-duty history (duty=" + duty + ")"); }
        finally { Finish(controller, loop); }
    }

    private static void ConfigIsolation()
    {
        int duty = 40;
        using var reading = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var cfg = Config(3500); cfg.MaxDuty = 45;
        var controller = new FanRpmHoldController(d => duty = d,
            () => { reading.Set(); Assert(resume.Wait(2000), "read not released"); return 500; }, () => true);
        var loop = controller.StartAsync(cfg);
        try
        {
            Assert(reading.Wait(2000), "read not started");
            cfg.MaxDuty = 100; cfg.CoarseDutyStep = 50;
            resume.Set();
            Assert(loop.Wait(2000), "did not saturate");
            Assert(duty <= 45, "running envelope changed under loop: " + duty);
        }
        finally { resume.Set(); Finish(controller, loop); }
    }

    private static void DitherRetarget()
    {
        int duty = 40;
        bool retargeted = false;
        int legs = 0;
        using var reached = new ManualResetEventSlim();
        var cfg = Config(1215); cfg.RpmTolerance = 6; cfg.RpmFilterWindow = 3;
        var controller = new FanRpmHoldController(d =>
        { if (d != duty) legs++; duty = d; }, () => duty * 30, () => true);
        controller.SnapshotUpdated += (_, s) =>
        {
            if (!retargeted && s.Note.Contains("dithering") && legs >= 5)
            { retargeted = true; controller.UpdateTarget(1605); }
            else if (retargeted && s.Status == FanHoldStatus.Stable &&
                s.TargetRpm == 1605 && duty >= 53 && Math.Abs(s.FilteredRpm - 1605) <= 18) reached.Set();
        };
        var loop = controller.StartAsync(cfg);
        try { Assert(reached.Wait(2500), "dither failed to alternate or retarget"); }
        finally { Finish(controller, loop); }
    }

    private static void FeedbackRecovery()
    {
        int phase = 0;
        var recovery = new System.Diagnostics.Stopwatch();
        using var stableAgain = new ManualResetEventSlim();
        var cfg = Config(); cfg.StabilizationTimeMs = 30;
        var controller = new FanRpmHoldController(_ => { }, () =>
        {
            if (phase == 1) { phase = 2; recovery.Start(); return (double?)null; }
            return 1000;
        }, () => true);
        controller.StatusChanged += (_, s) =>
        {
            if (s != FanHoldStatus.Stable) return;
            if (phase == 0) phase = 1;
            else if (phase == 2) stableAgain.Set();
        };
        var loop = controller.StartAsync(cfg);
        try
        {
            Assert(stableAgain.Wait(2500), "signal loss did not clear Stable");
            Assert(recovery.ElapsedMilliseconds >= 30, "missing-signal time counted as stable feedback");
        }
        finally { Finish(controller, loop); }
    }

    private static void StalledFan()
    {
        int duty = 40;
        var controller = new FanRpmHoldController(d => duty = d, () => duty >= 40 ? (double?)1000 : null, () => true);
        var loop = controller.StartAsync(Config(950));
        try
        {
            Assert(loop.Wait(2000), "stall did not stop controller");
            Assert(controller.Status == FanHoldStatus.Fault && duty == 40, "stall left fan below working duty");
        }
        finally { Finish(controller, loop); }
    }

    private static void DisconnectedStart()
    {
        int writes = 0;
        var controller = new FanRpmHoldController(_ => writes++, () => 1000, () => false);
        var loop = controller.StartAsync(Config());
        try { Assert(loop.Wait(2000), "loop did not finish"); Assert(writes == 0, "wrote before checking connection"); }
        finally { Finish(controller, loop); }
    }

    private static void InvalidConfig()
    {
        int writes = 0;
        var cfg = Config(); cfg.MinDuty = 120;
        var controller = new FanRpmHoldController(_ => writes++, () => 500, () => true);
        Task loop = null;
        bool refused = false;
        try { loop = controller.StartAsync(cfg); }
        catch (ArgumentException) { refused = true; }
        finally { if (loop != null) Finish(controller, loop); }
        Assert(refused && writes == 0, "unsafe duty envelope was accepted");
    }

    private static void Decode()
    {
        // Exercise the actual driver decoder without Connect, enumeration or opening a HID stream.
        var tach = new HidTachometer();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var decode = typeof(HidTachometer).GetMethod("DecodeRpm", flags);
        var timestamp = typeof(HidTachometer).GetField("_latestRpmUtc", flags);
        var rpm = typeof(HidTachometer).GetField("_latestRpm", flags);
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "el-GR", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                // Display order 43.2100 reverses the first 6 characters to 012.34 -> 12 RPM.
                var bytes = new byte[] { 101, 124, 222, 96, 123, 123, 0, 0, 0, 0, 2, 0 };
                var tokens = bytes.SelectMany(b => b.ToString("X2").Select(c => c.ToString())).ToList();
                decode.Invoke(tach, new object[] { tokens });
                Assert((double)rpm.GetValue(tach) == 12 && tach.BatteryLow, "bad decode in " + culture);
                var before = timestamp.GetValue(tach);
                decode.Invoke(tach, new object[] { Enumerable.Repeat("0", 24).ToList() });
                Assert(before.Equals(timestamp.GetValue(tach)), "invalid frame refreshed stale data");
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}

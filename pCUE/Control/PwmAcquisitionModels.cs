using System;
using System.Linq;
using System.Threading.Tasks;

namespace pCUE
{
    // Lowercase properties intentionally match the shared NoiseAutoTesting JSON contract.
    public sealed class PwmAcquisitionLimits
    {
        public double minimumSetpoint { get; set; }
        public double maximumSetpoint { get; set; }
        public double startSetpoint { get; set; }
        public double maximumStep { get; set; }
        public double maximumSlewPerSecond { get; set; }
        public double? currentLimitAmps { get; set; }
        public double rpmTolerance { get; set; } = 20;
        public int settleMilliseconds { get; set; } = 4000;
        public int stabilityMilliseconds { get; set; } = 3000;
        public int maximumSampleAgeMilliseconds { get; set; } = 1500;
        public int approachTimeoutSeconds { get; set; } = 120;
        public double maximumRpm { get; set; } = 10000;
        public double? kickSetpoint { get; set; }
        public int kickMilliseconds { get; set; }
        public void Validate()
        {
            if (new[] { minimumSetpoint, maximumSetpoint, startSetpoint, maximumStep, maximumSlewPerSecond, rpmTolerance, maximumRpm }.Any(v => !Finite(v)) ||
                minimumSetpoint < 0 || maximumSetpoint > 100 || minimumSetpoint > startSetpoint || startSetpoint > maximumSetpoint ||
                maximumStep <= 0 || maximumStep > 100 || maximumSlewPerSecond <= 0 || rpmTolerance <= 0 || maximumRpm <= rpmTolerance || maximumRpm > 100000 ||
                settleMilliseconds < 200 || settleMilliseconds > 60000 || stabilityMilliseconds < 1000 || stabilityMilliseconds > 60000 ||
                maximumSampleAgeMilliseconds < 100 || maximumSampleAgeMilliseconds > 2000 || approachTimeoutSeconds < 10 || approachTimeoutSeconds > 600 ||
                new[] { minimumSetpoint, maximumSetpoint, startSetpoint, maximumStep }.Any(v => v != Math.Truncate(v)))
                throw new ArgumentException("Explicit whole-percent DUT limits and valid slew, RPM and settling limits are required.");
            if (kickSetpoint.HasValue && (!Finite(kickSetpoint.Value) || kickSetpoint < startSetpoint || kickSetpoint > maximumSetpoint ||
                kickSetpoint != Math.Truncate(kickSetpoint.Value) || kickMilliseconds < 200 || kickMilliseconds > 10000))
                throw new ArgumentException("Startup kick must stay within the DUT duty limits.");
            if (!kickSetpoint.HasValue && kickMilliseconds != 0) throw new ArgumentException("Kick duration requires a kick setpoint.");
        }
        public static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
    public sealed class PwmAcquisitionRequest
    {
        public string operationId { get; set; }
        public string owner { get; set; }
        public int channel { get; set; }
        public int leaseSeconds { get; set; }
        public PwmAcquisitionLimits limits { get; set; }
        public string leaseToken { get; set; }
        public double? rpm { get; set; }
        public double? setpoint { get; set; }
        public string operatorName { get; set; }
        public string reason { get; set; }
    }
    public sealed class AcquisitionRpm
    {
        public double? value { get; set; }
        public string source { get; set; } = "external-hid";
        public string sampleSessionId { get; set; }
        public long? sampleSequence { get; set; }
        public string sampleUtc { get; set; }
        public double? sampleAgeMs { get; set; }
        public bool? batteryLow { get; set; }
    }
    public sealed class PwmAmbientConfirmation
    {
        public string basis { get; set; } = "operator";
        public string operatorName { get; set; }
        public string reason { get; set; }
        public string confirmedUtc { get; set; }
        public string operationId { get; set; }
        public string sampleSessionId { get; set; }
    }
    public sealed class PwmAcquisitionStatus
    {
        public int protocolVersion { get; set; } = 1;
        public string backend { get; set; } = "pCUE";
        public string hardwareMode { get; set; } = "Real";
        public int channel { get; set; }
        public object capabilities { get; set; }
        public object lease { get; set; }
        public string phase { get; set; }
        public double? targetRpm { get; set; }
        public AcquisitionRpm rpm { get; set; }
        public object actuator { get; set; }
        public string fault { get; set; }
        public bool ambientConfirmed { get; set; }
        public PwmAmbientConfirmation ambientConfirmation { get; set; }
        public string observedUtc { get; set; }
    }
    public sealed class PwmAcquisitionResponse
    {
        public bool ok { get; set; }
        public PwmAcquisitionStatus status { get; set; }
        public string error { get; set; }
        public string leaseToken { get; set; }
    }
    public interface IPwmAcquisitionHardware
    {
        bool IsConnected { get; }
        bool TryAcquireAcquisition(object owner);
        void ReleaseAcquisition(object owner);
        int? ReadAcquisitionPower(int channel);
        bool WriteAcquisitionPower(object owner, int channel, int duty);
    }
    public interface IPwmAcquisitionTarget
    {
        PwmAcquisitionStatus GetAcquisitionStatus();
        Task<PwmAcquisitionResponse> ExecuteAcquisitionAsync(string action, PwmAcquisitionRequest request);
    }
}

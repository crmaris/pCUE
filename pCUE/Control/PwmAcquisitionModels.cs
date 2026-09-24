using System;
using System.Linq;
using System.Threading.Tasks;

namespace pCUE
{
    public static class CommanderAcquisitionFrames
    {
        public static bool TryReadRpm(byte[] frame, int count, out int rpm)
        {
            rpm = 0;
            if (frame == null || count < 4 || count > frame.Length || frame[1] != 0) return false;
            rpm = (frame[2] << 8) + frame[3];
            return true;
        }
        public static string ReadDriveMode(byte[] frame, int count, int channel)
        {
            if (frame == null || count < 8 || count > frame.Length || frame[1] != 0 || channel < 0 || channel > 5) return null;
            return frame[channel + 2] == 1 ? "dc-percent" : frame[channel + 2] == 2 ? "pwm" : null;
        }
    }
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
                maximumStep <= 0 || maximumStep > 100 || maximumSlewPerSecond <= 0 || maximumSlewPerSecond > 100 || rpmTolerance <= 0 || maximumRpm <= rpmTolerance || maximumRpm > 100000 ||
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
        // Omission retains the old four-pin-only contract; three-pin callers must opt in explicitly.
        public string driveMode { get; set; }
        public string operationId { get; set; }
        public string owner { get; set; }
        public int channel { get; set; }
        public int leaseSeconds { get; set; }
        public PwmAcquisitionLimits limits { get; set; }
        public string leaseToken { get; set; }
        public double? rpm { get; set; }
        public double? setpoint { get; set; }
        // Omission retains attended physical-off confirmation. The explicit stopped-PWM
        // basis records an energized fixture whose fan has independently stopped.
        public string basis { get; set; }
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
        // Acquisition protocol version (distinct from the remote-API protocolVersion in
        // PcueRemoteClient.MinimumProtocolVersion). Kept as protocolVersion for wire compat.
        public int protocolVersion { get; set; } = 1;
        public string backend { get; set; } = "pCUE";
        public string hardwareMode { get; set; } = "Real";
        public string driveMode { get; set; }
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
        string ReadAcquisitionDriveMode(int channel);
        AcquisitionRpm ReadAcquisitionRpm(int channel);
        bool WriteAcquisitionPower(object owner, int channel, int duty, string expectedDriveMode, Func<bool> writeAllowed);
    }
    public interface IPwmAcquisitionTarget
    {
        PwmAcquisitionStatus GetAcquisitionStatus();
        Task<PwmAcquisitionStatus> ReadAcquisitionStatusAsync(int channel);
        Task<PwmAcquisitionResponse> ExecuteAcquisitionAsync(string action, PwmAcquisitionRequest request);
    }
}

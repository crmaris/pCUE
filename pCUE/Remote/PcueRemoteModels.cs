using System.Collections.Generic;

namespace pCUE
{
    /// <summary>
    /// Stable, typed status contract shared by pCUE's built-in HTTP server and its in-app remote
    /// client. Property names intentionally match the existing lower-case JSON API so old CLI
    /// consumers keep working while newer pCUE clients can deserialize without dynamic objects.
    /// </summary>
    public sealed class PcueStatusSnapshot
    {
        public string app { get; set; }
        public int protocolVersion { get; set; }
        public string version { get; set; }
        public string machine { get; set; }
        public string updatedUtc { get; set; }
        public PcueCommanderStatus commander { get; set; }
        public PcueCpuStatus cpu { get; set; }
        public List<PcueFanStatus> fans { get; set; }
        public PcueTachometerStatus tachometer { get; set; }
        public PcueHoldStatus hold { get; set; }
        public PcueUiSettingsStatus settings { get; set; }
    }

    public sealed class PcueCommanderStatus
    {
        public bool connected { get; set; }
        public string firmware { get; set; }
    }

    public sealed class PcueMetricStatus
    {
        public double current { get; set; }
        public double min { get; set; }
        public double max { get; set; }
        public double average { get; set; }
    }

    public sealed class PcueCpuStatus
    {
        //These three strings are retained for compatibility with the existing CLI.
        public string temperature { get; set; }
        public string mhz { get; set; }
        public string load { get; set; }
        public bool monitoring { get; set; }
        public PcueMetricStatus temperatureStats { get; set; }
        public PcueMetricStatus mhzStats { get; set; }
        public PcueMetricStatus loadStats { get; set; }
    }

    public sealed class PcueFanStatus
    {
        public int fan { get; set; }
        public int rpm { get; set; }
        public string mode { get; set; }
        public int setpoint { get; set; }
        public double min { get; set; }
        public double max { get; set; }
        public double average { get; set; }
    }

    public sealed class PcueTachometerStatus
    {
        public bool connected { get; set; }
        public double? rpm { get; set; }
        public bool batteryLow { get; set; }
        public int? assignedFan { get; set; }
    }

    public sealed class PcueHoldStatus
    {
        public bool running { get; set; }
        public string status { get; set; }
        public string display { get; set; }
        public int? fan { get; set; }
        public int? duty { get; set; }
        public string dutySource { get; set; }
        public int target { get; set; }
        public bool tachoAdjust { get; set; }
    }

    public sealed class PcueUiSettingsStatus
    {
        public bool averageValues { get; set; }
        public bool autoStart { get; set; }
        public bool autoConnect { get; set; }
    }

    public sealed class PcueApiActionResponse
    {
        public bool ok { get; set; }
        public string error { get; set; }
        public PcueStatusSnapshot status { get; set; }
    }

    public sealed class PcueDiscoveryResult
    {
        public string app { get; set; }
        public int protocolVersion { get; set; }
        public string version { get; set; }
        public string host { get; set; }
        public string url { get; set; }
        public bool requiresToken { get; set; }
    }
}

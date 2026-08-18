using System.Text.Json.Serialization;

namespace KopiaMonitorApp.Models;

public class KopiaCheckResult
{
    [JsonPropertyName("AgentId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("DeviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("ContainerName")]
    public string ContainerName { get; set; } = string.Empty;

    [JsonPropertyName("Status")]
    public string Status { get; set; } = "OK"; // "OK", "CRITICAL", "ERROR"

    [JsonPropertyName("LastMaintenance")]
    public DateTime? LastFullRequireContentsDate { get; set; }

    [JsonPropertyName("HoursExtendedDetected")]
    public double HoursExtendedDetected { get; set; }

    [JsonPropertyName("GarbageCollectorSuccess")]
    public bool GarbageCollectorSuccess { get; set; }

    [JsonPropertyName("VerifySuccess")]
    public bool VerifySuccess { get; set; }

    [JsonPropertyName("VerifyPercent")]
    public double VerifyPercent { get; set; }

    [JsonPropertyName("Details")]
    public string Details { get; set; } = string.Empty;

    [JsonPropertyName("Alerts")]
    public List<string> Alerts { get; set; } = new();

    [JsonPropertyName("LastCheckedAt")]
    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("RawOutput")]
    public string RawOutput { get; set; } = string.Empty;
}
namespace KopiaMonitorApp.Models;

public class KopiaCheckResult
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Status { get; set; } = "OK"; // "OK", "CRITICAL", "ERROR"
    public DateTime? LastFullRequireContentsDate { get; set; }
    public double HoursExtendedDetected { get; set; }
    public bool GarbageCollectorSuccess { get; set; }
    public List<string> Alerts { get; set; } = new();
    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;
    public string RawOutput { get; set; } = string.Empty;
}

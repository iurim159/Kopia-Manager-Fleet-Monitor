namespace KopiaMonitorApp.Models;

public class KopiaDeviceConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public int MaxFullRequireDays { get; set; } = 14;
    public int ExpectedRetentionHours { get; set; } = 24;
}

public class AppSettingsConfig
{
    public int CheckIntervalMinutes { get; set; } = 30;
    public List<KopiaDeviceConfig> Devices { get; set; } = new();
}
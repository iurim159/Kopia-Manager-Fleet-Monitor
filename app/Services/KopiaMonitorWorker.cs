using System.Collections.Concurrent;
using KopiaMonitorApp.Models;
using Microsoft.Extensions.Options;

namespace KopiaMonitorApp.Services;

public class KopiaMonitorWorker : BackgroundService
{
    private readonly KopiaAnalyzerService _analyzerService;
    private readonly IOptions<AppSettingsConfig> _config;
    private readonly ILogger<KopiaMonitorWorker> _logger;

    // Cache thread-safe in memoria con gli ultimi risultati dei controlli
    public ConcurrentDictionary<string, KopiaCheckResult> LatestResults { get; } = new();

    public KopiaMonitorWorker(
        KopiaAnalyzerService analyzerService,
        IOptions<AppSettingsConfig> config,
        ILogger<KopiaMonitorWorker> logger)
    {
        _analyzerService = analyzerService;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Inizio scansione routine di manutenzione Kopia...");
            await RunChecksAsync(stoppingToken);

            var interval = TimeSpan.FromMinutes(_config.Value.CheckIntervalMinutes);
            await Task.Delay(interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    public async Task RunChecksAsync(CancellationToken cancellationToken = default)
    {
        foreach (var device in _config.Value.Devices)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                _logger.LogInformation("Esecuzione verifica sul container {Container} ({Name})...", device.ContainerName, device.Name);
                var result = await _analyzerService.AnalyzeDeviceAsync(device);
                LatestResults[device.Id] = result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante la verifica del container {Container}", device.ContainerName);
                LatestResults[device.Id] = new KopiaCheckResult
                {
                    DeviceId = device.Id,
                    DeviceName = device.Name,
                    ContainerName = device.ContainerName,
                    Status = "ERROR",
                    Alerts = new List<string> { $"Eccezione di sistema: {ex.Message}" },
                    LastCheckedAt = DateTime.UtcNow
                };
            }
        }
    }
}

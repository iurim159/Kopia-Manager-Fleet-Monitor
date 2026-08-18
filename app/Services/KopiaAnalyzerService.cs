using System.Globalization;
using System.Text.RegularExpressions;
using KopiaMonitorApp.Models;

namespace KopiaMonitorApp.Services;

public class KopiaAnalyzerService
{
    private readonly DockerExecService _dockerService;

    public KopiaAnalyzerService(DockerExecService dockerService)
    {
        _dockerService = dockerService;
    }

    public async Task<KopiaCheckResult> AnalyzeDeviceAsync(KopiaDeviceConfig config)
    {
        var result = new KopiaCheckResult
        {
            DeviceId = config.Id,
            DeviceName = config.Name,
            ContainerName = config.ContainerName
        };

        // 1. Eseguiamo il comando snapshot list
        string snapOutput = await _dockerService.RunKopiaCommandAsync(config.ContainerName, "kopia snapshot list -l --all");
        
        // 2. Eseguiamo il comando maintenance status
        string maintOutput = await _dockerService.RunKopiaCommandAsync(config.ContainerName, "kopia maintenance status");

        result.RawOutput = $"=== SNAPSHOTS ===\n{snapOutput}\n\n=== MAINTENANCE ===\n{maintOutput}";

        if (snapOutput.Contains("[ERRORE DOCKER EXEC]") || maintOutput.Contains("[ERRORE DOCKER EXEC]"))
        {
            result.Status = "ERROR";
            result.Alerts.Add($"Impossibile comunicare con il container {config.ContainerName}");
            return result;
        }

        // --- Regola 1: Analisi Manutenzione Completa (full-rewrite-contents o require-contents) ---
        // Cerchiamo le date sia nei log di snapshot che nell'output della maintenance
        var combinedOutput = snapOutput + "\n" + maintOutput;
        var dates = ExtractMaintenanceDates(combinedOutput);

        if (dates.Any())
        {
            result.LastFullRequireContentsDate = dates.Max();
            var daysSinceLastFull = (DateTime.UtcNow - result.LastFullRequireContentsDate.Value).TotalDays;

            if (daysSinceLastFull > config.MaxFullRequireDays)
            {
                result.Status = "CRITICAL";
                result.Alerts.Add($"L'ultima manutenzione completa risale a {daysSinceLastFull:F1} giorni fa (soglia max: {config.MaxFullRequireDays}gg).");
            }
        }
        else
        {
            result.Alerts.Add("Nessuna manutenzione completa ('full-rewrite-contents') o 'require-contents' trovata nei log.");
        }

        // --- Regola 2: Verifica esito Maintenance/GC ---
        if (maintOutput.Contains("ERROR") || maintOutput.Contains("failed"))
        {
            result.GarbageCollectorSuccess = false;
            result.Status = "CRITICAL";
            result.Alerts.Add("Manutenzione Kopia / GC fallita o con errori.");
        }
        else
        {
            result.GarbageCollectorSuccess = true;
        }

        return result;
    }

    private List<DateTime> ExtractMaintenanceDates(string output)
    {
        var dates = new List<DateTime>();

        // Intercetta sia 'full-rewrite-contents' (da maintenance status) sia 'full require-contents' / 'require-contents' (da snapshot logs)
        var regex = new Regex(@"(\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2})\s.*?(?:full-rewrite-contents|require-contents)", RegexOptions.IgnoreCase);
        var matches = regex.Matches(output);

        foreach (Match match in matches)
        {
            if (DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime parsedDate))
            {
                dates.Add(parsedDate);
            }
        }

        return dates;
    }
}
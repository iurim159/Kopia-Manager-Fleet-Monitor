using System.Diagnostics;
using MQTTnet;
using KopiaMonitorApp.Models;
using KopiaMonitorApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Configurazione delle opzioni
builder.Services.Configure<AppSettingsConfig>(builder.Configuration.GetSection("KopiaSettings"));
builder.Services.AddControllers();

// Registrazione Servizi
builder.Services.AddSingleton<DockerExecService>();
builder.Services.AddSingleton<KopiaAnalyzerService>();
builder.Services.AddSingleton<KopiaMonitorWorker>();

// Registrazione del servizio MQTT unificato (Sia Subscriber che Publisher)
builder.Services.AddSingleton<MqttSubscriberService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MqttSubscriberService>());

// Hosted Service per il worker periodico
builder.Services.AddHostedService(sp => sp.GetRequiredService<KopiaMonitorWorker>());

var app = builder.Build();

// Abilita i file statici (per la Dashboard HTML/JS)
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

// Endpoint per ottenere lo stato dei container
app.MapGet("/api/status", () =>
{
    var mqttList = MqttSubscriberService.ContainerStatuses.Values.Select(m => new 
    {
        deviceId = m.UniqueKey,
        deviceName = $"{m.AgentId} - {m.ContainerName}",
        agentId = m.AgentId,
        containerName = m.ContainerName,
        status = m.Status,
        lastFullRequireContentsDate = m.LastMaintenance,
        hoursExtendedDetected = 0,
        garbageCollectorSuccess = m.GarbageCollectorSuccess,
        verifySuccess = m.VerifySuccess,
        verifyPercent = m.VerifyPercent, 
        alerts = (m.Status == "OK") 
                    ? new string[] { } 
                    : new string[] { m.Details },
        lastCheckedAt = DateTime.UtcNow.ToString("o"),
        rawOutput = string.IsNullOrEmpty(m.RawOutput) ? m.Details : m.RawOutput
    });

    return Results.Ok(mqttList);
});

// Endpoint per la pressione del tasto "Esegui i controlli ora"
app.MapPost("/api/run-now", async (KopiaMonitorWorker worker, MqttSubscriberService mqttService) =>
{
    await worker.RunChecksAsync();

    try
    {
        await mqttService.PublishAsync("kopia/maintenance/kopia-iso-tn-01-kopia", "start");

        return Results.Ok(new 
        { 
            success = true, 
            message = "Comando di manutenzione inviato via MQTT con successo!" 
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore invio MQTT: {ex.Message}");
    }
});

// --- NUOVO ENDPOINT PER LA CONFIGURAZIONE AVANZATA ---
app.MapPost("/api/config/advanced", async (AdvancedConfigDto config, MqttSubscriberService mqttService) =>
{
    try
    {
        // Includiamo il DeviceId nel payload MQTT in modo che l'agente possa filtrarlo
        var agentPayload = new 
        {
            DeviceId = config.DeviceId, // <-- FONDAMENTALE PER IL FILTRAGGIO
            VerifyPercent = config.IntegrityVerifyPercentage,
            NormalVerifyIntervalHours = config.NormalVerifyIntervalHours,
            IntegrityVerifyIntervalHours = config.IntegrityVerifyIntervalHours
        };

        string jsonPayload = System.Text.Json.JsonSerializer.Serialize(agentPayload);

        // Invia il messaggio MQTT
        await mqttService.PublishAsync("kopia/config/advanced", jsonPayload);

        return Results.Ok(new { success = true, message = "Configurazione inviata con successo all'agente!" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore durante l'invio della configurazione MQTT: {ex.Message}");
    }
});

app.Run();

// --- MODELLO DTO PER RICEVERE IL PAYLOAD DAL FRONTEND ---
public class AdvancedConfigDto
{
    public string DeviceId { get; set; }
    public int NormalVerifyIntervalHours { get; set; }
    public int IntegrityVerifyIntervalHours { get; set; }
    public double IntegrityVerifyPercentage { get; set; }
}
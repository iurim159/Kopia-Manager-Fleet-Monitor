using System.Diagnostics;
using MQTTnet;
using KopiaMonitorApp.Models;
using KopiaMonitorApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Configurazione delle opzioni
builder.Services.Configure<AppSettingsConfig>(builder.Configuration.GetSection("KopiaSettings"));

// Registrazione Servizi
builder.Services.AddSingleton<DockerExecService>();
builder.Services.AddSingleton<KopiaAnalyzerService>();
builder.Services.AddSingleton<KopiaMonitorWorker>();

// Hosted Services (Worker in background e MQTT Subscriber)
builder.Services.AddHostedService(sp => sp.GetRequiredService<KopiaMonitorWorker>());
builder.Services.AddHostedService<MqttSubscriberService>();

var app = builder.Build();

// Abilita i file statici (per la Dashboard HTML/JS)
app.UseDefaultFiles();
app.UseStaticFiles();

// Minimal API Endpoints
app.MapGet("/api/status", () =>
{
    var mqttList = MqttSubscriberService.ContainerStatuses.Values.Select(m => new 
    {
        deviceId = m.UniqueKey,                                    // ID Univoco (es. AGENT_01_kopia-iso-tn-01-kopia)
        deviceName = $"{m.AgentId} - {m.ContainerName}",           // Titolo visualizzato sulla Card
        agentId = m.AgentId,
        containerName = m.ContainerName,
        status = m.Status,
        lastFullRequireContentsDate = m.LastMaintenance,
        hoursExtendedDetected = 0,
        garbageCollectorSuccess = m.GarbageCollectorSuccess,
        alerts = (m.Status == "OK") 
                    ? new string[] { } 
                    : new string[] { m.Details },
        lastCheckedAt = DateTime.UtcNow.ToString("o"),
        rawOutput = string.IsNullOrEmpty(m.RawOutput) ? m.Details : m.RawOutput
    });

    return Results.Ok(mqttList);
});

// Endpoint per la pressione del tasto "Esegui i controlli ora"
app.MapPost("/api/run-now", async (KopiaMonitorWorker worker, IMqttClient mqttClient) =>
{
    await worker.RunChecksAsync();

    try
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("kopia/maintenance/kopia-iso-tn-01-kopia")
            .WithPayload("start")
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await mqttClient.PublishAsync(message);

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

app.Run();
using System.Diagnostics;
using System.Text;
using MQTTnet;
using Renci.SshNet;
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
        deviceName = $"{m.AgentId} - {m.ContainerName} ({m.Mode})",
        agentId = m.AgentId,
        containerName = m.ContainerName,
        mode = m.Mode,
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

// Endpoint per la pressione del tasto "Esegui i controlli ora" (Legacy MQTT)
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

// --- ENDPOINT PER L'ESECUZIONE REMOTA SSH SEQUENZIALE 1:1 ---
app.MapPost("/api/run-now-ssh", async (RunNowSshRequestDto request) =>
{
    if (request?.Targets == null || request.Targets.Count == 0)
    {
        return Results.BadRequest(new { success = false, message = "Nessun target fornito." });
    }

    var results = new List<object>();

    // Esecuzione sequenziale 1 a 1 sui nodi
    foreach (var target in request.Targets)
    {
        try
        {
            AuthenticationMethod authMethod;
            
            if (!string.IsNullOrEmpty(target.Credential) && (target.Credential.Contains("BEGIN") || target.Credential.Contains("PRIVATE KEY")))
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(target.Credential));
                var keyFile = new PrivateKeyFile(stream);
                authMethod = new PrivateKeyAuthenticationMethod(target.Username, keyFile);
            }
            else
            {
                authMethod = new PasswordAuthenticationMethod(target.Username, target.Credential ?? string.Empty);
            }

            var connectionInfo = new Renci.SshNet.ConnectionInfo(target.Ip, 22, target.Username, authMethod);
            using var client = new Renci.SshNet.SshClient(connectionInfo);            
            
            client.Connect();

            if (client.IsConnected)
            {
                // Script Bash incapsulato con le triple virgolette
                string scriptContent = """
                #!/bin/bash

                MODE="${1:-normal}"
                AGENT_ID="${AGENT_ID:-UNKNOWN_AGENT}"
                CONTAINER_NAME="${CONTAINER_NAME:-kopia-iso-tn-01-kopia}"
                LOG_FILE="/tmp/kopia-maintenance-${MODE}.log"
                MQTT_HOST="${MQTT_HOST:-emqx}"
                MQTT_PORT="${MQTT_PORT:-1883}"
                MQTT_TOPIC="kopia/maintenance/${AGENT_ID}/${CONTAINER_NAME}/${MODE}"
                KOPIA_PASS="${KOPIA_PASSWORD:-test-password}"
                SETTINGS_FILE="/app/agent-settings.env"

                if [ -f "$SETTINGS_FILE" ]; then
                    source "$SETTINGS_FILE"
                fi

                if [ "$MODE" == "integrity" ]; then
                    VERIFY_PERCENT="${INTEGRITY_VERIFY_PERCENT:-1}"
                    CURRENT_INTERVAL="${INTEGRITY_VERIFY_INTERVAL_HOURS:-168}"
                    DETAILS="Verification (integrity al ${VERIFY_PERCENT}%) completata senza errori."
                else
                    VERIFY_PERCENT=""
                    CURRENT_INTERVAL="${NORMAL_VERIFY_INTERVAL_HOURS:-24}"
                    DETAILS="Manutenzione e Garbage Collector completati senza errori."
                fi

                LAST_SUCCESS_FILE="/tmp/kopia_last_success_${CONTAINER_NAME}_${MODE}"
                MAX_DAYS_ALLOWED=14

                echo "=== Inizio Manutenzione Kopia (${MODE}) [${CONTAINER_NAME}] da Agent [${AGENT_ID}]: $(date) ===" > "$LOG_FILE"

                STATUS="OK"
                GC_SUCCESS=true
                VERIFY_SUCCESS=true

                run_kopia() {
                    if command -v kopia &> /dev/null; then
                        KOPIA_PASSWORD="$KOPIA_PASS" kopia "$@"
                    else
                        docker exec -e KOPIA_PASSWORD="$KOPIA_PASS" "$CONTAINER_NAME" kopia "$@"
                    fi
                }

                CURRENT_EPOCH=$(date +%s)
                if [ -f "$LAST_SUCCESS_FILE" ]; then
                    LAST_SUCCESS_EPOCH=$(cat "$LAST_SUCCESS_FILE" | tr -d '[:space:]')
                    if [[ "$LAST_SUCCESS_EPOCH" =~ ^[0-9]+$ ]]; then
                        DIFF_SECONDS=$(( CURRENT_EPOCH - LAST_SUCCESS_EPOCH ))
                        MAX_SECONDS=$(( MAX_DAYS_ALLOWED * 86400 ))
                        if [ $DIFF_SECONDS -gt $MAX_SECONDS ]; then
                            STATUS="CRITICAL"
                            DAYS_AGO=$(( DIFF_SECONDS / 86400 ))
                            DETAILS="ATTENZIONE: Nessuna esecuzione ${MODE} riuscita negli ultimi ${DAYS_AGO} giorni."
                        fi
                    fi
                fi

                if [ "$MODE" == "normal" ]; then
                    run_kopia maintenance set --owner=me >> "$LOG_FILE" 2>&1
                    run_kopia maintenance run --full --safety=full >> "$LOG_FILE" 2>&1
                    if [ $? -ne 0 ]; then
                        STATUS="ERROR"
                        GC_SUCCESS=false
                        DETAILS="Errore durante l'esecuzione del Garbage Collector / Manutenzione Full."
                    fi
                    run_kopia maintenance status >> "$LOG_FILE" 2>&1
                fi

                if [ "$MODE" == "integrity" ]; then
                    run_kopia snapshot verify --verify-files-percent=${VERIFY_PERCENT} >> "$LOG_FILE" 2>&1
                    if [ $? -ne 0 ]; then
                        STATUS="ERROR"
                        VERIFY_SUCCESS=false
                        DETAILS="Errore durante la verifica dei file."
                    fi
                fi

                if [ "$GC_SUCCESS" = true ] && [ "$VERIFY_SUCCESS" = true ]; then
                    echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
                fi

                LAST_MAINTENANCE=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
                RAW_LOG_CONTENT=$(grep -vE "([a-f0-9]{32,}|blob|pack|index)" "$LOG_FILE" | tr -d '\r' | sed 's/\\/\\\\/g' | sed 's/"/\\"/g' | awk '{printf "%s\\n", $0}')

                PAYLOAD=$(cat <<EOF
                {
                "AgentId": "${AGENT_ID}",
                "ContainerName": "${CONTAINER_NAME}",
                "Mode": "${MODE}",
                "Status": "${STATUS}",
                "LastMaintenance": "${LAST_MAINTENANCE}",
                "GarbageCollectorSuccess": ${GC_SUCCESS},
                "VerifySuccess": ${VERIFY_SUCCESS},
                "VerifyPercent": ${VERIFY_PERCENT:-0},
                "TargetIntervalHours": ${CURRENT_INTERVAL},
                "Details": "${DETAILS}",
                "RawOutput": "${RAW_LOG_CONTENT}"
                }
                EOF
                )

                BACKEND_URL="${BACKEND_URL:-http://localhost:5000/api/maintenance-report}"

                # Esegue il curl verso il backend .NET
                curl -s -X POST "$BACKEND_URL" -H "Content-Type: application/json" -d "$PAYLOAD"

                # Stampa il log completo sullo standard output per catturarlo in cmd.Result
                echo "=== LOG COMPLETO DI MANUTENZIONE ==="
                cat "$LOG_FILE"
                """;

                // Esecuzione pulita usando END_SCRIPT come delimitatore per evitare conflitti con EOF interni
                var cmd = client.RunCommand($"bash -s -- normal << 'END_SCRIPT'\n{scriptContent}\nEND_SCRIPT");
                
                results.Add(new {
                    ip = target.Ip,
                    success = true,
                    output = cmd.Result,
                    error = cmd.Error
                });

                client.Disconnect();
            }
            else
            {
                results.Add(new { ip = target.Ip, success = false, message = "Impossibile stabilire la connessione SSH." });
            }
        }
        catch (Exception ex)
        {
            results.Add(new { ip = target.Ip, success = false, message = ex.Message });
        }
    }

    return Results.Ok(new { success = true, data = results });
});

// --- ENDPOINT PER RICEVERE IL REPORT HTTP DIRETTO DAL CLIENT ---
app.MapPost("/api/maintenance-report", (KopiaCheckResult report) =>
{
    // Generiamo la chiave unica in base all'AgentId e ContainerName (o usa un default)
    string agentId = string.IsNullOrEmpty(report.DeviceId) ? "UNKNOWN_AGENT" : report.DeviceId;
    string containerName = string.IsNullOrEmpty(report.ContainerName) ? "kopia-iso-tn-01-kopia" : report.ContainerName;
    string mode = "normal"; // o lo rilevi dal payload se lo aggiungi

    string uniqueKey = $"{agentId}_{containerName}_{mode}";

    // Mappiamo il risultato nel dizionario usato dalla dashboard (/api/status)
    var statusMessage = new MqttStatusMessage
    {
        AgentId = agentId,
        ContainerName = containerName,
        Mode = mode,
        Status = report.Status,
        LastMaintenance = report.LastFullRequireContentsDate?.ToString("o") ?? DateTime.UtcNow.ToString("o"),
        GarbageCollectorSuccess = report.GarbageCollectorSuccess,
        VerifySuccess = report.VerifySuccess,
        VerifyPercent = report.VerifyPercent,
        Details = report.Details,
        RawOutput = report.RawOutput
    };

    // Aggiorniamo la memoria condivisa della dashboard
    lock (MqttSubscriberService.ContainerStatuses)
    {
        MqttSubscriberService.ContainerStatuses[uniqueKey] = statusMessage;
    }

    return Results.Ok(new { success = true, message = "Report ricevuto e registrato con successo." });
});

// --- ENDPOINT PER LA CONFIGURAZIONE AVANZATA ---
app.MapPost("/api/config/advanced", async (AdvancedConfigDto config, MqttSubscriberService mqttService) =>
{
    try
    {
        var agentPayload = new 
        {
            DeviceId = config.DeviceId, 
            VerifyPercent = config.IntegrityVerifyPercentage,
            NormalVerifyIntervalHours = config.NormalVerifyIntervalHours,
            IntegrityVerifyIntervalHours = config.IntegrityVerifyIntervalHours
        };

        string jsonPayload = System.Text.Json.JsonSerializer.Serialize(agentPayload);
        await mqttService.PublishAsync("kopia/config/advanced", jsonPayload);

        return Results.Ok(new { success = true, message = "Configurazione inviata con successo all'agente!" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Errore durante l'invio della configurazione MQTT: {ex.Message}");
    }
});

app.Run();

// --- MODELLI DTO ---
public class AdvancedConfigDto
{
    public required string DeviceId { get; set; }
    public int NormalVerifyIntervalHours { get; set; }
    public int IntegrityVerifyIntervalHours { get; set; }
    public double IntegrityVerifyPercentage { get; set; }
}

public class SshTargetDto
{
    public required string Ip { get; set; }
    public required string Username { get; set; }
    public string? Credential { get; set; } 
}

public class RunNowSshRequestDto
{
    public required List<SshTargetDto> Targets { get; set; }
}
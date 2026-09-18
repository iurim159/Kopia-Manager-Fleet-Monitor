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

// --- ENDPOINT UNIFICATO: GESTISCE SIA LINUX (VIA SSH) CHE WINDOWS (VIA WMI) ---
app.MapPost("/api/run-now-ssh", async (HttpRequest httpRequest, RunNowSshRequestDto request) =>
{
    if (request?.Targets == null || request.Targets.Count == 0)
    {
        return Results.BadRequest(new { success = false, message = "Nessun target fornito." });
    }

    var scheme = httpRequest.Scheme;
    var host = httpRequest.Host;
    string serverBaseUrl = "http://10.80.90.182:5106"; // IP del server KopiaMonitorApp

    var results = new List<object>();

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

            bool sshConnected = false;
            string remoteOs = "UNKNOWN";

            try
            {
                var connectionInfo = new Renci.SshNet.ConnectionInfo(target.Ip, 22, target.Username, authMethod);
                using var client = new Renci.SshNet.SshClient(connectionInfo);
                client.Connect();
                
                if (client.IsConnected)
                {
                    sshConnected = true;
                    var osProbe = client.RunCommand("if [ -f /etc/os-release ]; then echo 'LINUX'; elif (Test-Path 'C:\\Windows'); then echo 'WINDOWS'; else echo 'LINUX'; fi");
                    remoteOs = osProbe.Result.Trim();
                    client.Disconnect();
                }
            }
            catch
            {
                sshConnected = false;
                remoteOs = "WINDOWS";
            }

            // ==========================================
            // RAMO 1: DISPOSITIVI LINUX VIA SSH
            // ==========================================
            if (sshConnected && !remoteOs.Equals("WINDOWS", StringComparison.OrdinalIgnoreCase))
            {
                var connectionInfo = new Renci.SshNet.ConnectionInfo(target.Ip, 22, target.Username, authMethod);
                using var client = new Renci.SshNet.SshClient(connectionInfo);
                client.Connect();

                string bashScriptContent = $$"""
                #!/bin/bash
                MODE="${1:-normal}"
                AGENT_ID="${AGENT_ID:-UNKNOWN_AGENT}"
                CONTAINER_NAME="${CONTAINER_NAME:-kopia-iso-tn-01-kopia}"
                LOG_FILE="/tmp/kopia-maintenance-${MODE}.log"
                KOPIA_PASS="${KOPIA_PASSWORD:-test-password}"
                SERVER_URL="{{serverBaseUrl}}/api/maintenance-report"
                SETTINGS_FILE="/app/agent-settings.env"

                if [ -f "$SETTINGS_FILE" ]; then
                    source "$SETTINGS_FILE"
                fi

                DETAILS="Manutenzione e Garbage Collector completati senza errori."
                LAST_SUCCESS_FILE="/tmp/kopia_last_success_${CONTAINER_NAME}_${MODE}"
                MAX_DAYS_ALLOWED=14

                echo "=== Inizio Manutenzione Kopia (${MODE}) [${CONTAINER_NAME}] da Agent [${AGENT_ID}]: $(date) ==" > "$LOG_FILE"

                STATUS="OK"
                GC_SUCCESS=true
                VERIFY_SUCCESS=true
                VERIFY_PERCENT=0

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

                if [ "$MODE" = "normal" ]; then
                    run_kopia maintenance set --owner=me >> "$LOG_FILE" 2>&1
                    run_kopia maintenance run --full --safety=full >> "$LOG_FILE" 2>&1
                    if [ $? -ne 0 ]; then
                        STATUS="ERROR"
                        GC_SUCCESS=false
                        DETAILS="Errore manutenzione Full."
                    fi
                elif [ "$MODE" = "integrity" ]; then
                    VERIFY_PERCENT="${VERIFY_PERCENT:-1}"
                    run_kopia snapshot verify --verify-files-percent="$VERIFY_PERCENT" >> "$LOG_FILE" 2>&1
                    if [ $? -ne 0 ]; then
                        STATUS="ERROR"
                        VERIFY_SUCCESS=false
                        DETAILS="Errore durante la verifica dei file (integrity Verify ${VERIFY_PERCENT}%)."
                    fi
                fi

                if [ "$GC_SUCCESS" = true ] && [ "$VERIFY_SUCCESS" = true ]; then
                    echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
                fi

                echo "=== Fine Manutenzione (${MODE}): $(date) ===" >> "$LOG_FILE"
                LAST_MAINTENANCE=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

                RAW_LOG_CONTENT=$(grep -vE "([a-f0-9]{32,}|blob|pack|index)" "$LOG_FILE" | tr -d '\r' | sed 's/\\/\\\\/g' | sed 's/"/\\"/g' | awk '{printf "%s\\n", $0}')

                STATUS_CODE=1
                if [ "$STATUS" = "CRITICAL" ]; then
                    STATUS_CODE=3
                elif [ "$STATUS" = "ERROR" ] || [ "$STATUS" = "WARNING" ]; then
                    STATUS_CODE=2
                fi

                GC_STATUS=1
                [ "$GC_SUCCESS" = false ] && GC_STATUS=2

                VERIFY_STATUS=1
                [ "$VERIFY_SUCCESS" = false ] && VERIFY_STATUS=2

                TIMESTAMP_ISO="$LAST_MAINTENANCE"

                PAYLOG_ID="LinuxAgent_{{target.Ip}}"

                PAYLOAD=$(cat <<EOF
                {
                  "version": 1,
                  "id": "${PAYLOG_ID}",
                  "timestamp": "${TIMESTAMP_ISO}",
                  "status": ${STATUS_CODE},
                  "components": [
                    {
                      "name": "garbage_collector",
                      "status": ${GC_STATUS},
                      "description": "${DETAILS}",
                      "timestamp": "${TIMESTAMP_ISO}",
                      "options": {
                        "mode": "${MODE}",
                        "container": "${CONTAINER_NAME}"
                      }
                    },
                    {
                      "name": "snapshot_verify",
                      "status": ${VERIFY_STATUS},
                      "description": "Verificata integrità con percentuale ${VERIFY_PERCENT}%",
                      "timestamp": "${TIMESTAMP_ISO}",
                      "options": {
                        "verify_percent": ${VERIFY_PERCENT}
                      }
                    }
                  ]
                }
                EOF
                )

                echo "$PAYLOAD" >> "/tmp/kopia-health.jsonl"
                curl -s -X POST -H "Content-Type: application/json" -d "$PAYLOAD" "$SERVER_URL" > /dev/null
                """;

                string cleanBashScript = bashScriptContent.Replace("\r\n", "\n");
                var cmd = client.RunCommand($"bash -s -- normal << 'END_SCRIPT'\n{cleanBashScript}\nEND_SCRIPT");                client.Disconnect();

                results.Add(new {
                    ip = target.Ip,
                    os = "LINUX (SSH)",
                    success = (cmd.ExitStatus == 0),
                    output = cmd.Result,
                    error = cmd.Error
                });
            }
            else
            {
                // ==========================================
                // RAMO 2: DISPOSITIVI WINDOWS VIA WMI
                // ==========================================
                string psScriptContent = $$"""
                param([string]$Mode = "normal")
                $ErrorActionPreference = 'Stop'
                $ContainerName = "kopia-windows-node"
                $TempDir = "C:\Windows\Temp"
                $LogFile = Join-Path $TempDir "kopia-maintenance-$Mode.log"
                $LastSuccessFile = Join-Path $TempDir "kopia_last_success_${ContainerName}_${Mode}"
                $KopiaPass = if ($env:KOPIA_PASSWORD) { $env:KOPIA_PASSWORD } else { "test-password" }
                $ServerReportUrl = "{{serverBaseUrl}}/api/maintenance-report"

                function Ensure-KopiaInstalled {
                    $existingCmd = Get-Command kopia -ErrorAction SilentlyContinue
                    if ($existingCmd) { return "kopia" }
                    $defaultWinPath = "C:\Program Files\Kopia\kopia.exe"
                    if (Test-Path $defaultWinPath) { return $defaultWinPath }
                    return "kopia"
                }

                $VerifyPercent = 1
                $Details = if ($Mode -eq "integrity") { "Verification (integrity al ${VerifyPercent}%) completata senza errori." } else { "Manutenzione e Garbage Collector completati senza errori." }
                $MaxDaysAllowed = 14
                $StartDate = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
                "=== Inizio Manutenzione Kopia ($Mode) [$ContainerName] via WMI: $StartDate ===" | Out-File -FilePath $LogFile -Encoding utf8

                $Status = "OK"
                $GcSuccess = $true
                $VerifySuccess = $true

                function Run-Kopia {
                    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$KopiaArgs)
                    $kopiaExe = Ensure-KopiaInstalled
                    
                    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
                    $processInfo.FileName = $kopiaExe
                    $processInfo.Arguments = ($KopiaArgs -join ' ')
                    $processInfo.RedirectStandardOutput = $true
                    $processInfo.RedirectStandardError = $true
                    $processInfo.UseShellExecute = $false
                    $processInfo.Environment["KOPIA_PASSWORD"] = $KopiaPass

                    $process = [System.Diagnostics.Process]::Start($processInfo)
                    $stdout = $process.StandardOutput.ReadToEnd()
                    $stderr = $process.StandardError.ReadToEnd()
                    $process.WaitForExit()

                    if ($stdout) { $stdout }
                    if ($stderr) { $stderr }
                    
                    $script:LASTEXITCODE = $process.ExitCode
                }

                $CurrentEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                if (Test-Path $LastSuccessFile) {
                    $LastSuccessEpochStr = (Get-Content $LastSuccessFile -Raw).Trim()
                    if ($LastSuccessEpochStr -match '^\d+$') {
                        $LastSuccessEpoch = [int64]$LastSuccessEpochStr
                        $DiffSeconds = $CurrentEpoch - $LastSuccessEpoch
                        $MaxSeconds = $MaxDaysAllowed * 86400
                        if ($DiffSeconds -gt $MaxSeconds) {
                            $Status = "CRITICAL"
                            $DaysAgo = [Math]::Floor($DiffSeconds / 86400)
                            $Details = "ATTENZIONE: Nessuna esecuzione $Mode riuscita negli ultimi $DaysAgo giorni."
                        }
                    } else {
                        $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
                    }
                } else {
                    $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
                }

                if ($Mode -eq "normal") {
                    Run-Kopia maintenance set --owner=me | Out-File -FilePath $LogFile -Append -Encoding utf8
                    $maintOutput = Run-Kopia maintenance run --full --safety=full
                    $maintOutput | Out-File -FilePath $LogFile -Append -Encoding utf8
                    if ($LASTEXITCODE -ne 0) { $Status = "ERROR"; $GcSuccess = $false; $Details = "Errore manutenzione Full." }
                } elseif ($Mode -eq "integrity") {
                    $verifyOutput = Run-Kopia snapshot verify --verify-files-percent=$VerifyPercent
                    $verifyOutput | Out-File -FilePath $LogFile -Append -Encoding utf8
                    if ($LASTEXITCODE -ne 0) { $Status = "ERROR"; $VerifySuccess = $false; $Details = "Errore durante la verifica dei file." }
                }

                if ($GcSuccess -eq $true -and $VerifySuccess -eq $true) {
                    $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
                }

                $rawLines = Get-Content $LogFile -ErrorAction SilentlyContinue
                $filteredLines = @()
                if ($rawLines) {
                    foreach ($line in $rawLines) {
                        if ($line -notmatch '([a-f0-9]{32,}|blob|pack|index)') {
                            $filteredLines += $line
                        }
                    }
                }
                $RawLogContent = [string]::Join("`n", $filteredLines)

                $StatusCode = 1
                if ($Status -eq "CRITICAL") {
                    $StatusCode = 3
                } elseif ($Status -eq "ERROR" -or $Status -eq "WARNING") {
                    $StatusCode = 2
                }

                $GcStatus = if ($GcSuccess) { 1 } else { 2 }
                $VerifyStatus = if ($VerifySuccess) { 1 } else { 2 }
                $TimestampIso = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

                $healthLogObj = @{
                    version = 1
                    id = "WindowsWMI_${target.Ip}"
                    timestamp = $TimestampIso
                    status = $StatusCode
                    components = @(
                        @{
                            name = "garbage_collector"
                            status = $GcStatus
                            description = $Details
                            timestamp = $TimestampIso
                            options = @{
                                mode = $Mode
                                container = $ContainerName
                            }
                        },
                        @{
                            name = "snapshot_verify"
                            status = $VerifyStatus
                            description = "Verificata integrità con percentuale $VerifyPercent%"
                            timestamp = $TimestampIso
                            options = @{
                                verify_percent = $VerifyPercent
                            }
                        }
                    )
                }
                
                $JsonPayload = $healthLogObj | ConvertTo-Json -Depth 5 -Compress
                
                $JsonPayload | Out-File -FilePath "C:\Windows\Temp\kopia-health.jsonl" -Append -Encoding utf8

                try {
                    $BodyBytes = [System.Text.Encoding]::UTF8.GetBytes($JsonPayload)
                    Invoke-RestMethod -Uri $ServerReportUrl -Method Post -Body $BodyBytes -ContentType "application/json; charset=utf-8" -TimeoutSec 10
                } catch { 
                    $_.Exception.Message | Out-File -FilePath "C:\Windows\Temp\kopia-maintenance-error.log" -Append -Encoding utf8
                }
                """;

                var plainTextBytes = Encoding.Unicode.GetBytes(psScriptContent);
                string encodedScript = Convert.ToBase64String(plainTextBytes);

                string wrappedCmd = $"powershell.exe -ExecutionPolicy Bypass -NoProfile -NonInteractive -EncodedCommand {encodedScript}";

                bool wmiSuccess = false;
                string wmiError = string.Empty;

                try
                {
                    wmiSuccess = ExecuteWmiCommand(target.Ip, target.Username, target.Credential, wrappedCmd);
                    if (!wmiSuccess)
                    {
                        wmiError = "Errore di avvio processo tramite WMI.";
                    }
                }
                catch (Exception wmiEx)
                {
                    wmiSuccess = false;
                    wmiError = wmiEx.Message;
                }

                results.Add(new {
                    ip = target.Ip,
                    os = "WINDOWS (WMI)",
                    success = wmiSuccess,
                    output = wmiSuccess ? "Processo avviato con successo via WMI in background." : string.Empty,
                    error = wmiError
                });
            }
        }
        catch (Exception ex)
        {
            results.Add(new { ip = target.Ip, success = false, message = ex.Message });
        }
    }

    return Results.Ok(new { success = true, data = results });
});

// --- ENDPOINT UNIFICATO: RICEZIONE REPORT ---
app.MapPost("/api/maintenance-report", async (HttpRequest httpRequest, ILogger<Program> logger) =>
{
    using var reader = new StreamReader(httpRequest.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    string jsonString = await reader.ReadToEndAsync();

    var options = new System.Text.Json.JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    HealthCheckReportDto? report;
    try 
    {
        report = System.Text.Json.JsonSerializer.Deserialize<HealthCheckReportDto>(jsonString, options);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Errore di deserializzazione JSON dal report ricevuto. Payload grezzo: {Payload}", jsonString);
        return Results.BadRequest(new { success = false, message = "Errore di parsing JSON o codifica non supportata." });
    }

    if (report == null) 
    {
        return Results.BadRequest(new { success = false, message = "Report vuoto o non valido." });
    }

    logger.LogInformation("Ricevuto report HTTP da ID: {Id}, Status: {Status}", report.Id, report.Status);

    string agentId = string.IsNullOrEmpty(report.Id) ? "UNKNOWN_AGENT" : report.Id;
    string containerName = "kopia-node";
    string mode = "normal";

    var gcComp = report.Components.FirstOrDefault(c => c.Name == "garbage_collector");
    var verifyComp = report.Components.FirstOrDefault(c => c.Name == "snapshot_verify");

    if (gcComp?.Options != null)
    {
        if (gcComp.Options.TryGetValue("container", out var cName) && cName != null) containerName = cName.ToString()!;
        if (gcComp.Options.TryGetValue("mode", out var mMode) && mMode != null) mode = mMode.ToString()!;
    }

    string statusText = report.Status switch
    {
        1 => "OK",
        2 => "WARNING",
        3 => "CRITICAL",
        _ => "UNKNOWN"
    };

    string uniqueKey = $"{agentId}_{containerName}_{mode}";
    logger.LogInformation("Chiave generata per la dashboard: {Key}", uniqueKey);

    double verifyPercent = 0.0001;
    if (verifyComp?.Options != null && verifyComp.Options.TryGetValue("verify_percent", out var percentObj) && percentObj != null)
    {
        if (percentObj is System.Text.Json.JsonElement jsonElem)
        {
            if (jsonElem.ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                verifyPercent = jsonElem.GetDouble();
            }
        }
        else
        {
            try { verifyPercent = Convert.ToDouble(percentObj); } catch { }
        }
    }

    var statusMessage = new MqttStatusMessage
    {
        AgentId = agentId,
        ContainerName = containerName,
        Mode = mode,
        Status = statusText,
        LastMaintenance = report.Timestamp,
        GarbageCollectorSuccess = gcComp?.Status == 1,
        VerifySuccess = verifyComp?.Status == 1,
        VerifyPercent = verifyPercent,
        Details = gcComp?.Description ?? verifyComp?.Description ?? "Esecuzione completata",
        RawOutput = string.Empty
    };

    lock (MqttSubscriberService.ContainerStatuses)
    {
        MqttSubscriberService.ContainerStatuses[uniqueKey] = statusMessage;
    }

    logger.LogInformation("Stato aggiornato in memoria. Totale elementi in ContainerStatuses: {Count}", MqttSubscriberService.ContainerStatuses.Count);

    return Results.Ok(new { success = true });
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

#pragma warning disable CA1416
static bool ExecuteWmiCommand(string ip, string username, string password, string command)
{
    var options = new System.Management.ConnectionOptions
    {
        Username = username,
        Password = password ?? string.Empty,
        EnablePrivileges = true
    };

    var scope = new System.Management.ManagementScope($"\\\\{ip}\\root\\cimv2", options);
    scope.Connect();

    var classInstance = new System.Management.ManagementClass(scope, new System.Management.ManagementPath("Win32_Process"), null);
    var inParams = classInstance.GetMethodParameters("Create");
    inParams["CommandLine"] = command;

    var outParams = classInstance.InvokeMethod("Create", inParams, null);
    uint returnValue = (uint)(outParams?["ReturnValue"] ?? 999);

    return returnValue == 0;
}
#pragma warning restore CA1416

// --- MODELLI DTO ---
public class HealthCheckReportDto
{
    public int Version { get; set; }
    public required string Id { get; set; }
    public string Timestamp { get; set; } = string.Empty;
    public int Status { get; set; }
    public List<HealthComponentDto> Components { get; set; } = new();
}

public class HealthComponentDto
{
    public required string Name { get; set; }
    public int Status { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public Dictionary<string, object>? Options { get; set; }
}

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
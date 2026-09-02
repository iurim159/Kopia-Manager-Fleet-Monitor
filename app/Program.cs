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
app.MapPost("/api/run-now-ssh", async (HttpRequest httpRequest, RunNowSshRequestDto request) =>
{
    if (request?.Targets == null || request.Targets.Count == 0)
    {
        return Results.BadRequest(new { success = false, message = "Nessun target fornito." });
    }

    var scheme = httpRequest.Scheme;
    var host = httpRequest.Host;
    string serverBaseUrl = $"{scheme}://{host}";

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
                // 1. Rilevamento dinamico dell'OS remoto tramite SSH
                var osProbe = client.RunCommand("powershell -Command \"if (Test-Path 'C:\\Windows') { 'WINDOWS' } else { 'LINUX' }\"");
                string remoteOs = osProbe.Result.Trim();
                
                string executionOutput = string.Empty;
                string executionError = string.Empty;

                if (remoteOs.Equals("LINUX", StringComparison.OrdinalIgnoreCase))
                {
                    // 2A. Esecuzione dello script BASH (Linux)
                    string bashScriptContent = $$"""
                    #!/bin/bash
                    MODE="${1:-normal}"
                    AGENT_ID="${AGENT_ID:-UNKNOWN_AGENT}"
                    CONTAINER_NAME="${CONTAINER_NAME:-kopia-iso-tn-01-kopia}"
                    LOG_FILE="/tmp/kopia-maintenance-${MODE}.log"
                    KOPIA_PASS="${KOPIA_PASSWORD:test-password}"
                    SERVER_URL="{{serverBaseUrl}}/api/maintenance-report"
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
                        else
                            echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
                        fi
                    else
                        echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
                    fi

                    if [ "$MODE" == "normal" ]; then
                        run_kopia maintenance set --owner=me >> "$LOG_FILE" 2>&1
                        run_kopia maintenance run --full --safety=full >> "$LOG_FILE" 2>&1
                        if [ $? -ne 0 ]; then
                            STATUS="ERROR"
                            GC_SUCCESS=false
                            DETAILS="Errore manutenzione Full."
                        fi
                    fi

                    if [ "$MODE" == "integrity" ]; then
                        run_kopia snapshot verify --verify-files-percent=${VERIFY_PERCENT} >> "$LOG_FILE" 2>&1
                        if [ $? -ne 0 ]; then
                            STATUS="ERROR"
                            VERIFY_SUCCESS=false
                            DETAILS="Errore verifica file."
                        fi
                    fi

                    if [ "$GC_SUCCESS" = true ] && [ "$VERIFY_SUCCESS" = true ]; then
                        echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
                    fi

                    echo "=== Fine Manutenzione (${MODE}): $(date) ===" >> "$LOG_FILE"
                    LAST_MAINTENANCE=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

                    RAW_LOG_CONTENT=$(grep -vE "([a-f0-9]{32,}|blob|pack|index)" "$LOG_FILE" | tr -d '\r' | sed 's/\\/\\\\/g' | sed 's/"/\\"/g' | awk '{printf "%s\\n", $0}')

                    PAYLOAD=$(cat <<EOF
                    {
                      "deviceId": "${AGENT_ID}",
                      "containerName": "${CONTAINER_NAME}",
                      "status": "${STATUS}",
                      "lastFullRequireContentsDate": "${LAST_MAINTENANCE}",
                      "garbageCollectorSuccess": ${GC_SUCCESS},
                      "verifySuccess": ${VERIFY_SUCCESS},
                      "verifyPercent": ${VERIFY_PERCENT:-0},
                      "details": "${DETAILS}",
                      "rawOutput": "${RAW_LOG_CONTENT}"
                    }
                    EOF
                    )

                    curl -s -X POST -H "Content-Type: application/json" -d "$PAYLOAD" "$SERVER_URL" > /dev/null
                    """;

                    var cmd = client.RunCommand($"bash -s -- normal << 'END_SCRIPT'\n{bashScriptContent}\nEND_SCRIPT");
                    executionOutput = cmd.Result;
                    executionError = cmd.Error;
                }
                else if (remoteOs.Equals("WINDOWS", StringComparison.OrdinalIgnoreCase))
                {
                    // 2B. Esecuzione dello script POWERSHELL con API HTTP integrata
                    string psScriptContent = $$"""
                        param([string]$Mode = "normal")
                        $AgentId = if ($env:AGENT_ID) { $env:AGENT_ID } else { "UNKNOWN_AGENT" }
                        $ContainerName = if ($env:CONTAINER_NAME) { $env:CONTAINER_NAME } else { "kopia-windows-node" }
                        $TempDir = [System.IO.Path]::GetTempPath()
                        $LogFile = Join-Path $TempDir "kopia-maintenance-$Mode.log"
                        $ConfigSettingsFile = Join-Path $TempDir "kopia-agent-settings.json"
                        $LastSuccessFile = Join-Path $TempDir "kopia_last_success_${ContainerName}_${Mode}"
                        $KopiaPass = if ($env:KOPIA_PASSWORD) { $env:KOPIA_PASSWORD } else { "test-password" }
                        $ServerReportUrl = "{{serverBaseUrl}}/api/maintenance-report"

                        function Ensure-KopiaInstalled {
                            if ($env:KOPIA_PATH -and (Test-Path $env:KOPIA_PATH)) { return $env:KOPIA_PATH }
                            $existingCmd = Get-Command kopia -ErrorAction SilentlyContinue
                            if ($existingCmd) { return "kopia" }
                            $defaultWinPath = "C:\Program Files\Kopia\kopia.exe"
                            if (Test-Path $defaultWinPath) { return $defaultWinPath }
                            $InstallDir = Join-Path $TempDir "kopia-bin"
                            if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null }
                            $TargetExe = Join-Path $InstallDir "kopia.exe"
                            $zipPath = Join-Path $InstallDir "kopia.zip"
                            try {
                                $downloadUrl = "https://github.com/kopia/kopia/releases/download/v0.18.2/kopia-0.18.2-windows-x64.zip"
                                Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath
                                Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
                                $extractedFolder = Get-ChildItem $InstallDir -Directory | Where-Object { $_.Name -like "kopia-*" }
                                if ($extractedFolder) {
                                    Move-Item -Path (Join-Path $extractedFolder.FullName "kopia.exe") -Destination $TargetExe -Force
                                }
                                if (Test-Path $TargetExe) { return $TargetExe }
                            } catch { }
                            return $null
                        }

                        $VerifyPercent = 1
                        $CurrentInterval = 24
                        if (Test-Path $ConfigSettingsFile) {
                            try {
                                $jsonConfig = Get-Content $ConfigSettingsFile -Raw | ConvertFrom-Json
                                if ($Mode -eq "integrity") {
                                    if ($jsonConfig.IntegrityVerifyPercentage -ne $null) { $VerifyPercent = [double]$jsonConfig.IntegrityVerifyPercentage }
                                    if ($jsonConfig.IntegrityVerifyIntervalHours -ne $null) { $CurrentInterval = [int]$jsonConfig.IntegrityVerifyIntervalHours }
                                } else {
                                    if ($jsonConfig.NormalVerifyIntervalHours -ne $null) { $CurrentInterval = [int]$jsonConfig.NormalVerifyIntervalHours }
                                }
                            } catch { }
                        }

                        if ($Mode -eq "integrity") {
                            $Details = "Verification (integrity al ${VerifyPercent}%) completata senza errori."
                        } else {
                            $VerifyPercent = $null
                            $Details = "Manutenzione e Garbage Collector completati senza errori."
                        }

                        $MaxDaysAllowed = 14
                        $StartDate = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
                        "=== Inizio Manutenzione Kopia ($Mode) [$ContainerName] da Agent [$AgentId]: $StartDate ===" | Out-File -FilePath $LogFile -Encoding utf8

                        $Status = "OK"
                        $GcSuccess = $true
                        $VerifySuccess = $true

                        function Run-Kopia {
                            param([Parameter(ValueFromRemainingArguments = $true)][string[]]$KopiaArgs)
                            $env:KOPIA_PASSWORD = $KopiaPass
                            $kopiaExe = Ensure-KopiaInstalled
                            if ($null -eq $kopiaExe -or !(Test-Path $kopiaExe)) { return }
                            
                            # Esegue il comando reindirizzando gli errori come stringhe normali senza generare eccezioni di PowerShell
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
                            
                            # Imposta la variabile globale di PowerShell per mantenere la compatibilità con i controlli dell'exit code
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
                        }

                        if ($Mode -eq "integrity") {
                            $verifyOutput = Run-Kopia snapshot verify --verify-files-percent=$VerifyPercent
                            $verifyOutput | Out-File -FilePath $LogFile -Append -Encoding utf8
                            if ($LASTEXITCODE -ne 0) { $Status = "ERROR"; $VerifySuccess = $false; $Details = "Errore verifica file." }
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

                        $payloadObj = @{
                            deviceId                = $AgentId
                            containerName           = $ContainerName
                            status                  = $Status
                            lastFullRequireContentsDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                            garbageCollectorSuccess = $GcSuccess
                            verifySuccess           = $VerifySuccess
                            verifyPercent           = if ($VerifyPercent -ne $null) { $VerifyPercent } else { 0 }
                            details                 = $Details
                            rawOutput               = $RawLogContent
                        }
                        
                        $JsonPayload = $payloadObj | ConvertTo-Json -Compress

                        try {
                            Invoke-RestMethod -Uri $ServerReportUrl -Method Post -Body $JsonPayload -ContentType "application/json" -TimeoutSec 10
                        } catch { }
                    """;

                    int chunkSize = 4000;
                    var encodedBytes = Encoding.Unicode.GetBytes(psScriptContent);
                    string fullEncoded = Convert.ToBase64String(encodedBytes);
                    var chunks = new List<string>();

                    for (int i = 0; i < fullEncoded.Length; i += chunkSize)
                    {
                        chunks.Add(fullEncoded.Substring(i, Math.Min(chunkSize, fullEncoded.Length - i)));
                    }

                    string remoteScriptPath = "C:\\Windows\\Temp\\kopia_run.ps1";
                    
                    var createResult = client.RunCommand($"powershell -Command \"[System.IO.File]::WriteAllText('{remoteScriptPath}', [string]::Empty)\"");
                    bool chunkSuccess = (createResult.ExitStatus == 0);

                    if (chunkSuccess)
                    {
                        foreach (var chunk in chunks)
                        {
                            string appendCmd = $"powershell -Command \"[System.IO.File]::AppendAllText('{remoteScriptPath}', [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('{chunk}')))\"";
                            var appendResult = client.RunCommand(appendCmd);
                            if (appendResult.ExitStatus != 0)
                            {
                                chunkSuccess = false;
                                executionError = $"Errore durante la scrittura del blocco remoto: {appendResult.Error}";
                                break;
                            }
                        }
                    }
                    else
                    {
                        executionError = $"Impossibile inizializzare lo script remoto: {createResult.Error}";
                    }

                    if (chunkSuccess)
                    {
                        var psCmd = client.RunCommand($"powershell -ExecutionPolicy Bypass -File \"{remoteScriptPath}\" -Mode normal");
                        executionOutput = psCmd.Result;
                        executionError = psCmd.Error;
                    }
                }
                else
                {
                    executionError = $"Sistema operativo non supportato o non rilevato ({remoteOs}).";
                }

                results.Add(new {
                    ip = target.Ip,
                    os = remoteOs,
                    success = string.IsNullOrEmpty(executionError),
                    output = executionOutput,
                    error = executionError
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
    string agentId = string.IsNullOrEmpty(report.DeviceId) ? "UNKNOWN_AGENT" : report.DeviceId;
    string containerName = string.IsNullOrEmpty(report.ContainerName) ? "kopia-iso-tn-01-kopia" : report.ContainerName;
    string mode = "normal";

    string uniqueKey = $"{agentId}_{containerName}_{mode}";

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
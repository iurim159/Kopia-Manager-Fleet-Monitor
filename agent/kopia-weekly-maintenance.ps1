param(
    [string]$Mode = "normal"
)

# --- CONFIGURAZIONE DINAMICA ---
$AgentId = if ($env:AGENT_ID) { $env:AGENT_ID } else { "UNKNOWN_AGENT" }
$ContainerName = if ($env:CONTAINER_NAME) { $env:CONTAINER_NAME } else { "kopia-windows-node" }

# Percorsi nativi Windows sicuri tramite la cartella TEMP di sistema
$TempDir = [System.IO.Path]::GetTempPath()
$LogFile = Join-Path $TempDir "kopia-maintenance-$Mode.log"
$ConfigSettingsFile = Join-Path $TempDir "kopia-agent-settings.json"
$LastSuccessFile = Join-Path $TempDir "kopia_last_success_${ContainerName}_${Mode}"

$MqttHost = if ($env:MQTT_HOST) { $env:MQTT_HOST } else { "localhost" }
$MqttPort = if ($env:MQTT_PORT) { $env:MQTT_PORT } else { "1883" }

# Topic MQTT specifico per agent, container e modalità
$MqttTopic = "kopia/maintenance/$AgentId/$ContainerName/$Mode"
$KopiaPass = if ($env:KOPIA_PASSWORD) { $env:KOPIA_PASSWORD } else { "test-password" }

# --- FUNZIONE DI AUTO-INSTALLAZIONE E RICERCA KOPIA PER WINDOWS ---
function Ensure-KopiaInstalled {
    # 1. Controlla variabile d'ambiente personalizzata
    if ($env:KOPIA_PATH -and (Test-Path $env:KOPIA_PATH)) { 
        return $env:KOPIA_PATH 
    }
    
    # 2. Controlla se è presente nel PATH di sistema
    $existingCmd = Get-Command kopia -ErrorAction SilentlyContinue
    if ($existingCmd) { 
        return "kopia" 
    }

    # 3. Controlla il percorso di installazione standard su Windows
    $defaultWinPath = "C:\Program Files\Kopia\kopia.exe"
    if (Test-Path $defaultWinPath) { 
        return $defaultWinPath 
    }

    # 4. Se non c'è da nessuna parte, lo scarica e installa automaticamente in TEMP
    Write-Host "Kopia non trovato su Windows. Avvio del download e dell'installazione automatica..." -ForegroundColor Yellow
    
    $InstallDir = Join-Path $TempDir "kopia-bin"
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    }
    $TargetExe = Join-Path $InstallDir "kopia.exe"
    $zipPath = Join-Path $InstallDir "kopia.zip"

    try {
        # URL ufficiale della release Windows x64 di Kopia
        $downloadUrl = "https://github.com/kopia/kopia/releases/download/v0.18.2/kopia-0.18.2-windows-x64.zip"
        
        Write-Host "Scaricamento di Kopia in corso..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath
        
        Write-Host "Estrazione dell'archivio..." -ForegroundColor Cyan
        Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
        
        # Sposta l'eseguibile nella cartella principale di installazione ripulendo la sottocartella
        $extractedFolder = Get-ChildItem $InstallDir -Directory | Where-Object { $_.Name -like "kopia-*" }
        if ($extractedFolder) {
            Move-Item -Path (Join-Path $extractedFolder.FullName "kopia.exe") -Destination $TargetExe -Force
        }
        
        if (Test-Path $TargetExe) {
            Write-Host "Kopia installato con successo in: $TargetExe" -ForegroundColor Green
            return $TargetExe
        }
    } catch {
        Write-Error "Impossibile scaricare o installare automaticamente Kopia: $_"
    }

    return $null
}

# --- LETTURA CONFIGURAZIONE DINAMICA (MQTT) ---
$VerifyPercent = 1
$CurrentInterval = 24

if (Test-Path $ConfigSettingsFile) {
    try {
        $jsonConfig = Get-Content $ConfigSettingsFile -Raw | ConvertFrom-Json
        if ($Mode -eq "integrity") {
            if ($jsonConfig.IntegrityVerifyPercentage -ne $null) {
                $VerifyPercent = [double]$jsonConfig.IntegrityVerifyPercentage
            } elseif ($jsonConfig.VerifyPercent -ne $null) {
                $VerifyPercent = [double]$jsonConfig.VerifyPercent
            }
            if ($jsonConfig.IntegrityVerifyIntervalHours -ne $null) {
                $CurrentInterval = [int]$jsonConfig.IntegrityVerifyIntervalHours
            }
        } else {
            if ($jsonConfig.NormalVerifyIntervalHours -ne $null) {
                $CurrentInterval = [int]$jsonConfig.NormalVerifyIntervalHours
            }
        }
    } catch {
        Write-Warning "Impossibile leggere il file di configurazione dinamica, uso i valori predefiniti."
    }
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
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$KopiaArgs
    )
    $env:KOPIA_PASSWORD = $KopiaPass

    # Sfrutta la funzione di auto-installazione e ricerca centralizzata
    $kopiaExe = Ensure-KopiaInstalled

    if ($null -eq $kopiaExe -or !(Test-Path $kopiaExe)) {
        Write-Error "Eseguibile di Kopia non trovato e impossibile da scaricare. Verifica la connessione o imposta la variabile d'ambiente KOPIA_PATH."
        return
    }

    & $kopiaExe @KopiaArgs 2>&1
}

# --- 0. CONTROLLO DEI 14 GIORNI ---
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
            $Details = "ATTENZIONE: Nessuna esecuzione $Mode riuscita negli ultimi $DaysAgo giorni (limite: $MaxDaysAllowed giorni)."
            "CRITICAL: Nessuna esecuzione $Mode riuscita negli ultimi $DaysAgo giorni." | Out-File -FilePath $LogFile -Append -Encoding utf8
        }
    } else {
        $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
    }
} else {
    $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
}

# --- ESECUZIONE IN BASE ALLA MODALITÀ ---
if ($Mode -eq "normal") {
    Run-Kopia maintenance set --owner=me | Out-File -FilePath $LogFile -Append -Encoding utf8
    
    $maintOutput = Run-Kopia maintenance run --full --safety=full
    $maintOutput | Out-File -FilePath $LogFile -Append -Encoding utf8

    if ($LASTEXITCODE -ne 0) {
        $Status = "ERROR"
        $GcSuccess = $false
        $Details = "Errore durante l'esecuzione del Garbage Collector / Manutenzione Full."
    }

    Run-Kopia maintenance status | Out-File -FilePath $LogFile -Append -Encoding utf8
    $VerifySuccess = $true
}

if ($Mode -eq "integrity") {
    "--- Inizio Verification (integrity - ${VerifyPercent}%) ---" | Out-File -FilePath $LogFile -Append -Encoding utf8
    $verifyOutput = Run-Kopia snapshot verify --verify-files-percent=$VerifyPercent
    $verifyOutput | Out-File -FilePath $LogFile -Append -Encoding utf8
    $VerifyExitCode = $LASTEXITCODE

    if ($VerifyExitCode -ne 0) {
        $Status = "ERROR"
        $VerifySuccess = $false
        $Details = "Errore durante la verifica dei file (integrity Verify ${VerifyPercent}%)."
    } else {
        $VerifySuccess = $true
    }
}

# --- AGGIORNAMENTO DEL TIMESTAMP ---
if ($GcSuccess -eq $true -and $VerifySuccess -eq $true) {
    $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
}

$EndDate = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
"=== Fine Manutenzione ($Mode): $EndDate ===" | Out-File -FilePath $LogFile -Append -Encoding utf8

$LastMaintenance = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

$rawLines = Get-Content $LogFile -ErrorAction SilentlyContinue
$filteredLines = @()
if ($rawLines) {
    foreach ($line in $rawLines) {
        if ($line -notmatch '([a-f0-9]{32,}|blob|pack|index)') {
            $filteredLines += ($line -replace '\\', '\\\\' -replace '"', '\"')
        }
    }
}
$RawLogContent = [string]::Join("`n", $filteredLines)

$payloadObj = @{
    AgentId                 = $AgentId
    ContainerName           = $ContainerName
    Mode                    = $Mode
    Status                  = $Status
    LastMaintenance         = $LastMaintenance
    GarbageCollectorSuccess = $GcSuccess
    VerifySuccess           = $VerifySuccess
    VerifyPercent           = if ($VerifyPercent -ne $null) { $VerifyPercent } else { 0 }
    TargetIntervalHours     = $CurrentInterval
    Details                 = $Details
    RawOutput               = $RawLogContent
}

$Payload = $payloadObj | ConvertTo-Json -Compress

Write-Host "Invio messaggio da '$AgentId' per '$ContainerName' ($Mode) a ${MqttHost}:${MqttPort} [$MqttTopic]..." -ForegroundColor Cyan

# Utilizzo di mqttx per dialogare con EMQX
if (Get-Command mqttx -ErrorAction SilentlyContinue) {
    & mqttx pub -h "$MqttHost" -p "$MqttPort" -t "$MqttTopic" -m "$Payload"
} else {
    Write-Warning "Il comando 'mqttx' non è disponibile in questo ambiente Windows. Impossibile inviare il payload via MQTT."
}

Write-Host "modalità: $AgentId, container: $ContainerName, tipo: $Mode, stato: $Status, percentuale verifica: $(if($VerifyPercent){"$VerifyPercent%"}else{"N/A"}), intervallo: $CurrentInterval h"
# --- CONFIGURAZIONE DINAMICA ---
$AgentId = if ($env:AGENT_ID) { $env:AGENT_ID } else { "UNKNOWN_AGENT" }
$ContainerName = if ($env:CONTAINER_NAME) { $env:CONTAINER_NAME } else { "kopia-iso-tn-01-kopia" }
$LogFile = "/tmp/kopia-maintenance.log"
$MqttHost = if ($env:MQTT_HOST) { $env:MQTT_HOST } else { "localhost" }
$MqttPort = if ($env:MQTT_PORT) { $env:MQTT_PORT } else { "1883" }

# Topic dinamico con AGENT_ID
$MqttTopic = "kopia/maintenance/$AgentId/$ContainerName"
$KopiaPass = if ($env:KOPIA_PASSWORD) { $env:KOPIA_PASSWORD } else { "test-password" }

# --- LETTURA CONFIGURAZIONE DINAMICA (MQTT) ---
$ConfigSettingsFile = "/tmp/kopia-agent-settings.json"
$VerifyPercent = 0.0001 # Valore di default

if (Test-Path $ConfigSettingsFile) {
    try {
        $jsonConfig = Get-Content $ConfigSettingsFile -Raw | ConvertFrom-Json
        if ($jsonConfig.VerifyPercent -ne $null) {
            $VerifyPercent = [double]$jsonConfig.VerifyPercent
        }
    } catch {
        Write-Warning "Impossibile leggere il file di configurazione dinamica, uso il valore predefinito: $VerifyPercent"
    }
}

# File persistente per tracciare l'ultimo successo (per il controllo dei 14 giorni)
$LastSuccessFile = "/tmp/kopia_last_success_$ContainerName"
$MaxDaysAllowed = 14

$StartDate = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
"=== Inizio Manutenzione Kopia ($ContainerName) da Agent [$AgentId]: $StartDate ===" | Out-File -FilePath $LogFile -Encoding utf8

$Status = "OK"
$Details = "Manutenzione, Garbage Collector e Verification ($VerifyPercent%) completati senza errori."
$GcSuccess = $true
$VerifySuccess = $true

function Run-Kopia {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$KopiaArgs
    )
    
    $env:KOPIA_PASSWORD = $KopiaPass
    $kopiaCmd = Get-Command kopia -ErrorAction SilentlyContinue

    if ($kopiaCmd) {
        & kopia @KopiaArgs 2>&1
    } else {
        & docker exec -e KOPIA_PASSWORD=$KopiaPass $ContainerName kopia @KopiaArgs 2>&1
    }
}

# --- 0. CONTROLLO DEI 14 GIORNI (BASATO SUL PASSATO) ---
$CurrentEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

if (Test-Path $LastSuccessFile) {
    # Leggiamo il file pulendo eventuali spazi o ritorni a capo
    $LastSuccessEpochStr = (Get-Content $LastSuccessFile -Raw).Trim()
    
    # Verifichiamo che sia un numero valido
    if ($LastSuccessEpochStr -match '^\d+$') {
        $LastSuccessEpoch = [int64]$LastSuccessEpochStr
        $DiffSeconds = $CurrentEpoch - $LastSuccessEpoch
        $MaxSeconds = $MaxDaysAllowed * 86400

        if ($DiffSeconds -gt $MaxSeconds) {
            $Status = "CRITICAL"
            $DaysAgo = [Math]::Floor($DiffSeconds / 86400)
            $Details = "ATTENZIONE: Nessuna manutenzione completa riuscita negli ultimi $DaysAgo giorni (limite: $MaxDaysAllowed giorni)."
            "CRITICAL: Nessuna manutenzione completa riuscita negli ultimi $DaysAgo giorni (limite: $MaxDaysAllowed giorni)." | Out-File -FilePath $LogFile -Append -Encoding utf8
        }
    } else {
        # Se il file contiene per errore una stringa non numerica, lo resettiamo
        $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
    }
} else {
    # Prima esecuzione in assoluto: creiamo il file con il timestamp attuale
    $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
}

# 1. Assegna l'owner della manutenzione all'utente corrente (evita il blocco 'auto')
Run-Kopia maintenance set --owner=me | Out-File -FilePath $LogFile -Append -Encoding utf8

# 2. Esecuzione Manutenzione Full e Garbage Collector
$maintOutput = Run-Kopia maintenance run --full --safety=full
$maintOutput | Out-File -FilePath $LogFile -Append -Encoding utf8

if ($LASTEXITCODE -ne 0) {
    $Status = "ERROR"
    $GcSuccess = $false
    $Details = "Errore durante l'esecuzione del Garbage Collector / Manutenzione Full."
}

# 3. Status della Manutenzione
Run-Kopia maintenance status | Out-File -FilePath $LogFile -Append -Encoding utf8

# 4. Verification rapida sui file con la percentuale dinamica
"--- Inizio Verification ($VerifyPercent%) ---" | Out-File -FilePath $LogFile -Append -Encoding utf8
$verifyOutput = Run-Kopia snapshot verify --verify-files-percent=$VerifyPercent
$verifyOutput | Out-File -FilePath $LogFile -Append -Encoding utf8
$VerifyExitCode = $LASTEXITCODE

if ($VerifyExitCode -ne 0) {
    $Status = "ERROR"
    $VerifySuccess = $false
    if ($GcSuccess -eq $true) {
        $Details = "Errore durante la verifica dei file (Snapshot Verify $VerifyPercent%)."
    } else {
        $Details = "Errori riscontrati sia nel Garbage Collector che nella Verification."
    }
} else {
    $VerifySuccess = $true
}

# --- AGGIORNAMENTO DEL TIMESTAMP ---
# Se la manutenzione e la verifica odierne sono andate bene, aggiorniamo il file col timestamp attuale
if ($GcSuccess -eq $true -and $VerifySuccess -eq $true) {
    $CurrentEpoch | Out-File -FilePath $LastSuccessFile -Encoding utf8
}

$EndDate = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
"=== Fine Manutenzione: $EndDate ===" | Out-File -FilePath $LogFile -Append -Encoding utf8

$LastMaintenance = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

# Escludiamo le righe piene di hash / ID dei blob (equivalente al filtro grep -vE)
$rawLines = Get-Content $LogFile -ErrorAction SilentlyContinue
$filteredLines = foreach ($line in $rawLines) {
    if ($line -notmatch '([a-f0-9]{32,}|blob|pack|index)') {
        # Esegue l'escaping di backslash e virgolette per la validità JSON
        $line -replace '\\', '\\\\' -replace '"', '\"'
    }
}
$RawLogContent = [string]::Join("\n", $filteredLines)

# Creazione del Payload JSON (includendo la VerifyPercent effettiva utilizzata)
$payloadObj = @{
    AgentId                 = $AgentId
    ContainerName           = $ContainerName
    Status                  = $Status
    LastMaintenance         = $LastMaintenance
    GarbageCollectorSuccess = $GcSuccess
    VerifySuccess           = $VerifySuccess
    VerifyPercent           = $VerifyPercent
    Details                 = $Details
    RawOutput               = $RawLogContent
}

$Payload = $payloadObj | ConvertTo-Json -Compress

Write-Host "Invio messaggio da '$AgentId' per '$ContainerName' a ${MqttHost}:${MqttPort} [$MqttTopic]..."

# Invio tramite mosquitto_pub se presente nel sistema Windows
if (Get-Command mosquitto_pub -ErrorAction SilentlyContinue) {
    & mosquitto_pub -h "$MqttHost" -p "$MqttPort" -t "$MqttTopic" -m "$Payload"
} else {
    Write-Warning "Il comando 'mosquitto_pub' non è disponibile in questo ambiente. Impossibile inviare il messaggio MQTT."
}
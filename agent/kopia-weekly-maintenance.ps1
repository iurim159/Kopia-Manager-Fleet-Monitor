param(
    [string]$Mode = "normal"
)

# --- CONFIGURAZIONE ---
$AgentId = if ($env:AGENT_ID) { $env:AGENT_ID } else { "UNKNOWN_AGENT" }
$ContainerName = if ($env:CONTAINER_NAME) { $env:CONTAINER_NAME } else { "kopia-windows-node" }

$TempDir = [System.IO.Path]::GetTempPath()
$LogFile = Join-Path $TempDir "kopia-maintenance-$Mode.log"
$ConfigSettingsFile = Join-Path $TempDir "kopia-agent-settings.json"
$LastSuccessFile = Join-Path $TempDir "kopia_last_success_${ContainerName}_${Mode}"

$MqttHost = if ($env:MQTT_HOST) { $env:MQTT_HOST } else { "localhost" }
$MqttPort = if ($env:MQTT_PORT) { [int]$env:MQTT_PORT } else { 1883 }
$MqttTopic = "kopia/maintenance/$AgentId/$ContainerName/$Mode"
$KopiaPass = if ($env:KOPIA_PASSWORD) { $env:KOPIA_PASSWORD } else { "test-password" }

$StartDate = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
"=== Inizio Manutenzione Kopia ($Mode) [$ContainerName] da Agent [$AgentId]: $StartDate ===" | Out-File -FilePath $LogFile -Encoding utf8

# --- 1. GESTIONE INSTALLAZIONE KOPIA (UNICA E BLOCCANTE) ---
$script:ResolvedKopiaExe = $null

function Get-KopiaExecutable {
    if ($script:ResolvedKopiaExe -and (Test-Path $script:ResolvedKopiaExe)) {
        return $script:ResolvedKopiaExe
    }

    if ($env:KOPIA_PATH -and (Test-Path $env:KOPIA_PATH)) {
        $script:ResolvedKopiaExe = (Resolve-Path $env:KOPIA_PATH).Path
        return $script:ResolvedKopiaExe
    }
    
    $existingCmd = Get-Command kopia -ErrorAction SilentlyContinue
    if ($existingCmd) {
        $script:ResolvedKopiaExe = "kopia"
        return $script:ResolvedKopiaExe
    }

    $defaultWinPath = "C:\Program Files\Kopia\kopia.exe"
    if (Test-Path $defaultWinPath) {
        $script:ResolvedKopiaExe = $defaultWinPath
        return $script:ResolvedKopiaExe
    }

    $TargetExe = Join-Path $TempDir "kopia-bin\kopia.exe"
    if (Test-Path $TargetExe) {
        $script:ResolvedKopiaExe = $TargetExe
        return $TargetExe
    }

    Write-Host "Kopia non trovato sul sistema. Download e installazione automatica in corso..." -ForegroundColor Yellow
    "Kopia non trovato. Avvio download automatico..." | Out-File -FilePath $LogFile -Append -Encoding utf8
    
    $InstallDir = Join-Path $TempDir "kopia-bin"
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    }
    $zipPath = Join-Path $InstallDir "kopia.zip"

    try {
        $downloadUrl = "https://github.com/kopia/kopia/releases/download/v0.18.2/kopia-0.18.2-windows-x64.zip"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UserAgent "PowerShell-KopiaManager"
        Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
        
        $extractedFolder = Get-ChildItem $InstallDir -Directory | Where-Object { $_.Name -like "kopia-*" }
        if ($extractedFolder) {
            Move-Item -Path (Join-Path $extractedFolder.FullName "kopia.exe") -Destination $TargetExe -Force
        }
        
        if (Test-Path $TargetExe) {
            Write-Host "Kopia installato con successo in: $TargetExe" -ForegroundColor Green
            "Kopia installato con successo in: $TargetExe" | Out-File -FilePath $LogFile -Append -Encoding utf8
            $script:ResolvedKopiaExe = $TargetExe
            return $TargetExe
        }
    } catch {
        $errMessage = "Impossibile scaricare Kopia automaticamente: $_"
        Write-Error $errMessage
        $errMessage | Out-File -FilePath $LogFile -Append -Encoding utf8
    }

    return $null
}

$KopiaExePath = Get-KopiaExecutable
if ($null -eq $KopiaExePath) {
    $critMsg = "ERRORE CRITICO: Impossibile trovare o installare Kopia. Interruzione script."
    Write-Error $critMsg
    $critMsg | Out-File -FilePath $LogFile -Append -Encoding utf8
    exit 1
}

# --- 2. INVIO MQTT NATIVO VIA TCP ---
function Send-MqttMessage {
    param(
        [string]$Broker,
        [int]$Port,
        [string]$Topic,
        [string]$Payload
    )

    try {
        $socket = [Net.Sockets.TcpClient]::new()
        $socket.Connect($Broker, $Port)
        if (-not $socket.Connected) { return $false }

        $stream = $socket.GetStream()

        $protocolName = [System.Text.Encoding]::UTF8.GetBytes("MQTT")
        $variableHeader = [byte[]]@(0x00, 0x04) + $protocolName + [byte[]]@(0x04, 0x02, 0x00, 0x3C, 0x00, 0x00)
        $connectPacket = [byte[]]@(0x10) + [byte[]]$variableHeader.Length + $variableHeader

        $stream.Write($connectPacket, 0, $connectPacket.Length)
        Start-Sleep -Milliseconds 200

        $topicBytes = [System.Text.Encoding]::UTF8.GetBytes($Topic)
        $payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($Payload)
        
        $topicLen = [byte[]]@([BitConverter]::GetBytes([uint16]$topicBytes.Length)[1], [BitConverter]::GetBytes([uint16]$topicBytes.Length)[0])
        
        # Calcolo corretto Remaining Length (gestisce payload grandi oltre 127 byte)
        $remainingLength = $topicLen.Length + $topicBytes.Length + $payloadBytes.Length
        $encodedRemainingLength = @()
        do {
            $digit = $remainingLength % 128
            $remainingLength = [Math]::Floor($remainingLength / 128)
            if ($remainingLength -gt 0) {
                $digit = $digit -bor 128
            }
            $encodedRemainingLength += [byte]$digit
        } while ($remainingLength -gt 0)

        $publishHeader = [byte[]]@(0x30) + $encodedRemainingLength
        
        $publishPacket = $publishHeader + $topicLen + $topicBytes + $payloadBytes
        $stream.Write($publishPacket, 0, $publishPacket.Length)

        $disconnectPacket = [byte[]]@(0xE0, 0x00)
        $stream.Write($disconnectPacket, 0, $disconnectPacket.Length)

        $stream.Close()
        $socket.Close()
        return $true
    } catch {
        Write-Warning "Errore nell'invio MQTT via TCP nativo: $_"
        return $false
    }
}

# --- ESECUZIONE LOGICA ---
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
    } catch {}
}

if ($Mode -eq "integrity") {
    $Details = "Verification (integrity al ${VerifyPercent}%) completata senza errori."
} else {
    $VerifyPercent = $null
    $Details = "Manutenzione e Garbage Collector completati senza errori."
}

$Status = "OK"
$GcSuccess = $true
$VerifySuccess = $true

function Run-Kopia {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$KopiaArgs)
    $env:KOPIA_PASSWORD = $KopiaPass
    & $KopiaExePath @KopiaArgs 2>&1
}

if ($Mode -eq "normal") {
    Run-Kopia maintenance set --owner=me | Out-File -FilePath $LogFile -Append -Encoding utf8
    $maintOutput = Run-Kopia maintenance run --full --safety=full
    $maintOutput | Out-File -FilePath $LogFile -Append -Encoding utf8

    if ($LASTEXITCODE -ne 0) {
        $Status = "ERROR"
        $GcSuccess = $false
        $Details = "Errore durante l'esecuzione del Garbage Collector."
    }
    Run-Kopia maintenance status | Out-File -FilePath $LogFile -Append -Encoding utf8
}

if ($Mode -eq "integrity") {
    $verifyOutput = Run-Kopia snapshot verify --verify-files-percent=$VerifyPercent
    $verifyOutput | Out-File -FilePath $LogFile -Append -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        $Status = "ERROR"
        $VerifySuccess = $false
        $Details = "Errore durante la verifica dei file."
    }
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
$sent = Send-MqttMessage -Broker $MqttHost -Port $MqttPort -Topic $MqttTopic -Payload $Payload

Write-Host "modalità: $AgentId, container: $ContainerName, tipo: $Mode, stato: $Status, percentuale verifica: $(if($VerifyPercent){"$VerifyPercent%"}else{"N/A"}), intervallo: $CurrentInterval h"
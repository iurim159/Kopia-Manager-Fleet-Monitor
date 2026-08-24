$MqttHost = if ($env:MQTT_HOST) { $env:MQTT_HOST } else { "localhost" }
$MqttPort = if ($env:MQTT_PORT) { [int]$env:MQTT_PORT } else { 1883 }
$Topic = "kopia/config/advanced"
$ConfigFile = Join-Path ([System.IO.Path]::GetTempPath()) "kopia-agent-settings.json"

Write-Host "Avvio del listener MQTT nativo (TCP) avanzato su Windows..." -ForegroundColor Cyan

# --- FUNZIONE DI SUPPORTO (DEFINITA PRIMA DELL'USO) ---
function Process-MqttMessage {
    param($Buffer, $BytesRead, $ConfigFile)
    
    $packetType = ($Buffer[0] -band 0xF0)
    if ($packetType -eq 0x30) {
        $idx = 1
        $multiplier = 1
        $remainingLength = 0
        do {
            $encodedByte = $Buffer[$idx++]
            $remainingLength += ($encodedByte -band 127) * $multiplier
            $multiplier *= 128
        } while (($encodedByte -band 128) -ne 0)

        $topicLength = [BitConverter]::ToUInt16([byte[]]@($Buffer[$idx+1], $Buffer[$idx]), 0)
        $idx += 2 + $topicLength

        $qos = ($Buffer[0] -band 0x06) -shr 1
        if ($qos -gt 0) { $idx += 2 }

        $payloadLength = ($BytesRead - $idx)
        if ($payloadLength -gt 0) {
            $jsonMessage = [System.Text.Encoding]::UTF8.GetString($Buffer, $idx, $payloadLength).TrimEnd([char]0)
            Write-Host "Configurazione ricevuta (anche Retained): $jsonMessage" -ForegroundColor Green
            
            try {
                $parsedObj = $jsonMessage | ConvertFrom-Json
                $verifyPercent = if ($parsedObj.VerifyPercent) { $parsedObj.VerifyPercent } else { 1 }
                $maintCron = if ($parsedObj.MaintenanceCron) { $parsedObj.MaintenanceCron } else { "0 1 * * *" }
                $verifyCron = if ($parsedObj.VerifyCron) { $parsedObj.VerifyCron } else { "0 2 * * *" }

                $configObj = @{
                    VerifyPercent   = [double]$verifyPercent
                    MaintenanceCron = $maintCron
                    VerifyCron      = $verifyCron
                }

                $configObj | ConvertTo-Json -Compress | Out-File -FilePath $ConfigFile -Encoding utf8
                Write-Host "File di configurazione aggiornato in: $ConfigFile" -ForegroundColor Cyan
            } catch {
                Write-Warning "Errore nel parsing del JSON: $_"
            }
        }
    }
}

# --- LOOP PRINCIPALE ---
while ($true) {
    $socket = $null
    $stream = $null
    try {
        $socket = [Net.Sockets.TcpClient]::new()
        $socket.Connect($MqttHost, $MqttPort)
        if (-not $socket.Connected) { throw "Connessione fallita" }

        $stream = $socket.GetStream()

        # 1. Pacchetto MQTT CONNECT
        $protocolName = [System.Text.Encoding]::UTF8.GetBytes("MQTT")
        $variableHeader = [byte[]]@(0x00, 0x04) + $protocolName + [byte[]]@(0x04, 0x02, 0x00, 0x3C, 0x00, 0x00)
        $connectPacket = [byte[]]@(0x10) + [byte[]]$variableHeader.Length + $variableHeader
        $stream.Write($connectPacket, 0, $connectPacket.Length)
        Start-Sleep -Milliseconds 200

        # Leggi CONNACK
        $ackBuffer = New-Object byte[] 2
        $stream.Read($ackBuffer, 0, 2) | Out-Null

        # 2. Pacchetto MQTT SUBSCRIBE
        $topicBytes = [System.Text.Encoding]::UTF8.GetBytes($Topic)
        $packetId = [byte[]]@(0x00, 0x01)
        $topicLen = [byte[]]@([BitConverter]::GetBytes([uint16]$topicBytes.Length)[1], [BitConverter]::GetBytes([uint16]$topicBytes.Length)[0])
        $requestedQoS = [byte[]]@(0x00)
        
        $subPayload = $packetId + $topicLen + $topicBytes + $requestedQoS
        $subscribePacket = [byte[]]@(0x82, $subPayload.Length) + $subPayload

        $stream.Write($subscribePacket, 0, $subscribePacket.Length)
        Write-Host "Connesso al broker MQTT e iscritto a: $Topic (In attesa di messaggi o configurazioni Retained...)" -ForegroundColor Green

        $buffer = New-Object byte[] 8192

        # Controlla subito se arriva il messaggio Retained dopo la Subscribe
        Start-Sleep -Milliseconds 300
        if ($stream.DataAvailable) {
            $bytesRead = $stream.Read($buffer, 0, $buffer.Length)
            if ($bytesRead -gt 0) {
                Process-MqttMessage -Buffer $buffer -BytesRead $bytesRead -ConfigFile $ConfigFile
            }
        }

        # Loop di ascolto continuo
        while ($socket.Connected) {
            if ($stream.DataAvailable) {
                $bytesRead = $stream.Read($buffer, 0, $buffer.Length)
                if ($bytesRead -eq 0) { break }
                Process-MqttMessage -Buffer $buffer -BytesRead $bytesRead -ConfigFile $ConfigFile
            }
            Start-Sleep -Milliseconds 100
        }
    } catch {
        Write-Warning "Connessione MQTT interrotta: $_. Riprovo tra 5 secondi..."
        Start-Sleep -Seconds 5
    } finally {
        if ($stream) { $stream.Close() }
        if ($socket) { $socket.Close() }
    }
}
$MqttHost = if ($env:MQTT_HOST) { $env:MQTT_HOST } else { "localhost" }
$MqttPort = if ($env:MQTT_PORT) { [int]$env:MQTT_PORT } else { 1883 }
$Topic = "kopia/config/advanced/#"
$ConfigFile = Join-Path ([System.IO.Path]::GetTempPath()) "kopia-agent-settings.json"

Write-Host "Avvio del listener MQTT nativo (TCP) avanzato su Windows..." -ForegroundColor Cyan

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

        # Leggi CONNACK (2 byte fissi)
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
        Write-Host "Connesso al broker MQTT e iscritto a: $Topic" -ForegroundColor Green

        $buffer = New-Object byte[] 8192

        # Loop di ascolto bloccante/sicuro sullo stream
        while ($socket.Connected) {
            if ($stream.DataAvailable) {
                $bytesRead = $stream.Read($buffer, 0, $buffer.Length)
                if ($bytesRead -eq 0) { break }

                # Controlliamo se è un pacchetto PUBLISH (Control packet type 3 -> 0x30)
                $packetType = ($buffer[0] -band 0xF0)
                if ($packetType -eq 0x30) {
                    $idx = 1
                    # Decodifica Remaining Length (algoritmo MQTT variable byte integer)
                    $multiplier = 1
                    $remainingLength = 0
                    do {
                        $encodedByte = $buffer[$idx++]
                        $remainingLength += ($encodedByte -band 127) * $multiplier
                        $multiplier *= 128
                    } while (($encodedByte -band 128) -ne 0)

                    # Leggi Topic Length
                    $topicLength = [BitConverter]::ToUInt16([byte[]]@($buffer[$idx+1], $buffer[$idx]), 0)
                    $idx += 2
                    
                    # Estrai il Topic (opzionale se vuoi stamparlo)
                    $receivedTopic = [System.Text.Encoding]::UTF8.GetString($buffer, $idx, $topicLength)
                    $idx += $topicLength

                    # Se QoS > 0 c'è il Packet ID (2 byte)
                    $qos = ($buffer[0] -band 0x06) -shr 1
                    if ($qos -gt 0) {
                        $idx += 2
                    }

                    # Il resto del pacchetto è il Payload JSON
                    $headerLength = $idx
                    $payloadLength = ($bytesRead - $headerLength)
                    
                    if ($payloadLength -gt 0) {
                        $jsonMessage = [System.Text.Encoding]::UTF8.GetString($buffer, $headerLength, $payloadLength).TrimEnd([char]0)
                        
                        Write-Host "[$receivedTopic] Ricevuta configurazione: $jsonMessage" -ForegroundColor Green
                        
                        try {
                            $parsedObj = $jsonMessage | ConvertFrom-Json
                            
                            $verifyPercent = if ($parsedObj.VerifyPercent -ne $null) { $parsedObj.VerifyPercent } elseif ($parsedObj.IntegrityVerifyPercentage -ne $null) { $parsedObj.IntegrityVerifyPercentage } else { 1 }
                            $normalHours = if ($parsedObj.NormalVerifyIntervalHours -ne $null) { $parsedObj.NormalVerifyIntervalHours } else { 24 }
                            $integrityHours = if ($parsedObj.IntegrityVerifyIntervalHours -ne $null) { $parsedObj.IntegrityVerifyIntervalHours } else { 168 }

                            $configObj = @{
                                IntegrityVerifyPercentage    = [double]$verifyPercent
                                NormalVerifyIntervalHours    = [int]$normalHours
                                IntegrityVerifyIntervalHours = [int]$integrityHours
                            }

                            $configObj | ConvertTo-Json -Compress | Out-File -FilePath $ConfigFile -Encoding utf8
                            Write-Host "File di configurazione aggiornato in: $ConfigFile" -ForegroundColor Cyan
                        } catch {
                            Write-Warning "Errore nel parsing del JSON ricevuto: $_"
                        }
                    }
                }
            }
            Start-Sleep -Milliseconds 50
        }
    } catch {
        Write-Warning "Connessione MQTT interrotta: $_. Riprovo tra 5 secondi..."
        Start-Sleep -Seconds 5
    } finally {
        if ($stream) { $stream.Close() }
        if ($socket) { $socket.Close() }
    }
}
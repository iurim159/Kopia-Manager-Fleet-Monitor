$MqttHost = if ($env:MQTT_HOST) { $env:MQTT_HOST } else { "localhost" }
$Topic = "kopia/config/advanced"

while ($true) {
    try {
        # Rimane in ascolto del topic MQTT
        mosquitto_sub -h $MqttHost -t $Topic | ForEach-Object {
            $jsonMessage = $_
            Write-Host "Ricevuta nuova configurazione MQTT: $jsonMessage"
            # Salva il payload nel file di configurazione letto dallo script di manutenzione
            $jsonMessage | Out-File -FilePath "/tmp/kopia-agent-settings.json" -Encoding utf8
        }
    } catch {
        Start-Sleep -Seconds 10
    }
}
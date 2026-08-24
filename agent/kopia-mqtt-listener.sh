#!/bin/bash

MQTT_HOST="${MQTT_HOST:-emqx}"
MQTT_PORT="${MQTT_PORT:-1883}"
TOPIC="kopia/config/advanced/#"
SETTINGS_FILE="/app/agent-settings.env"
CRON_JOB_FILE="/etc/cron.d/kopia-jobs"

echo "Avvio del listener MQTT per Kopia su EMQX ($MQTT_HOST:$MQTT_PORT) con mqttx..."

extract_json_value() {
    local json="$1"
    local key="$2"
    echo "$json" | grep -oE "\"$key\"\s*:\s*(\"[^\"]*\"|[0-9.]+)" | head -n1 | sed -E 's/.*:\s*[""]?([^""]*)[""]?/\1/'
}

while true; do
    # Usiamo esclusivamente mqttx sub per connetterci ad EMQX
    mqttx sub -h "$MQTT_HOST" -p "$MQTT_PORT" -t "$TOPIC" | while read -r msg; do
        [[ -z "$msg" ]] && continue
        echo "Ricevuta configurazione MQTT: $msg"
        
        VERIFY_CRON=$(extract_json_value "$msg" "VerifyCron")
        MAINT_CRON=$(extract_json_value "$msg" "MaintenanceCron")
        
        INTEGRITY_PERCENT=$(extract_json_value "$msg" "IntegrityVerifyPercentage")
        [[ -z "$INTEGRITY_PERCENT" ]] && INTEGRITY_PERCENT=$(extract_json_value "$msg" "VerifyPercent")
        
        NORMAL_HOURS=$(extract_json_value "$msg" "NormalVerifyIntervalHours")
        INTEGRITY_HOURS=$(extract_json_value "$msg" "IntegrityVerifyIntervalHours")
        
        if [[ -n "$INTEGRITY_PERCENT" ]] || [[ -n "$NORMAL_HOURS" ]] || [[ -n "$INTEGRITY_HOURS" ]]; then
            cat << EOF > "$SETTINGS_FILE"
INTEGRITY_VERIFY_PERCENT=${INTEGRITY_PERCENT:-1}
NORMAL_VERIFY_INTERVAL_HOURS=${NORMAL_HOURS:-24}
INTEGRITY_VERIFY_INTERVAL_HOURS=${INTEGRITY_HOURS:-168}
EOF
            echo "Aggiornato $SETTINGS_FILE con successo."
        fi
        
        if [[ -n "$MAINT_CRON" ]] && [[ -n "$VERIFY_CRON" ]]; then
            cat << EOF > "$CRON_JOB_FILE"
$MAINT_CRON root /app/kopia-weekly-maintenance.sh normal >> /var/log/cron.log 2>&1
$VERIFY_CRON root /app/kopia-weekly-maintenance.sh integrity >> /var/log/cron.log 2>&1
EOF
            chmod 0644 "$CRON_JOB_FILE"
            crontab "$CRON_JOB_FILE"
            echo "Crontab Linux aggiornato con successo!"
        fi
    done
    
    echo "Disconnesso da EMQX. Riprovo tra 10 secondi..."
    sleep 10
done
#!/bin/bash

MQTT_HOST="${MQTT_HOST:-emqx}"
MQTT_PORT="${MQTT_PORT:-1883}"
TOPIC="kopia/config/advanced/#"
SETTINGS_FILE="/app/agent-settings.env"
CRON_JOB_FILE="/etc/cron.d/kopia-jobs"

echo "Avvio del listener MQTT per le configurazioni avanzate su Linux..."

while true; do
    mosquitto_sub -h "$MQTT_HOST" -p "$MQTT_PORT" -t "$TOPIC" | while read -r msg; do
        echo "Ricevuta nuova configurazione MQTT: $msg"
        
        # Estrazione dei campi cron
        VERIFY_CRON=$(echo "$msg" | grep -o '"VerifyCron":"[^"]*' | cut -d'"' -f4)
        MAINT_CRON=$(echo "$msg" | grep -o '"MaintenanceCron":"[^"]*' | cut -d'"' -f4)
        
        # Estrazione migliorata della percentuale (cerca IntegrityVerifyPercentage o VerifyPercent e accetta decimali)
        INTEGRITY_PERCENT=$(echo "$msg" | grep -E -o '"(IntegrityVerifyPercentage|VerifyPercent)":[0-9.]+' | head -n1 | cut -d':' -f2 | tr -d ' ')
        
        # Intervalli in ore
        NORMAL_HOURS=$(echo "$msg" | grep -E -o '"NormalVerifyIntervalHours":[0-9]+' | head -n1 | cut -d':' -f2 | tr -d ' ')
        INTEGRITY_HOURS=$(echo "$msg" | grep -E -o '"IntegrityVerifyIntervalHours":[0-9]+' | head -n1 | cut -d':' -f2 | tr -d ' ')
        
        # Se viene trovato almeno un valore utile, aggiorna il file di configurazione
        if [ ! -z "$INTEGRITY_PERCENT" ] || [ ! -z "$INTEGRITY_HOURS" ] || [ ! -z "$NORMAL_HOURS" ]; then
            cat << EOF > "$SETTINGS_FILE"
INTEGRITY_VERIFY_PERCENT=${INTEGRITY_PERCENT:-1}
NORMAL_VERIFY_INTERVAL_HOURS=${NORMAL_HOURS:-24}
INTEGRITY_VERIFY_INTERVAL_HOURS=${INTEGRITY_HOURS:-168}
EOF
            echo "Aggiornato $SETTINGS_FILE con successo:"
            cat "$SETTINGS_FILE"
        fi
        
        # Aggiorna dinamicamente il crontab
        if [ ! -z "$MAINT_CRON" ] && [ ! -z "$VERIFY_CRON" ]; then
            echo "$MAINT_CRON root /app/kopia-weekly-maintenance.sh normal >> /var/log/cron.log 2>&1" > "$CRON_JOB_FILE"
            echo "$VERIFY_CRON root /app/kopia-weekly-maintenance.sh integrity >> /var/log/cron.log 2>&1" >> "$CRON_JOB_FILE"
            
            chmod 0644 "$CRON_JOB_FILE"
            crontab "$CRON_JOB_FILE"
            echo "Crontab Linux aggiornato con successo!"
        fi
    done
    
    echo "Connessione MQTT interrotta. Riprovo tra 10 secondi..."
    sleep 10
done
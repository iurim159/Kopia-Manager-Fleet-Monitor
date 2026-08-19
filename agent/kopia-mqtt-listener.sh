#!/bin/bash

MQTT_HOST="${MQTT_HOST:-emqx}"
MQTT_PORT="${MQTT_PORT:-1883}"
TOPIC="kopia/config/advanced/#"
SETTINGS_FILE="/app/agent-settings.env"
CRON_JOB_FILE="/etc/cron.d/kopia-jobs"

echo "Avvio del listener MQTT per le configurazioni avanzate su Linux..."

while true; do
    # Ascolta il topic MQTT in streaming continuo
    mosquitto_sub -h "$MQTT_HOST" -p "$MQTT_PORT" -t "$TOPIC" | while read -r msg; do
        echo "Ricevuta nuova configurazione MQTT: $msg"
        
        # Estrae i campi dal JSON ricevuto
        VERIFY_CRON=$(echo "$msg" | grep -o '"VerifyCron":"[^"]*' | cut -d'"' -f4)
        VERIFY_PERCENT=$(echo "$msg" | grep -o '"VerifyPercent":[^,}]*' | cut -d':' -f2)
        MAINT_CRON=$(echo "$msg" | grep -o '"MaintenanceCron":"[^"]*' | cut -d'"' -f4)
        
        # Salva la percentuale in un file di ambiente letto dallo script di manutenzione
        if [ ! -z "$VERIFY_PERCENT" ]; then
            echo "VERIFY_PERCENT=$VERIFY_PERCENT" > "$SETTINGS_FILE"
            echo "Aggiornata percentuale di verifica a: $VERIFY_PERCENT"
        fi
        
        # Aggiorna dinamicamente il crontab di sistema con le nuove schedulazioni
        if [ ! -z "$MAINT_CRON" ] && [ ! -z "$VERIFY_CRON" ]; then
            echo "$MAINT_CRON root /app/kopia-weekly-maintenance.sh >> /var/log/cron.log 2>&1" > "$CRON_JOB_FILE"
            echo "$VERIFY_CRON root /app/kopia-weekly-maintenance.sh --verify-only >> /var/log/cron.log 2>&1" >> "$CRON_JOB_FILE"
            
            chmod 0644 "$CRON_JOB_FILE"
            crontab "$CRON_JOB_FILE"
            echo "Crontab Linux aggiornato con successo!"
        fi
    done
    
    echo "Connessione MQTT interrotta. Riprovo tra 10 secondi..."
    sleep 10
done
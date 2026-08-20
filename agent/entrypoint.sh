#!/bin/bash

# 1. Assicurati che la cartella dei log esista
mkdir -p /var/log

# 2. Crea il file di log se non esiste
touch /var/log/cron.log

# 3. Avvia il servizio cron
if command -v service &> /dev/null; then
    service cron start
else
    /usr/sbin/cron
fi

# 4. AVVIA IL LISTENER MQTT IN BACKGROUND
# Assicurati che il percorso corrisponda a dove si trova dentro il container (es. /app/kopia-mqtt-listener.sh)
if [ -f /app/kopia-mqtt-listener.sh ]; then
    echo "Avvio del listener MQTT..."
    /app/kopia-mqtt-listener.sh &
else
    echo "Attenzione: /app/kopia-mqtt-listener.sh non trovato!"
fi

echo "Container avviato e operativo. Monitoraggio in corso..."

# 5. Esegue il tail sul log
tail -F /var/log/cron.log
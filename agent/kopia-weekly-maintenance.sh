#!/bin/bash

# --- CONFIGURAZIONE DINAMICA ---
AGENT_ID="${AGENT_ID:-UNKNOWN_AGENT}"
CONTAINER_NAME="${CONTAINER_NAME:-kopia-iso-tn-01-kopia}"
LOG_FILE="/tmp/kopia-maintenance.log"
MQTT_HOST="${MQTT_HOST:-emqx}"
MQTT_PORT="${MQTT_PORT:-1883}"

# Topic dinamico con AGENT_ID
MQTT_TOPIC="kopia/maintenance/${AGENT_ID}/${CONTAINER_NAME}"
KOPIA_PASS="${KOPIA_PASSWORD:-test-password}"

echo "=== Inizio Manutenzione Kopia (${CONTAINER_NAME}) da Agent [${AGENT_ID}]: $(date) ===" > "$LOG_FILE"

STATUS="OK"
DETAILS="Manutenzione e Garbage Collector completati senza errori."
GC_SUCCESS=true

run_kopia() {
    if command -v kopia &> /dev/null; then
        KOPIA_PASSWORD="$KOPIA_PASS" kopia "$@"
    else
        docker exec -e KOPIA_PASSWORD="$KOPIA_PASS" "$CONTAINER_NAME" kopia "$@"
    fi
}

# 1. Assegna l'owner della manutenzione all'utente corrente (evita il blocco 'auto')
run_kopia maintenance set --owner=me >> "$LOG_FILE" 2>&1

# 2. Esecuzione Manutenzione Full e Garbage Collector
run_kopia maintenance run --full --safety=full >> "$LOG_FILE" 2>&1
if [ $? -ne 0 ]; then
    STATUS="ERROR"
    GC_SUCCESS=false
    DETAILS="Errore durante l'esecuzione del Garbage Collector / Manutenzione Full."
fi

# 3. Status e Verification
run_kopia maintenance status >> "$LOG_FILE" 2>&1
run_kopia snapshot verify --verify-files-percent=0.00001 >> "$LOG_FILE" 2>&1

echo "=== Fine Manutenzione: $(date) ===" >> "$LOG_FILE"

LAST_MAINTENANCE=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

RAW_LOG_CONTENT=$(cat "$LOG_FILE" | tr -d '\r' | sed 's/\\/\\\\/g' | sed 's/"/\\"/g' | awk '{printf "%s\\n", $0}')

PAYLOAD=$(cat <<EOF
{
  "AgentId": "${AGENT_ID}",
  "ContainerName": "${CONTAINER_NAME}",
  "Status": "${STATUS}",
  "LastMaintenance": "${LAST_MAINTENANCE}",
  "GarbageCollectorSuccess": ${GC_SUCCESS},
  "Details": "${DETAILS}",
  "RawOutput": "${RAW_LOG_CONTENT}"
}
EOF
)

echo "Invio messaggio da '${AGENT_ID}' per '${CONTAINER_NAME}' a ${MQTT_HOST}:${MQTT_PORT} [${MQTT_TOPIC}]..."
mosquitto_pub -h "$MQTT_HOST" -p "$MQTT_PORT" -t "$MQTT_TOPIC" -m "$PAYLOAD"
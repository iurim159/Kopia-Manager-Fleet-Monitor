#!/bin/bash

# --- MODALITÀ DI ESECUZIONE (normal / integrity) ---
MODE="${1:-normal}"

# --- CONFIGURAZIONE DINAMICA ---
AGENT_ID="${AGENT_ID:-UNKNOWN_AGENT}"
CONTAINER_NAME="${CONTAINER_NAME:-kopia-iso-tn-01-kopia}"
LOG_FILE="/tmp/kopia-maintenance-${MODE}.log"
MQTT_HOST="${MQTT_HOST:-emqx}"
MQTT_PORT="${MQTT_PORT:-1883}"

# Topic dinamico con AGENT_ID e modalità
MQTT_TOPIC="kopia/maintenance/${AGENT_ID}/${CONTAINER_NAME}/${MODE}"
KOPIA_PASS="${KOPIA_PASSWORD:-test-password}"

# --- LETTURA CONFIGURAZIONE DINAMICA (MQTT) ---
SETTINGS_FILE="/app/agent-settings.env"
VERIFY_PERCENT=0.0001 # Valore di default

if [ -f "$SETTINGS_FILE" ]; then
    source "$SETTINGS_FILE"
fi

# Assegna la percentuale (solo per integrity) e l'intervallo in base alla modalità scelta
if [ "$MODE" == "integrity" ]; then
    VERIFY_PERCENT="${INTEGRITY_VERIFY_PERCENT:-1}"
    CURRENT_INTERVAL="${INTEGRITY_VERIFY_INTERVAL_HOURS:-168}"
else
    VERIFY_PERCENT=""
    CURRENT_INTERVAL="${NORMAL_VERIFY_INTERVAL_HOURS:-24}"
fi

# File persistente per tracciare l'ultimo successo specifico per modalità
LAST_SUCCESS_FILE="/tmp/kopia_last_success_${CONTAINER_NAME}_${MODE}"
MAX_DAYS_ALLOWED=14

echo "=== Inizio Manutenzione Kopia (${MODE}) [${CONTAINER_NAME}] da Agent [${AGENT_ID}]: $(date) ===" > "$LOG_FILE"

STATUS="OK"
DETAILS="Manutenzione e Verification (${MODE} al ${VERIFY_PERCENT}%) completati senza errori."
GC_SUCCESS=true
VERIFY_SUCCESS=true

run_kopia() {
    if command -v kopia &> /dev/null; then
        KOPIA_PASSWORD="$KOPIA_PASS" kopia "$@"
    else
        docker exec -e KOPIA_PASSWORD="$KOPIA_PASS" "$CONTAINER_NAME" kopia "$@"
    fi
}

# --- 0. CONTROLLO DEI 14 GIORNI (BASATO SUL PASSATO) ---
CURRENT_EPOCH=$(date +%s)
if [ -f "$LAST_SUCCESS_FILE" ]; then
    LAST_SUCCESS_EPOCH=$(cat "$LAST_SUCCESS_FILE" | tr -d '[:space:]')
    
    if [[ "$LAST_SUCCESS_EPOCH" =~ ^[0-9]+$ ]]; then
        DIFF_SECONDS=$(( CURRENT_EPOCH - LAST_SUCCESS_EPOCH ))
        MAX_SECONDS=$(( MAX_DAYS_ALLOWED * 86400 ))

        if [ $DIFF_SECONDS -gt $MAX_SECONDS ]; then
            STATUS="CRITICAL"
            DAYS_AGO=$(( DIFF_SECONDS / 86400 ))
            DETAILS="ATTENZIONE: Nessuna verifica ${MODE} riuscita negli ultimi ${DAYS_AGO} giorni (limite: ${MAX_DAYS_ALLOWED} giorni)."
            echo "CRITICAL: Nessuna verifica ${MODE} riuscita negli ultimi ${DAYS_AGO} giorni." >> "$LOG_FILE"
        fi
    else
        echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
    fi
else
    echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
fi

if [ "$MODE" == "normal" ]; then
    # 1. Assegna l'owner della manutenzione all'utente corrente
    run_kopia maintenance set --owner=me >> "$LOG_FILE" 2>&1

    # 2. Esecuzione Manutenzione Full e Garbage Collector (solo nella normale)
    run_kopia maintenance run --full --safety=full >> "$LOG_FILE" 2>&1
    if [ $? -ne 0 ]; then
        STATUS="ERROR"
        GC_SUCCESS=false
        DETAILS="Errore durante l'esecuzione del Garbage Collector / Manutenzione Full."
    fi

    # 3. Status della Manutenzione
    run_kopia maintenance status >> "$LOG_FILE" 2>&1
fi

# 4. Verification (rapida o profonda in base alla modalità)
echo "--- Inizio Verification (${MODE}) ---" >> "$LOG_FILE"
if [ "$MODE" == "integrity" ]; then
    echo "Esecuzione Verification profonda su ${VERIFY_PERCENT}% dei dati (integrity)..." >> "$LOG_FILE"
    run_kopia snapshot verify --verify-files-percent=${VERIFY_PERCENT} >> "$LOG_FILE" 2>&1
    VERIFY_EXIT_CODE=$?
fi


if [ $VERIFY_EXIT_CODE -ne 0 ]; then
    STATUS="ERROR"
    VERIFY_SUCCESS=false
    if [ "$GC_SUCCESS" = true ]; then
        DETAILS="Errore durante la verifica dei file (${MODE} Verify ${VERIFY_PERCENT}%)."
    else
        DETAILS="Errori riscontrati sia nel Garbage Collector che nella Verification."
    fi
else
    VERIFY_SUCCESS=true
fi

# --- AGGIORNAMENTO DEL TIMESTAMP ---
if [ "$GC_SUCCESS" = true ] && [ "$VERIFY_SUCCESS" = true ]; then
    echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
fi

echo "=== Fine Manutenzione (${MODE}): $(date) ===" >> "$LOG_FILE"

LAST_MAINTENANCE=$(date -u +"%Y-%m-%dT%H:%M:%SZ") 
if [ "$MODE" == "normal" ]; then
    # 1. Assegna l'owner della manutenzione all'utente corrente
    run_kopia maintenance set --owner=me >> "$LOG_FILE" 2>&1

    # 2. Esecuzione Manutenzione Full e Garbage Collector (solo nella normale)
    run_kopia maintenance run --full --safety=full >> "$LOG_FILE" 2>&1
    if [ $? -ne 0 ]; then
        STATUS="ERROR"
        GC_SUCCESS=false
        DETAILS="Errore durante l'esecuzione del Garbage Collector / Manutenzione Full."
    fi

    # 3. Status della Manutenzione
    run_kopia maintenance status >> "$LOG_FILE" 2>&1
    
    # In modalità normal non facciamo la verifica, quindi consideriamo il check superato d'ufficio
    VERIFY_SUCCESS=true
fi

# 4. Verification (Eseguita SOLO in modalità integrity)
if [ "$MODE" == "integrity" ]; then
    echo "--- Inizio Verification (integrity - ${VERIFY_PERCENT}%) ---" >> "$LOG_FILE"
    run_kopia snapshot verify --verify-files-percent=${VERIFY_PERCENT} >> "$LOG_FILE" 2>&1
    VERIFY_EXIT_CODE=$?

    if [ $VERIFY_EXIT_CODE -ne 0 ]; then
        STATUS="ERROR"
        VERIFY_SUCCESS=false
        DETAILS="Errore durante la verifica dei file (integrity Verify ${VERIFY_PERCENT}%)."
    else
        VERIFY_SUCCESS=true
    fi
fi

# --- AGGIORNAMENTO DEL TIMESTAMP ---
if [ "$GC_SUCCESS" = true ] && [ "$VERIFY_SUCCESS" = true ]; then
    echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
fi

echo "=== Fine Manutenzione (${MODE}): $(date) ===" >> "$LOG_FILE"

LAST_MAINTENANCE=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Escludiamo le righe piene di hash / ID dei blob
RAW_LOG_CONTENT=$(grep -vE "([a-f0-9]{32,}|blob|pack|index)" "$LOG_FILE" | tr -d '\r' | sed 's/\\/\\\\/g' | sed 's/"/\\"/g' | awk '{printf "%s\\n", $0}')

PAYLOAD=$(cat <<EOF
{
  "AgentId": "${AGENT_ID}",
  "ContainerName": "${CONTAINER_NAME}",
  "Mode": "${MODE}",
  "Status": "${STATUS}",
  "LastMaintenance": "${LAST_MAINTENANCE}",
  "GarbageCollectorSuccess": ${GC_SUCCESS},
  "VerifySuccess": ${VERIFY_SUCCESS},
  "VerifyPercent": ${VERIFY_PERCENT:-0},
  "TargetIntervalHours": ${CURRENT_INTERVAL},
  "Details": "${DETAILS}",
  "RawOutput": "${RAW_LOG_CONTENT}"
}
EOF
)

echo "Invio messaggio da '${AGENT_ID}' per '${CONTAINER_NAME}' (${MODE}) a ${MQTT_HOST}:${MQTT_PORT} [${MQTT_TOPIC}]..."
mosquitto_pub -h "$MQTT_HOST" -p "$MQTT_PORT" -t "$MQTT_TOPIC" -m "$PAYLOAD"

echo "modalità: ${AGENT_ID}, container: ${CONTAINER_NAME}, tipo: ${MODE}, stato: ${STATUS}, percentuale verifica: ${VERIFY_PERCENT}%, intervallo corrente: ${CURRENT_INTERVAL}, dettagli: ${DETAILS}"
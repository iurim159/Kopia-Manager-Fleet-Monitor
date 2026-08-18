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

# Percentuale di verifica file per grandi moli di dati
VERIFY_PERCENT=0.0001

# File persistente per tracciare l'ultimo successo (per il controllo dei 14 giorni)
LAST_SUCCESS_FILE="/tmp/kopia_last_success_${CONTAINER_NAME}"
MAX_DAYS_ALLOWED=14

echo "=== Inizio Manutenzione Kopia (${CONTAINER_NAME}) da Agent [${AGENT_ID}]: $(date) ===" > "$LOG_FILE"

STATUS="OK"
DETAILS="Manutenzione, Garbage Collector e Verification (${VERIFY_PERCENT}%) completati senza errori."
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
    # Leggiamo il file pulendo eventuali spazi o ritorni a capo
    LAST_SUCCESS_EPOCH=$(cat "$LAST_SUCCESS_FILE" | tr -d '[:space:]')
    
    # Verifichiamo che sia un numero valido
    if [[ "$LAST_SUCCESS_EPOCH" =~ ^[0-9]+$ ]]; then
        DIFF_SECONDS=$(( CURRENT_EPOCH - LAST_SUCCESS_EPOCH ))
        MAX_SECONDS=$(( MAX_DAYS_ALLOWED * 86400 ))

        if [ $DIFF_SECONDS -gt $MAX_SECONDS ]; then
            STATUS="CRITICAL"
            DAYS_AGO=$(( DIFF_SECONDS / 86400 ))
            DETAILS="ATTENZIONE: Nessuna manutenzione completa riuscita negli ultimi ${DAYS_AGO} giorni (limite: ${MAX_DAYS_ALLOWED} giorni)."
            echo "CRITICAL: Nessuna manutenzione completa riuscita negli ultimi ${DAYS_AGO} giorni (limite: ${MAX_DAYS_ALLOWED} giorni)." >> "$LOG_FILE"
        fi
    else
        # Se il file contiene per errore una stringa non numerica, lo resettiamo
        echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
    fi
else
    # Prima esecuzione in assoluto: creiamo il file con il timestamp attuale
    echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
fi

# 1. Assegna l'owner della manutenzione all'utente corrente (evita il blocco 'auto')
run_kopia maintenance set --owner=me >> "$LOG_FILE" 2>&1

# 2. Esecuzione Manutenzione Full e Garbage Collector
run_kopia maintenance run --full --safety=full >> "$LOG_FILE" 2>&1
if [ $? -ne 0 ]; then
    STATUS="ERROR"
    GC_SUCCESS=false
    DETAILS="Errore durante l'esecuzione del Garbage Collector / Manutenzione Full."
fi

# 3. Status della Manutenzione
run_kopia maintenance status >> "$LOG_FILE" 2>&1

# 4. Verification rapida sui file
echo "--- Inizio Verification (${VERIFY_PERCENT}%) ---" >> "$LOG_FILE"
run_kopia snapshot verify --verify-files-percent=${VERIFY_PERCENT} >> "$LOG_FILE" 2>&1
VERIFY_EXIT_CODE=$?

echo "Exit code verifica: $VERIFY_EXIT_CODE"

if [ $VERIFY_EXIT_CODE -ne 0 ]; then
    STATUS="ERROR"
    VERIFY_SUCCESS=false
    if [ "$GC_SUCCESS" = true ]; then
        DETAILS="Errore durante la verifica dei file (Snapshot Verify ${VERIFY_PERCENT}%)."
    else
        DETAILS="Errori riscontrati sia nel Garbage Collector che nella Verification."
    fi
else
    VERIFY_SUCCESS=true
fi

# --- AGGIORNAMENTO DEL TIMESTAMP ---
# Se la manutenzione e la verifica odierne sono andate bene (nessun errore nei comandi kopia),
# aggiorniamo il file con il timestamp attuale così sparisce lo stato CRITICAL.
if [ "$GC_SUCCESS" = true ] && [ "$VERIFY_SUCCESS" = true ]; then
    echo "$CURRENT_EPOCH" > "$LAST_SUCCESS_FILE"
fi

echo "=== Fine Manutenzione: $(date) ===" >> "$LOG_FILE"

LAST_MAINTENANCE=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Escludiamo le righe piene di hash / ID dei blob (es. stringhe esadecimali lunghe o linee di contenuto ripetitivo)
RAW_LOG_CONTENT=$(grep -vE "([a-f0-9]{32,}|blob|pack|index)" "$LOG_FILE" | tr -d '\r' | sed 's/\\/\\\\/g' | sed 's/"/\\"/g' | awk '{printf "%s\\n", $0}')
PAYLOAD=$(cat <<EOF
{
  "AgentId": "${AGENT_ID}",
  "ContainerName": "${CONTAINER_NAME}",
  "Status": "${STATUS}",
  "LastMaintenance": "${LAST_MAINTENANCE}",
  "GarbageCollectorSuccess": ${GC_SUCCESS},
  "VerifySuccess": ${VERIFY_SUCCESS},
  "VerifyPercent": ${VERIFY_PERCENT},
  "Details": "${DETAILS}",
  "RawOutput": "${RAW_LOG_CONTENT}"
}
EOF
)

echo "Invio messaggio da '${AGENT_ID}' per '${CONTAINER_NAME}' a ${MQTT_HOST}:${MQTT_PORT} [${MQTT_TOPIC}]..."
mosquitto_pub -h "$MQTT_HOST" -p "$MQTT_PORT" -t "$MQTT_TOPIC" -m "$PAYLOAD"
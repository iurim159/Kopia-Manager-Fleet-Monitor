#!/bin/bash

# 1. Avvia il listener MQTT in background
/app/kopia-mqtt-listener.sh &

# 2. Avvia Cron in foreground (mantiene in vita il container Docker)
exec cron -f
FROM ubuntu:22.04

# Evita interazioni da terminale durante l'installazione
ENV DEBIAN_FRONTEND=noninteractive

# Installazione dei pacchetti necessari: Docker CLI, Cron, Mosquitto Client e Curl
RUN apt-get update && apt-get install -y \
    curl \
    cron \
    mosquitto-clients \
    ca-certificates \
    gnupg \
    lsb-release && \
    mkdir -p /etc/apt/keyrings && \
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(lsb_release -cs) stable" > /etc/apt/sources.list.d/docker.list && \
    apt-get update && apt-get install -y docker-ce-cli && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copia e imposta i permessi dello script
COPY kopia-weekly-maintenance.sh /app/kopia-weekly-maintenance.sh
RUN chmod +x /app/kopia-weekly-maintenance.sh

# Configura Cron per eseguire lo script ogni giorno a 00:00
RUN echo "0 0 * * * root /app/kopia-weekly-maintenance.sh >> /var/log/cron.log 2>&1" > /etc/cron.d/kopia-cron && \
    chmod 0644 /etc/cron.d/kopia-cron && \
    crontab /etc/cron.d/kopia-cron

# Avvia cron in foreground
CMD ["cron", "-f"]
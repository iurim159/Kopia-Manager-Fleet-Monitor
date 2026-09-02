FROM ubuntu:20.04

RUN apt-get update -o Acquire::Check-Valid-Until=false -o Acquire::Allow-InsecureRepositories=true && apt-get install -y \
    curl \
    cron \
    ca-certificates \
    gnupg \
    docker.io \
    && curl -LO https://www.emqx.com/en/downloads/MQTTX/v1.9.5/mqttx-cli-linux-x64 \
    && install ./mqttx-cli-linux-x64 /usr/local/bin/mqttx \
    && rm mqttx-cli-linux-x64 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY kopia-weekly-maintenance.sh /app/kopia-weekly-maintenance.sh
COPY kopia-mqtt-listener.sh /app/kopia-mqtt-listener.sh
COPY entrypoint.sh /app/entrypoint.sh

RUN chmod +x /app/kopia-weekly-maintenance.sh /app/kopia-mqtt-listener.sh /app/entrypoint.sh

# Configurazione cron
RUN echo "0 0 * * * root /app/kopia-weekly-maintenance.sh >> /var/log/cron.log 2>&1" > /etc/cron.d/kopia-cron \
    && chmod 0644 /etc/cron.d/kopia-cron \
    && crontab /etc/cron.d/kopia-cron

CMD ["/app/entrypoint.sh"]
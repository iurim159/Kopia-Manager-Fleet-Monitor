# JDoctor - Kopia Manager & Fleet Monitor

Un'infrastruttura completa per il monitoraggio e la manutenzione automatizzata di istanze **Kopia Backup**. Il sistema raccoglie metriche, log e allarmi in tempo reale tramite **MQTT** da diversi agenti di manutenzione e li espone in una dashboard web moderna, reattiva e sempre aggiornata.

---

## 🏗 Architettura del Sistema

Il progetto è strutturato in una rete multi-container orchestrata da Docker Compose:

1. **Broker MQTT (EMQX)**: Gestisce il flusso di messaggi, allerte e log RAW tra gli agent e la dashboard.
2. **Kopia Instance (Kopia UI)**: Istanza server Kopia che gestisce i repository e le attività di backup/snapshot.
3. **Monitor App (.NET Web API & Frontend)**: Backend in .NET 8 che elabora le metriche MQTT e le distribuisce via REST API, servendo la dashboard web statica.
4. **Agents (Ubuntu + Cron + Mosquitto + Docker CLI)**: Agenti distribuiti che eseguono script periodici di manutenzione (es. Garbage Collector, `full require-contents`) inviando log ed esiti al broker.

---

## 📁 Struttura del Progetto

```text
.
├── agent/                         # Container dell'agente di manutenzione
│   ├── Dockerfile                 # Ambiente Ubuntu con Cron, Mosquitto & Docker CLI
│   └── kopia-weekly-maintenance.sh# Script di manutenzione eseguito dall'agente
├── app/                           # Backend .NET 8 (KopiaMonitorApp)
│   ├── Models/                    # Modelli dati per metriche e dispositivi
│   ├── Properties/
│   ├── Services/                  # Servizio di ascolto MQTT e gestione stato
│   ├── wwwroot/                   # Frontend Web (HTML, CSS, JS)
│   │   ├── index.html             # Layout principale dashboard
│   │   ├── style.css              # Stili CSS responsive & temi dark
│   │   └── app.js                 # Logica dinamica e aggiornamento realtime
│   ├── appsettings.json           # Configurazione dell'applicazione .NET
│   └── Program.cs                 # Entrypoint .NET API
├── cache/                         # Repository cache di Kopia
├── config/                        # Configurazione del repository Kopia
├── logs/                          # Log condivisi di manutenzione ed esecuzione
├── docker-compose.yml             # Orchestrazione dell'intera flotta
├── Dockerfile                     # Dockerfile per il backend .NET (KopiaMonitorApp)
├── kopia-maintenance-crontab      # Pianificazione Cron
├── kopia-weekly-maintenance.sh    # Script di utilità manutenzione host
└── run-maintenance.sh             # Entrypoint per l'avvio rapido manuale

```

---

## 🚀 Requisiti di Sistema

* **Docker** >= 20.10
* **Docker Compose** (plugin `docker compose` o binario `docker-compose`)
* Porte libere sull'host:
* `1883`: Broker MQTT
* `18083`: Dashboard di amministrazione EMQX
* `5000`: Dashboard Web JDoctor
* `5115`: Kopia Web UI



---

## 🛠 Guida all'Installazione e Avvio

### 1. Clonare il Repository

```bash
git clone <URL_DEL_TUO_REPOSITORY>
cd jdoctor-kopia-manager

```

### 2. Struttura dei file Web

Assicurati che i 3 file dell'interfaccia grafica aggiornati siano posizionati all'interno della cartella `app/wwwroot/`:

* `app/wwwroot/index.html`
* `app/wwwroot/style.css`
* `app/wwwroot/app.js`

### 3. Ricostruire e Avviare i Container

Per applicare le modifiche ed evitare problemi di cache Docker, avvia la build da zero:

```bash
docker compose down
docker compose build --no-cache
docker compose up -d

```

### 4. Verificare lo stato dei Servizi

```bash
docker compose ps

```

---

## 🌐 Porte e Interfacce Web

| Servizio | URL / Porta | Descrizione |
| --- | --- | --- |
| **JDoctor Dashboard** | `http://localhost:5000` | Dashboard di monitoraggio con log RAW fissi |
| **Kopia UI** | `http://localhost:5115` | Interfaccia nativa di gestione Kopia |
| **EMQX Admin** | `http://localhost:18083` | Dashboard di gestione del Broker MQTT (User: `admin` / Pass: `public`) |
| **MQTT Broker** | `localhost:1883` | Endpoint per la trasmissione dei dati degli agenti |

---

## 💻 Funzionalità Dashboard Frontend

* **Istanze Fisse e Log RAW Permanenti**: Nessun pannello a comparsa. I log delle istanze Kopia sono sempre aperti ed estesi per una rapida consultazione.
* **Auto-refresh in Background**: Stato dei nodi, allarmi e log si aggiornano automaticamente ogni 15 secondi tramite chiamate background REST (`/api/status`).
* **Pulsante "Esegui Controlli Ora"**: Invoca la rotta `/api/run-now` per forzare la manutenzione immediata con feedback visivo sullo stato dell'operazione.
* **Layout Responsive (Mobile & Desktop)**: Interfaccia ottimizzata sia per schermi desktop ad alta risoluzione (card ampie a partire da `420px`), sia per consultazione via smartphone/tablet.

---

## 🔧 Manutenzione e Log

* Per visualizzare i log del container di monitoraggio .NET:
```bash
docker logs -f jdoctor-kopia-monitor

```


* Per visualizzare i log di un agente specifico:
```bash
docker logs -f jdoctor-kopia-agent

```



```

function sanitizeId(raw) {
    return String(raw || '').replace(/[^a-zA-Z0-9_-]/g, '_');
}

async function fetchStatus() {
    try {
        const response = await fetch('/api/status');
        const data = await response.json();
        renderCards(data);
    } catch (error) {
        console.error("Errore recupero stato:", error);
    }
}

function renderCards(devices) {
    const container = document.getElementById('cardsContainer');
    const totalDevicesEl = document.getElementById('total-devices');
    const systemStatusEl = document.getElementById('system-status');
    const lastSyncEl = document.getElementById('last-sync-time');

    const now = new Date();
    lastSyncEl.textContent = `Sync: ${now.toLocaleTimeString('it-IT')}`;

    if (!devices || devices.length === 0) {
        container.innerHTML = `<div class="empty-state">Nessun dispositivo rilevato o in attesa di dati MQTT.</div>`;
        totalDevicesEl.textContent = '0';
        return;
    }

    if (container.querySelector('.empty-state')) {
        container.innerHTML = '';
    }

    totalDevicesEl.textContent = devices.length;
    const hasError = devices.some(dev => dev.status !== 'OK');
    
    if (hasError) {
        systemStatusEl.textContent = 'ATTENZIONE';
        systemStatusEl.style.color = 'var(--accent-red)';
    } else {
        systemStatusEl.textContent = 'OPERATIVO';
        systemStatusEl.style.color = 'var(--accent-green)';
    }

    devices.forEach(dev => {
        const rawKey = dev.deviceId || dev.containerName || dev.deviceName || String(Math.random());
        const devId = sanitizeId(rawKey);
        
        let cardEl = document.getElementById(`card-${devId}`);

        let badgeClass = 'ok';
        if (dev.status === 'CRITICAL') badgeClass = 'critical';
        else if (dev.status === 'ERROR') badgeClass = 'error';

        const lastFullReq = dev.lastFullRequireContentsDate 
            ? new Date(dev.lastFullRequireContentsDate).toLocaleString('it-IT') 
            : 'N/A';
        
        const lastCheck = new Date(dev.lastCheckedAt).toLocaleTimeString('it-IT');

        const alertsList = dev.alerts && dev.alerts.length > 0 
            ? dev.alerts.map(a => `<li class="alert-item-error">⚠ ${a}</li>`).join('')
            : '<li class="alert-item-ok">✓ Parametri regolari</li>';

        const rawLogText = dev.rawOutput || 'Nessun log salvato.';

        if (!cardEl) {
            cardEl = document.createElement('div');
            cardEl.id = `card-${devId}`;
            cardEl.className = 'agent-card';

            cardEl.innerHTML = `
                <div class="card-header">
                    <div>
                        <div class="agent-title">${dev.deviceName || ''}</div>
                        <div class="agent-subtitle">${dev.containerName || ''}</div>
                    </div>
                    <span class="status-badge ${badgeClass}" id="badge-${devId}">${dev.status || ''}</span>
                </div>

                <div class="card-body">
                    <div class="info-row">
                        <span class="info-label">Full Require-Contents</span>
                        <span class="info-value" id="fullreq-${devId}">${lastFullReq}</span>
                    </div>
                    <div class="info-row">
                        <span class="info-label">Garbage Collector</span>
                        <span class="info-value" id="gc-${devId}" style="color: ${dev.garbageCollectorSuccess ? 'var(--accent-green)' : 'var(--accent-red)'}">
                            ${dev.garbageCollectorSuccess ? 'OK' : 'FAIL'}
                        </span>
                    </div>

                    <div class="alerts-box">
                        <ul class="alerts-list" id="alerts-${devId}">
                            ${alertsList}
                        </ul>
                    </div>

                    <div class="log-section">
                        <div class="log-section-header">Log Kopia RAW</div>
                        <div class="log-box" id="log-${devId}">${rawLogText}</div>
                    </div>
                </div>

                <div class="card-footer-info" id="footer-${devId}">
                    Update: ${lastCheck}
                </div>
            `;

            container.appendChild(cardEl);

        } else {
            // Aggiornamento dinamico senza ricostruire l'HTML per evitare flickering
            const badgeEl = document.getElementById(`badge-${devId}`);
            if (badgeEl) {
                badgeEl.className = `status-badge ${badgeClass}`;
                badgeEl.textContent = dev.status || '';
            }

            const fullReqEl = document.getElementById(`fullreq-${devId}`);
            if (fullReqEl) fullReqEl.textContent = lastFullReq;

            const gcEl = document.getElementById(`gc-${devId}`);
            if (gcEl) {
                gcEl.textContent = dev.garbageCollectorSuccess ? 'OK' : 'FAIL';
                gcEl.style.color = dev.garbageCollectorSuccess ? 'var(--accent-green)' : 'var(--accent-red)';
            }

            const alertsEl = document.getElementById(`alerts-${devId}`);
            if (alertsEl) alertsEl.innerHTML = alertsList;

            const logEl = document.getElementById(`log-${devId}`);
            if (logEl && logEl.textContent !== rawLogText) {
                logEl.textContent = rawLogText;
            }

            const footerEl = document.getElementById(`footer-${devId}`);
            if (footerEl) footerEl.textContent = `Update: ${lastCheck}`;
        }
    });
}

async function runChecksNow() {
    const btn = document.getElementById('btnRunNow');
    const icon = document.getElementById('btnIcon');
    const text = document.getElementById('btnText');

    btn.disabled = true;
    icon.className = 'spinner';
    icon.textContent = '';
    text.textContent = 'Esecuzione...';
    
    try {
        const response = await fetch('/api/run-now', { method: 'POST' });
        const result = await response.json();

        if (!response.ok || result.success === false) {
            alert("Errore esecuzione: " + (result.message || result.detail || "Impossibile completare la manutenzione."));
        }
        
        await fetchStatus();
    } catch (err) {
        console.error("Errore esecuzione controlli:", err);
        alert("Errore di rete durante la richiesta.");
    } finally {
        btn.disabled = false;
        icon.className = '';
        icon.textContent = '↻';
        text.textContent = 'Esegui Controlli Ora';
    }
}

// Avvio immediato + polling ogni 15 secondi
fetchStatus();
setInterval(fetchStatus, 15000);
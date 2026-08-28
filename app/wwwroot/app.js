let currentDeviceId = null;

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

        const gcSuccess = dev.garbageCollectorSuccess;
        const verifySuccess = dev.verifySuccess !== undefined ? dev.verifySuccess : dev.VerifySuccess;
        
        const alertsList = dev.alerts && dev.alerts.length > 0 
            ? dev.alerts.map(a => `<li class="alert-item-error">⚠ ${escapeHtml(a)}</li>`).join('')
            : '<li class="alert-item-ok">✓ Parametri regolari</li>';

        const detailsText = dev.details ? escapeHtml(dev.details) : '';
        const rawLogText = dev.rawOutput || 'Nessun log salvato.';

        if (!cardEl) {
            cardEl = document.createElement('div');
            cardEl.id = `card-${devId}`;
            cardEl.className = 'agent-card';

            cardEl.innerHTML = `
                <div class="card-header">
                    <div>
                        <div class="agent-title">${escapeHtml(dev.deviceName || dev.deviceId || '')}</div>
                        <div class="agent-subtitle">${escapeHtml(dev.containerName || '')}</div>
                    </div>
                    
                    <button class="btn-config-card" onclick="openSettingsModal('${devId}')" title="Configura Istanza">
                        ⚙️ Configura
                    </button>

                    <span class="status-badge ${badgeClass}" id="badge-${devId}">${escapeHtml(dev.status || '')}</span>
                </div>

                <div class="card-body">
                    <div class="info-row">
                        <span class="info-label">Full Require-Contents</span>
                        <span class="info-value" id="fullreq-${devId}">${lastFullReq}</span>
                    </div>

                    <div class="info-row">
                        <span class="info-label">Garbage Collector</span>
                        <span class="info-value" id="gc-${devId}" style="color: ${gcSuccess ? 'var(--accent-green)' : 'var(--accent-red)'}">
                            ${gcSuccess ? 'OK' : 'FAIL'}
                        </span>
                    </div>

                    <div class="info-row">
                        <span class="info-label">Verifica Integrità</span>
                        <span class="info-value" id="verify-${devId}" style="color: ${verifySuccess ? 'var(--accent-green)' : 'var(--accent-red)'}">
                            ${verifySuccess ? 'Integrità dei dati garantita' : 'VERIFICA FALLITA'}
                        </span>
                    </div>

                    ${detailsText ? `<div class="details-box" id="details-${devId}">${detailsText}</div>` : `<div class="details-box" id="details-${devId}" style="display:none;"></div>`}

                    <div class="alerts-box">
                        <ul class="alerts-list" id="alerts-${devId}">
                            ${alertsList}
                        </ul>
                    </div>

                    <div class="log-section">
                        <div class="log-section-header">Log Kopia RAW</div>
                        <div class="log-box" id="log-${devId}">${escapeHtml(rawLogText)}</div>
                    </div>
                </div>

                <div class="card-footer-info" id="footer-${devId}">
                    Update: ${lastCheck}
                </div>
            `;

            container.appendChild(cardEl);
        } else {
            const badgeEl = document.getElementById(`badge-${devId}`);
            if (badgeEl) {
                badgeEl.className = `status-badge ${badgeClass}`;
                badgeEl.textContent = dev.status || '';
            }

            const fullReqEl = document.getElementById(`fullreq-${devId}`);
            if (fullReqEl) fullReqEl.textContent = lastFullReq;

            const gcEl = document.getElementById(`gc-${devId}`);
            if (gcEl) {
                gcEl.textContent = gcSuccess ? 'OK' : 'FAIL';
                gcEl.style.color = gcSuccess ? 'var(--accent-green)' : 'var(--accent-red)';
            }

            const verifyEl = document.getElementById(`verify-${devId}`);
            if (verifyEl) {
                verifyEl.textContent = verifySuccess 
                    ? 'Integrità dei file garantita' 
                    : 'VERIFICA FALLITA';
                verifyEl.style.color = verifySuccess ? 'var(--accent-green)' : 'var(--accent-red)';
            }

            const detailsEl = document.getElementById(`details-${devId}`);
            if (detailsEl) {
                if (detailsText) {
                    detailsEl.textContent = detailsText;
                    detailsEl.style.display = 'block';
                } else {
                    detailsEl.style.display = 'none';
                }
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

// --- GESTIONE MODALE SSH (ESECUZIONE CONTROLLI ORA) ---

function runChecksNow() {
    // Apre il modale per la lista IP e credenziali SSH invece di eseguire subito la chiamata
    document.getElementById('runNowModal').style.display = 'flex';
}

function closeRunNowModal() {
    document.getElementById('runNowModal').style.display = 'none';
}

async function submitRunNowSsh() {
    const btn = document.querySelector('#runNowModal .btn-save');
    const nodeListText = document.getElementById('sshNodeList').value.trim();

    if (!nodeListText) {
        showToast("Inserisci almeno un nodo nel formato corretto.", "error");
        return;
    }

    // Analizza riga per riga la textarea
    const lines = nodeListText.split(/[\r\n]+/);
    const targets = [];

    for (let line of lines) {
        if (!line.trim()) continue;

        // Supporta separatore pipe "|" o virgola ","
        const parts = line.split(/[|,]/).map(p => p.trim());
        
        if (parts.length >= 3) {
            targets.push({
                ip: parts[0],
                username: parts[1],
                credential: parts.slice(2).join(':') // Riunisce eventuali caratteri speciali nella password
            });
        }
    }

    if (targets.length === 0) {
        showToast("Nessun target valido trovato. Usa il formato: IP | username | password", "error");
        return;
    }

    const payload = { targets: targets };

    btn.disabled = true;
    btn.textContent = 'Connessione sequenziale in corso...';

    try {
        const response = await fetch('/api/run-now-ssh', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        const result = await response.json();

        if (!response.ok || result.success === false) {
            showToast("Errore esecuzione remota: " + (result.message || "Impossibile completare."), "error");
        } else {
            showToast("Comandi SSH avviati sequenzialmente sui nodi!", "success");
            closeRunNowModal();
        }
        
        await fetchStatus();
    } catch (err) {
        console.error("Errore di rete SSH:", err);
        showToast("Errore di rete durante la richiesta SSH.", "error");
    } finally {
        btn.disabled = false;
        btn.textContent = 'Connetti ed Esegui';
    }
}

// --- UTILITIES DI NOTIFICA E ESCAPING ---

function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}

function showToast(message, type = 'success') {
    const existingToast = document.getElementById('customToast');
    if (existingToast) existingToast.remove();

    const toast = document.createElement('div');
    toast.id = 'customToast';
    toast.className = `custom-toast ${type}`;
    toast.textContent = message;

    document.body.appendChild(toast);

    setTimeout(() => toast.classList.add('show'), 10);

    setTimeout(() => {
        toast.classList.remove('show');
        setTimeout(() => toast.remove(), 300);
    }, 4000);
}

// --- LOGICA DEL MODALE CONFIGURAZIONE AVANZATA ---

function openSettingsModal(deviceId) {
    currentDeviceId = deviceId; 
    document.getElementById('settingsModal').style.display = 'flex';
}

function closeSettingsModal() {
    currentDeviceId = null;
    document.getElementById('settingsModal').style.display = 'none';
}

async function saveAdvancedConfig() {
    const btn = document.querySelector('#settingsModal .btn-save');
    
    const normalEl = document.getElementById('normalInterval');
    const integrityEl = document.getElementById('integrityInterval');
    const verifyEl = document.getElementById('verifyPercent');

    const normalInterval = normalEl ? (parseInt(normalEl.value, 10) || 24) : 24;
    const integrityInterval = integrityEl ? (parseInt(integrityEl.value, 10) || 168) : 168;
    const verifyPercent = verifyEl ? (parseFloat(verifyEl.value) || 1) : 1;
    
    const payload = {
        DeviceId: currentDeviceId,
        NormalVerifyIntervalHours: normalInterval,
        IntegrityVerifyIntervalHours: integrityInterval,
        IntegrityVerifyPercentage: verifyPercent
    };

    btn.disabled = true;
    btn.textContent = 'Salvataggio...';

    try {
        const response = await fetch('/api/config/advanced', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        if (response.ok) {
            showToast('Configurazione salvata con successo!', 'success');
            closeSettingsModal();
        } else {
            showToast('Errore durante il salvataggio della configurazione.', 'error');
        }
    } catch (err) {
        console.error('Errore di rete:', err);
        showToast('Errore di rete durante la comunicazione.', 'error');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Salva e Invia';
    }
}

// Avvio immediato e polling ogni 15 secondi
fetchStatus();
setInterval(fetchStatus, 15000);
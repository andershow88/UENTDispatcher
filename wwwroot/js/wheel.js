// ════════════════════════════════════════════════════════════════════════════
// UENT Dispatcher — Wheel-Engine
//   - Zeichnet Glücksrad mit beliebig vielen Slices.
//   - Spin: Server liefert Gewinner, Animation landet visuell auf der Slice.
//   - Bestätigung getrennt vom Spin: nur bestätigte Auswahl wird persistiert.
// ════════════════════════════════════════════════════════════════════════════

(function () {
    'use strict';

    var SLICE_COLORS = [
        '#00515A', '#C8A96E', '#39747A', '#B5945E',
        '#003D44', '#D4BB8A', '#1f6168', '#a37f4a'
    ];

    var state = {
        candidates: [],         // Alle aktiven Mitarbeiter mit Status
        eligible: [],           // Davon der aktuelle Pool (abhängig von override)
        currentRotation: 0,     // Aktueller Winkel der Canvas-Rotation
        spinning: false,
        winner: null,
        blacklistIgnoriert: false,
        antiforgeryToken: null
    };

    function init(initialStatus, antiforgeryToken) {
        state.candidates = (initialStatus || []).map(s => ({
            id: s.Id || s.id,
            anzeigename: (s.Vorname || s.vorname || '') + ' ' + (s.Nachname || s.nachname || ''),
            gesperrt: s.Gesperrt || s.gesperrt || false,
            restTage: s.RestTage || s.restTage || 0
        }));
        state.antiforgeryToken = antiforgeryToken;
        recomputeEligible();
        drawWheel();
        updateStatusPanel();
        bindUI();
        // Bei Theme-Wechsel: Canvas neu zeichnen (Empty-State-Farben)
        window.addEventListener('uent:theme-changed', function () {
            if (!state.spinning) drawWheel();
        });
        // Echte Browser-Fullscreen-Aenderungen (z. B. Esc) mit unserem
        // body.wheel-fs Status synchronisieren — Browser-Einstellungen
        // sollen in beide Richtungen sauber zurueckgesetzt werden.
        document.addEventListener('fullscreenchange', function () {
            if (!document.fullscreenElement && document.body.classList.contains('wheel-fs')) {
                document.body.classList.remove('wheel-fs');
                if (!state.spinning) drawWheel();
            }
        });
    }

    // ── Fullscreen-Helfer ───────────────────────────────────────────────────
    // animate=true → Wachstums-Animation des Rades starten (nur fuer den
    // ersten Spin aus dem Normal-View; Re-Spins im Vollbild lassen das Rad
    // einfach in voller Groesse drehen).
    function enterFullscreen(animate) {
        var wasFs = document.body.classList.contains('wheel-fs');
        document.body.classList.add('wheel-fs');
        drawWheel();
        var el = document.documentElement;
        if (el.requestFullscreen && !document.fullscreenElement) {
            el.requestFullscreen().catch(function () { /* User abgelehnt o.ae. — visueller FS reicht */ });
        }
        if (animate && !wasFs) {
            startGrowingAnimation();
        }
    }
    function exitFullscreen() {
        document.body.classList.remove('wheel-fs');
        var wrap = document.querySelector('.wheel-canvas-wrap');
        if (wrap) wrap.classList.remove('growing');
        drawWheel();
        if (document.fullscreenElement && document.exitFullscreen) {
            document.exitFullscreen().catch(function () {});
        }
    }
    function startGrowingAnimation() {
        var wrap = document.querySelector('.wheel-canvas-wrap');
        if (!wrap) return;
        wrap.classList.remove('growing');
        // Force reflow, damit die CSS-Animation neu startet
        void wrap.offsetWidth;
        wrap.classList.add('growing');
    }

    function recomputeEligible() {
        state.eligible = state.blacklistIgnoriert
            ? state.candidates.slice()
            : state.candidates.filter(c => !c.gesperrt);
    }

    // ── UI Bindings ─────────────────────────────────────────────────────────
    function bindUI() {
        document.getElementById('btnSpin').addEventListener('click', onSpin);
        document.getElementById('btnConfirm').addEventListener('click', onConfirm);
        document.getElementById('btnRespin').addEventListener('click', onRespin);
        document.getElementById('toggleBlacklist').addEventListener('click', toggleBlacklist);
    }

    function toggleBlacklist() {
        if (state.spinning) return;
        state.blacklistIgnoriert = !state.blacklistIgnoriert;
        var btn = document.getElementById('toggleBlacklist');
        var row = document.getElementById('toggleRow');
        btn.classList.toggle('on', state.blacklistIgnoriert);
        row.classList.toggle('active', state.blacklistIgnoriert);
        recomputeEligible();
        drawWheel();
        updateStatusPanel();
        // Wenn jemand schon "gewählt" wurde aber die Auswahl nicht mehr passt:
        // Result einfach verstecken.
        hideResult();
    }

    // ── Canvas-Drawing ──────────────────────────────────────────────────────
    function drawWheel() {
        var canvas = document.getElementById('wheelCanvas');
        if (!canvas) return;
        var ctx = canvas.getContext('2d');
        var dpr = window.devicePixelRatio || 1;
        var size = canvas.clientWidth;
        if (canvas.width !== size * dpr) {
            canvas.width = size * dpr;
            canvas.height = size * dpr;
        }
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, size, size);

        var cx = size / 2, cy = size / 2;
        var radius = size / 2 - 6;

        var slices = state.eligible;
        if (slices.length === 0) {
            // Leerer Zustand — Farben aus CSS-Variablen, damit Theme-aware
            var rs = getComputedStyle(document.documentElement);
            var emptyBg = (rs.getPropertyValue('--mc-canvas-empty-bg') || '#f3f5f8').trim();
            var emptyBorder = (rs.getPropertyValue('--mc-border') || '#e4e7eb').trim();
            var emptyText = (rs.getPropertyValue('--mc-text-muted') || '#8895a7').trim();

            ctx.beginPath();
            ctx.arc(cx, cy, radius, 0, Math.PI * 2);
            ctx.fillStyle = emptyBg;
            ctx.fill();
            ctx.strokeStyle = emptyBorder; ctx.lineWidth = 2; ctx.stroke();
            ctx.fillStyle = emptyText;
            ctx.font = '600 14px Inter, system-ui, sans-serif';
            ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
            ctx.fillText('Keine teilnehmenden Personen', cx, cy - 8);
            ctx.font = '12px Inter, system-ui, sans-serif';
            ctx.fillText('Sperrliste ignorieren oder Mitarbeitende anlegen.', cx, cy + 14);
            return;
        }

        var sliceAngle = (Math.PI * 2) / slices.length;

        for (var i = 0; i < slices.length; i++) {
            var startAngle = -Math.PI / 2 + i * sliceAngle;
            var endAngle = startAngle + sliceAngle;
            var color = SLICE_COLORS[i % SLICE_COLORS.length];

            ctx.beginPath();
            ctx.moveTo(cx, cy);
            ctx.arc(cx, cy, radius, startAngle, endAngle);
            ctx.closePath();

            // Subtle radial gradient für jede Slice
            var grad = ctx.createRadialGradient(cx, cy, radius * 0.15, cx, cy, radius);
            grad.addColorStop(0, lighten(color, 0.18));
            grad.addColorStop(1, color);
            ctx.fillStyle = grad;
            ctx.fill();

            ctx.strokeStyle = 'rgba(255,255,255,.45)';
            ctx.lineWidth = 1.5;
            ctx.stroke();

            // Text (radial vom Zentrum nach außen)
            ctx.save();
            ctx.translate(cx, cy);
            ctx.rotate(startAngle + sliceAngle / 2);
            ctx.textAlign = 'right';
            ctx.textBaseline = 'middle';
            ctx.fillStyle = isLight(color) ? '#1a202c' : '#ffffff';
            var name = slices[i].anzeigename;
            // Schriftgroesse skaliert mit Canvas-Groesse (Faktor relativ zu 560px Basis),
            // damit Text auch auf dem 1100px-Vollbild-Rad gut lesbar ist.
            var sizeFactor = Math.max(1, size / 560);
            var baseFontSize = slices.length > 18 ? 10 : (slices.length > 14 ? 11.5 : (slices.length > 10 ? 13 : 15));
            var fontSize = Math.round(baseFontSize * sizeFactor);
            ctx.font = '600 ' + fontSize + 'px Inter, system-ui, sans-serif';
            // Locked-Marker
            var prefix = slices[i].gesperrt ? '🔒 ' : '';
            ctx.fillText(prefix + truncate(name, 22), radius - Math.round(14 * sizeFactor), 4);
            ctx.restore();
        }

        // Goldener Außenring
        ctx.beginPath();
        ctx.arc(cx, cy, radius + 2, 0, Math.PI * 2);
        ctx.strokeStyle = 'rgba(200,169,110,.7)';
        ctx.lineWidth = 4;
        ctx.stroke();
    }

    function lighten(hex, amt) {
        var c = hex.replace('#', '');
        var r = parseInt(c.substr(0, 2), 16);
        var g = parseInt(c.substr(2, 2), 16);
        var b = parseInt(c.substr(4, 2), 16);
        r = Math.min(255, Math.round(r + (255 - r) * amt));
        g = Math.min(255, Math.round(g + (255 - g) * amt));
        b = Math.min(255, Math.round(b + (255 - b) * amt));
        return 'rgb(' + r + ',' + g + ',' + b + ')';
    }

    function isLight(hex) {
        var c = hex.replace('#', '');
        var r = parseInt(c.substr(0, 2), 16);
        var g = parseInt(c.substr(2, 2), 16);
        var b = parseInt(c.substr(4, 2), 16);
        return (r * 0.299 + g * 0.587 + b * 0.114) > 165;
    }

    function truncate(s, max) {
        if (s.length <= max) return s;
        return s.slice(0, max - 1) + '…';
    }

    // ── Status-Panel ────────────────────────────────────────────────────────
    function updateStatusPanel() {
        var availableList = document.getElementById('availableList');
        var lockedList = document.getElementById('lockedList');
        var availCount = document.getElementById('availableCount');
        var lockedCount = document.getElementById('lockedCount');
        var totalActive = document.getElementById('totalActive');

        var available = state.candidates.filter(c => !c.gesperrt);
        var locked = state.candidates.filter(c => c.gesperrt);

        availCount.textContent = available.length;
        lockedCount.textContent = locked.length;
        if (totalActive) totalActive.textContent = state.candidates.length;

        availableList.innerHTML = available.length
            ? available.map(personRow).join('')
            : '<div class="empty-state"><i class="bi bi-people"></i><div>Niemand verfügbar.</div></div>';
        lockedList.innerHTML = locked.length
            ? locked.map(personRow).join('')
            : '<div class="muted" style="font-size:.82rem; padding:.4rem 0;">Aktuell ist niemand gesperrt.</div>';
    }

    function personRow(p) {
        var initials = p.anzeigename.split(' ').filter(Boolean)
            .map(s => s.charAt(0).toUpperCase()).slice(0, 2).join('');
        if (p.gesperrt) {
            return '<div class="person-row">' +
                '<div class="person-avatar locked">' + escapeHtml(initials) + '</div>' +
                '<div>' +
                '<div class="person-name">' + escapeHtml(p.anzeigename) + '</div>' +
                '<div class="person-meta">noch ' + p.restTage + ' Tag(e) gesperrt</div>' +
                '</div>' +
                '<div class="meta-right"><span class="badge-locked">Gesperrt</span></div>' +
                '</div>';
        }
        return '<div class="person-row">' +
            '<div class="person-avatar">' + escapeHtml(initials) + '</div>' +
            '<div>' +
            '<div class="person-name">' + escapeHtml(p.anzeigename) + '</div>' +
            '</div>' +
            '<div class="meta-right"><span class="badge-free">Verfügbar</span></div>' +
            '</div>';
    }

    function escapeHtml(s) {
        return (s || '').replace(/[&<>"']/g, m =>
            ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[m]);
    }

    // ── Spin ────────────────────────────────────────────────────────────────
    async function onSpin() {
        if (state.spinning) return;
        hideError();
        hideResult();

        if (state.eligible.length === 0) {
            showError(state.blacklistIgnoriert
                ? 'Es sind keine aktiven Mitarbeitenden gepflegt.'
                : 'Alle Mitarbeitenden sind aktuell gesperrt. Aktiviere "Sperrliste ignorieren", um trotzdem zu drehen.');
            return;
        }

        // Vollbild-Modus aktivieren — Sidebar, Status-Spalte und Toggle-Row
        // werden ausgeblendet, Wheel fuellt das gesamte Viewport. Wachstums-
        // Animation laeuft nur beim ersten Drehen aus dem Normal-View; bei
        // Re-Spins im bereits aktiven Vollbild wird sie uebersprungen.
        enterFullscreen(true);

        state.spinning = true;
        document.getElementById('btnSpin').disabled = true;

        var resp;
        try {
            resp = await postJson('/Dispatcher/Spin', { blacklistIgnoriert: state.blacklistIgnoriert });
        } catch (e) {
            state.spinning = false;
            document.getElementById('btnSpin').disabled = false;
            showError('Verbindungsfehler: ' + e.message);
            return;
        }

        if (!resp.ok) {
            state.spinning = false;
            document.getElementById('btnSpin').disabled = false;
            // Statuses synchronisieren — falls inzwischen jemand weggefallen ist
            if (resp.kandidaten) {
                state.candidates = resp.kandidaten.map(c => ({
                    id: c.id, anzeigename: c.anzeigename,
                    gesperrt: c.gesperrt, restTage: c.restTage
                }));
                recomputeEligible();
                drawWheel(); updateStatusPanel();
            }
            showError(resp.error || 'Auswahl fehlgeschlagen.');
            return;
        }

        // Server hat Statuses ggf. verändert — synchronisieren
        if (resp.kandidaten) {
            state.candidates = resp.kandidaten.map(c => ({
                id: c.id, anzeigename: c.anzeigename,
                gesperrt: c.gesperrt, restTage: c.restTage
            }));
            recomputeEligible();
            drawWheel();
            updateStatusPanel();
        }

        var winner = resp.winner;
        var idx = state.eligible.findIndex(c => c.id === winner.id);
        if (idx < 0) idx = 0;

        animateSpinTo(idx, function () {
            state.spinning = false;
            document.getElementById('btnSpin').disabled = false;
            // Defensive: Confirm/Respin explizit aktiv setzen — nichts darf
            // aus einem vorherigen Spin-Zyklus haengen bleiben.
            var btnConfirm = document.getElementById('btnConfirm');
            var btnRespin = document.getElementById('btnRespin');
            if (btnConfirm) { btnConfirm.disabled = false; btnConfirm.innerHTML = '<i class="bi bi-check2-circle"></i> Bestätigen'; }
            if (btnRespin) { btnRespin.disabled = false; }
            state.winner = winner;
            showResult(winner);
        });
    }

    function animateSpinTo(sliceIdx, onDone) {
        var canvas = document.getElementById('wheelCanvas');
        var slices = state.eligible.length;
        var sliceAngleDeg = 360 / slices;
        // Pointer ist oben (12 Uhr). Slice 0 startet bei -90° (oben mittig).
        // Für Slice idx: Mittelpunkt ist bei (-90° + idx*sliceAngle + sliceAngle/2).
        // Wir wollen, dass dieser Punkt unter den Pointer (bei -90°) rutscht
        // — also rotation = -(idx*sliceAngle + sliceAngle/2).
        var targetAngleDeg = -(sliceIdx * sliceAngleDeg + sliceAngleDeg / 2);
        // 18 s Spin → 14..18 volle Drehungen. Mit dem starken Ease-out
        // passieren die meisten Drehungen frueh, danach laeuft das Rad
        // sichtbar aus.
        var spins = 14 + Math.floor(Math.random() * 5); // 14..18
        var finalRot = state.currentRotation;
        var base = Math.ceil((finalRot + 360) / 360) * 360;
        var newRot = base + spins * 360 + targetAngleDeg;
        if (newRot <= state.currentRotation) newRot += 360;
        state.currentRotation = newRot;
        canvas.style.transform = 'rotate(' + newRot + 'deg)';

        // CSS-Transition steht in dispatcher.css (18s cubic-bezier).
        setTimeout(onDone, 18100);
    }

    function showResult(winner) {
        var box = document.getElementById('wheelResult');
        var nameEl = document.getElementById('resultName');
        var hintEl = document.getElementById('resultHint');
        nameEl.textContent = winner.anzeigename;

        if (winner.gesperrt) {
            hintEl.innerHTML = '<i class="bi bi-shield-exclamation"></i> Eigentlich noch <strong>' +
                winner.restTage + ' Tag(e)</strong> gesperrt — Override aktiv. ' +
                'Bei Bestätigung wird ein <strong>neuer Eintrag</strong> im Verlauf erstellt und die Sperre auf frische 21 Tage gesetzt.';
        } else {
            hintEl.textContent = 'Bitte bestätigen, um die Auswahl als Dispatcher der Woche festzuhalten.';
        }

        box.style.display = 'flex';
        document.getElementById('actionsAfterSpin').style.display = 'flex';
        // Drehen-Button waehrend der Result-Phase verstecken — die Aktionen
        // "Bestaetigen" / "Erneut drehen" stehen separat zur Verfuegung,
        // damit nicht zwei "Erneut drehen"-Buttons nebeneinander erscheinen.
        document.getElementById('btnSpin').style.display = 'none';
    }
    function hideResult() {
        document.getElementById('wheelResult').style.display = 'none';
        document.getElementById('actionsAfterSpin').style.display = 'none';
        document.getElementById('btnSpin').style.display = '';
        document.getElementById('btnSpin').innerHTML = '<i class="bi bi-arrow-repeat"></i> Drehen';
        state.winner = null;
    }
    function showError(msg) {
        var el = document.getElementById('wheelError');
        el.textContent = msg;
        el.style.display = 'block';
    }
    function hideError() {
        document.getElementById('wheelError').style.display = 'none';
    }

    // ── Confirm / Respin ────────────────────────────────────────────────────
    function onRespin() {
        if (state.spinning) return;
        hideResult();
        // Kleiner setTimeout schiebt den Spin-Start auf den naechsten Tick —
        // das gibt dem DOM Zeit, hideResult-Aenderungen anzuwenden, bevor
        // onSpin den Zustand kippt. In der Praxis verhindert das eine
        // seltene Race-Condition, bei der actionsAfterSpin scheinbar
        // sichtbar bleibt obwohl der Spin laeuft.
        setTimeout(onSpin, 30);
    }

    async function onConfirm() {
        if (!state.winner || state.spinning) return;
        var btn = document.getElementById('btnConfirm');
        btn.disabled = true;
        btn.innerHTML = '<i class="bi bi-hourglass-split"></i> Speichere…';

        try {
            var resp = await postJson('/Dispatcher/Confirm', {
                employeeId: state.winner.id,
                blacklistIgnoriert: state.blacklistIgnoriert
            });
            if (!resp.ok) {
                showError(resp.error || 'Bestätigung fehlgeschlagen.');
                btn.disabled = false;
                btn.innerHTML = '<i class="bi bi-check2-circle"></i> Bestätigen';
                return;
            }
            // Erfolgs-Modal bleibt IM Vollbildmodus zentriert ueber dem Rad —
            // erst beim Klick auf "Schliessen" verlassen wir den Vollbild.
            confetti();
            showSuccessModal(resp.info);

            // Status im Hintergrund aktualisieren, damit nach dem Schliessen
            // alles frisch ist (Sperrlisten, Restdauer-Counter etc.).
            try {
                var statusResp = await fetch('/Dispatcher/Status');
                if (statusResp.ok) {
                    var list = await statusResp.json();
                    state.candidates = list.map(c => ({
                        id: c.id, anzeigename: c.anzeigename,
                        gesperrt: c.gesperrt, restTage: c.restTage
                    }));
                    recomputeEligible();
                }
            } catch (e) { /* Modal trotzdem sauber schliessbar */ }

            btn.disabled = false;
            btn.innerHTML = '<i class="bi bi-check2-circle"></i> Bestätigen';
        } catch (e) {
            showError('Verbindungsfehler: ' + e.message);
            btn.disabled = false;
            btn.innerHTML = '<i class="bi bi-check2-circle"></i> Bestätigen';
        }
    }

    function showSuccessModal(info) {
        var modal = document.getElementById('successModal');
        document.getElementById('successName').textContent = info.anzeigename;
        var until = new Date(info.sperrBisUtc);
        var fmt = until.toLocaleDateString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' });
        document.getElementById('successMeta').textContent = 'Gesperrt bis ' + fmt + ' (21 Tage)';
        modal.classList.add('open');
    }

    window.closeSuccessModal = function () {
        document.getElementById('successModal').classList.remove('open');
        // Erst jetzt zurueck in die Normalansicht — Vollbild verlassen,
        // Result/Spin-Buttons resetten, Status-Panel mit den frischen
        // Daten neu rendern. Defensive: alle Spin-bezogenen States hart
        // zuruecksetzen, damit der Drehen-Button beim naechsten Mal
        // garantiert wieder klickbar ist.
        exitFullscreen();
        hideResult();
        state.spinning = false;
        state.winner = null;
        var btnSpin = document.getElementById('btnSpin');
        var btnConfirm = document.getElementById('btnConfirm');
        var btnRespin = document.getElementById('btnRespin');
        if (btnSpin) { btnSpin.disabled = false; btnSpin.style.display = ''; }
        if (btnConfirm) { btnConfirm.disabled = false; btnConfirm.innerHTML = '<i class="bi bi-check2-circle"></i> Bestätigen'; }
        if (btnRespin) { btnRespin.disabled = false; }
        drawWheel();
        updateStatusPanel();
    };

    function confetti() {
        var colors = ['#C8A96E', '#00515A', '#39747A', '#D4BB8A', '#10b981'];
        for (var i = 0; i < 60; i++) {
            var p = document.createElement('div');
            p.className = 'confetti-piece';
            p.style.left = Math.random() * 100 + 'vw';
            p.style.background = colors[Math.floor(Math.random() * colors.length)];
            p.style.animationDelay = (Math.random() * 0.4) + 's';
            p.style.animationDuration = (1.6 + Math.random() * 0.8) + 's';
            p.style.transform = 'rotate(' + Math.random() * 360 + 'deg)';
            document.body.appendChild(p);
            setTimeout(() => p.remove(), 2600);
        }
    }

    // ── HTTP-Helper ─────────────────────────────────────────────────────────
    async function postJson(url, body) {
        var headers = { 'Content-Type': 'application/json' };
        if (state.antiforgeryToken) headers['RequestVerificationToken'] = state.antiforgeryToken;
        var res = await fetch(url, {
            method: 'POST',
            headers: headers,
            body: JSON.stringify(body || {})
        });
        if (!res.ok) throw new Error('HTTP ' + res.status);
        return await res.json();
    }

    // Resize-Handler — Canvas neu zeichnen bei Layoutänderung
    window.addEventListener('resize', function () {
        if (!state.spinning) drawWheel();
    });

    window.UENTWheel = { init: init };
})();

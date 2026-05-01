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

    // Foto-Cache: id → HTMLImageElement | null. null = Ladeversuch lief, Foto
    // existiert nicht. undefined = noch nicht versucht. So vermeiden wir
    // doppelte Requests und 404-Loops.
    var photoCache = {};

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
        document.getElementById('toggleBlacklist').addEventListener('click', toggleBlacklist);
        // Winner-Modal-Buttons sind via inline onclick gebunden (window.onWinnerConfirm/onWinnerRespin)
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
    }

    // ── Canvas-Drawing ──────────────────────────────────────────────────────
    // drawWheel = synchroner Render PLUS asynchrones Nachladen fehlender Fotos.
    // Sobald Fotos eintreffen, wird automatisch erneut gezeichnet.
    function drawWheel() {
        drawWheelNow();
        triggerPhotoLoads();
    }

    // Stoesst im Hintergrund Photo-Loads fuer alle bekannten Kandidaten an,
    // die noch keinen Cache-Eintrag haben. Idempotent — laeuft pro ID nur
    // einmal pro Session (Erfolg ODER 404 wird gemerkt).
    function triggerPhotoLoads() {
        var ids = state.candidates.map(function (s) { return s.id; });
        var pending = 0;
        var maybeRedraw = function () { if (pending === 0) drawWheelNow(); };
        ids.forEach(function (id) {
            if (photoCache[id] !== undefined) return;
            photoCache[id] = null; // Slot reservieren
            pending++;
            var img = new Image();
            img.onload = function () {
                photoCache[id] = img;
                pending--;
                maybeRedraw();
            };
            img.onerror = function () {
                // photoCache[id] bleibt null — kein Foto fuer diese Person
                pending--;
                maybeRedraw();
            };
            img.src = '/Employees/Photo/' + id;
        });
    }

    function drawWheelNow() {
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
        var sizeFactor = Math.max(1, size / 560);

        // Foto-/Initialen-Disc-Position und -Groesse
        var photoR = radius * 0.62;
        var chordHalf = photoR * Math.sin(sliceAngle / 2);
        // Disc fuellt 80% der Slice-Breite an dieser Position, plus Cap auf 30% des Radius
        var photoSize = Math.max(24, Math.min(radius * 0.32, chordHalf * 1.8));

        for (var i = 0; i < slices.length; i++) {
            var startAngle = -Math.PI / 2 + i * sliceAngle;
            var endAngle = startAngle + sliceAngle;
            var color = SLICE_COLORS[i % SLICE_COLORS.length];

            // Slice-Fuellung mit radialem Gradient
            ctx.beginPath();
            ctx.moveTo(cx, cy);
            ctx.arc(cx, cy, radius, startAngle, endAngle);
            ctx.closePath();
            var grad = ctx.createRadialGradient(cx, cy, radius * 0.15, cx, cy, radius);
            grad.addColorStop(0, lighten(color, 0.18));
            grad.addColorStop(1, color);
            ctx.fillStyle = grad;
            ctx.fill();
            ctx.strokeStyle = 'rgba(255,255,255,.45)';
            ctx.lineWidth = 1.5;
            ctx.stroke();

            // Foto bzw. Initialen-Kreis im Slice
            var midAngle = startAngle + sliceAngle / 2;
            var px = cx + Math.cos(midAngle) * photoR;
            var py = cy + Math.sin(midAngle) * photoR;
            var photo = photoCache[slices[i].id];
            var radius2 = photoSize / 2;

            // Schatten unter der Disc, fuer leichten 3D-Effekt
            ctx.save();
            ctx.shadowColor = 'rgba(0,0,0,.25)';
            ctx.shadowBlur = Math.max(4, photoSize * 0.08);
            ctx.shadowOffsetY = Math.max(1, photoSize * 0.04);
            ctx.beginPath();
            ctx.arc(px, py, radius2, 0, Math.PI * 2);
            ctx.fillStyle = '#ffffff';
            ctx.fill();
            ctx.restore();

            ctx.save();
            // In den Kreis clippen, dann Foto/Hintergrund zeichnen
            ctx.beginPath();
            ctx.arc(px, py, radius2, 0, Math.PI * 2);
            ctx.closePath();
            ctx.clip();

            if (photo && photo.naturalWidth > 0) {
                // object-fit: cover — Source-Crop quadratisch
                var iw = photo.naturalWidth;
                var ih = photo.naturalHeight;
                var sx, sy, sSize;
                if (iw >= ih) {
                    sSize = ih;
                    sx = (iw - ih) / 2;
                    sy = 0;
                } else {
                    sSize = iw;
                    sx = 0;
                    sy = (ih - iw) / 2;
                }
                ctx.drawImage(photo, sx, sy, sSize, sSize,
                              px - radius2, py - radius2, photoSize, photoSize);
            } else {
                // Fallback: Initialen-Disc mit Slice-Color
                var initGrad = ctx.createLinearGradient(px - radius2, py - radius2, px + radius2, py + radius2);
                initGrad.addColorStop(0, lighten(color, 0.25));
                initGrad.addColorStop(1, color);
                ctx.fillStyle = initGrad;
                ctx.fillRect(px - radius2, py - radius2, photoSize, photoSize);

                ctx.fillStyle = isLight(color) ? '#1a202c' : '#ffffff';
                var name = slices[i].anzeigename || '';
                var initials = name.split(' ').filter(Boolean)
                    .map(function (s) { return s.charAt(0).toUpperCase(); })
                    .slice(0, 2).join('');
                ctx.font = 'bold ' + Math.round(photoSize * 0.36) + 'px Inter, system-ui, sans-serif';
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText(initials || '?', px, py);
            }
            ctx.restore();

            // Weisser Ring um die Disc (Polaroid-Look)
            ctx.beginPath();
            ctx.arc(px, py, radius2, 0, Math.PI * 2);
            ctx.strokeStyle = '#ffffff';
            ctx.lineWidth = Math.max(2, photoSize * 0.06);
            ctx.stroke();

            // Locked-Indikator: kleines Schloss oben rechts auf der Disc
            if (slices[i].gesperrt) {
                var badgeR = photoSize * 0.20;
                var bx = px + Math.cos(-Math.PI / 4) * radius2;
                var by = py + Math.sin(-Math.PI / 4) * radius2;
                ctx.beginPath();
                ctx.arc(bx, by, badgeR, 0, Math.PI * 2);
                ctx.fillStyle = '#f59e0b';
                ctx.fill();
                ctx.strokeStyle = '#ffffff';
                ctx.lineWidth = Math.max(1.5, photoSize * 0.04);
                ctx.stroke();
                ctx.fillStyle = '#ffffff';
                ctx.font = 'bold ' + Math.round(badgeR * 1.3) + 'px sans-serif';
                ctx.textAlign = 'center';
                ctx.textBaseline = 'middle';
                ctx.fillText('🔒', bx, by + badgeR * 0.05);
            }

            // Vorname als kleines Label am Aussenrand der Slice
            ctx.save();
            ctx.translate(cx, cy);
            ctx.rotate(midAngle);
            ctx.textAlign = 'right';
            ctx.textBaseline = 'middle';
            ctx.fillStyle = isLight(color) ? '#1a202c' : '#ffffff';
            var firstName = (slices[i].anzeigename || '').split(' ')[0] || '';
            var baseLabelSize = slices.length > 18 ? 10 : (slices.length > 14 ? 11 : (slices.length > 10 ? 12 : 13));
            var labelSize = Math.round(baseLabelSize * sizeFactor);
            ctx.font = '600 ' + labelSize + 'px Inter, system-ui, sans-serif';
            ctx.fillText(truncate(firstName, 14), radius - Math.round(10 * sizeFactor), 0);
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
        var btnSpin = document.getElementById('btnSpin');
        btnSpin.disabled = true;
        // Drehen-Button waehrend des Spins komplett verstecken — die Buehne
        // gehoert dem Rad. Erscheint erst wieder im Normal-View nach Bestaetigung.
        btnSpin.style.display = 'none';

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
            var btnSpinErr = document.getElementById('btnSpin');
            btnSpinErr.disabled = false;
            btnSpinErr.style.display = '';
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
            state.winner = winner;
            // Direkt das Winner-Modal zeigen — kein Zwischen-Result-Block,
            // keine separaten Bottom-Buttons.
            showWinnerModal(winner);
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

    function showError(msg) {
        var el = document.getElementById('wheelError');
        el.textContent = msg;
        el.style.display = 'block';
    }
    function hideError() {
        document.getElementById('wheelError').style.display = 'none';
    }

    // ── Winner-Modal ────────────────────────────────────────────────────────
    // Nach jedem Spin wird genau EIN grosses Modal zentriert ueber dem
    // Vollbild-Rad gezeigt: "Glueckwunsch, diese Woche bist du dran, <Name>"
    // mit zufaelligem witzigen Text. Aktionen: Bestaetigen oder Erneut drehen.
    // Kein zweites Modal mehr nach Bestaetigen — direkt zurueck zur Hauptansicht.

    var WITTY_LINES = [
        "Tickets warten nicht, {vorname}. Auf gehts! 📨",
        "Diese Woche bist du das Schicksal des Service Desks. ⚡",
        "Glückwunsch zum heißen Stuhl. Kaffee bereit? ☕",
        "Der Algorithmus hat gesprochen — und {vorname} hat gewonnen.",
        "Halte das Team am Laufen, {vorname}. Du schaffst das!",
        "Service-Desk-Adel diese Woche. Trag die Krone mit Stolz. 👑",
        "Dispatcher-Mode: aktiviert. Möge die Macht mit dir sein.",
        "Diese Woche dreht sich alles um dich. (Wortwörtlich.) 🎯",
        "Auserwählt vom Glücksrad — und vom Team.",
        "Frische Dispatcher-Energie. Es kann losgehen!",
        "Tag eins von sieben — du hast das im Griff!",
        "Heute du, nächste Woche jemand anders. Aber jetzt: du!",
        "Der Goldzeiger zeigt auf {vorname}. Glückwunsch!",
        "{vorname}, mach das Service Desk stolz!",
        "Es ist {vorname}-Zeit. Das Team verlässt sich auf dich.",
        "Glückwunsch — du bist heute der/die heißeste Dispatcher:in der Stadt."
    ];

    function pickWittyLine(winner) {
        var line = WITTY_LINES[Math.floor(Math.random() * WITTY_LINES.length)];
        var firstName = (winner.anzeigename || '').split(' ')[0];
        return line.replace(/\{vorname\}/g, firstName);
    }

    function showWinnerModal(winner) {
        document.getElementById('winnerName').textContent = winner.anzeigename;
        document.getElementById('winnerWitty').textContent = pickWittyLine(winner);

        var lockHint = document.getElementById('winnerLockHint');
        if (winner.gesperrt) {
            lockHint.innerHTML = '<i class="bi bi-shield-exclamation"></i> Eigentlich noch <strong>' +
                winner.restTage + ' Tag(e)</strong> gesperrt — Override aktiv. ' +
                'Bei Bestätigung wird ein <strong>neuer Eintrag</strong> im Verlauf erstellt und die Sperre auf frische 21 Tage gesetzt.';
            lockHint.style.display = 'block';
        } else {
            lockHint.style.display = 'none';
        }

        var err = document.getElementById('winnerError');
        if (err) err.style.display = 'none';

        var confirmBtn = document.getElementById('winnerConfirmBtn');
        var respinBtn = document.getElementById('winnerRespinBtn');
        confirmBtn.disabled = false;
        confirmBtn.innerHTML = '<i class="bi bi-check2-circle"></i> Bestätigen';
        respinBtn.disabled = false;

        document.getElementById('winnerModal').classList.add('open');
        // Konfetti zur Feier des ausgewaehlten — dezente Celebration, kein
        // separates Modal danach mehr.
        confetti();
    }

    window.onWinnerConfirm = async function () {
        if (!state.winner) return;
        var confirmBtn = document.getElementById('winnerConfirmBtn');
        var respinBtn = document.getElementById('winnerRespinBtn');
        var err = document.getElementById('winnerError');
        err.style.display = 'none';
        confirmBtn.disabled = true; respinBtn.disabled = true;
        confirmBtn.innerHTML = '<i class="bi bi-hourglass-split"></i> Speichere…';

        try {
            var resp = await postJson('/Dispatcher/Confirm', {
                employeeId: state.winner.id,
                blacklistIgnoriert: state.blacklistIgnoriert
            });
            if (!resp.ok) {
                err.textContent = resp.error || 'Bestätigung fehlgeschlagen.';
                err.style.display = 'block';
                confirmBtn.disabled = false; respinBtn.disabled = false;
                confirmBtn.innerHTML = '<i class="bi bi-check2-circle"></i> Bestätigen';
                return;
            }
            // Status im Hintergrund frisch ziehen, damit Sperrlisten passen
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
            } catch (e) { /* trotzdem sauber schliessen */ }

            // Direkt zurueck in die Hauptansicht — KEIN Erfolgs-Modal mehr.
            document.getElementById('winnerModal').classList.remove('open');
            exitFullscreen();
            state.spinning = false;
            state.winner = null;
            var btnSpin = document.getElementById('btnSpin');
            if (btnSpin) { btnSpin.disabled = false; btnSpin.style.display = ''; }
            drawWheel();
            updateStatusPanel();
        } catch (e) {
            err.textContent = 'Verbindungsfehler: ' + e.message;
            err.style.display = 'block';
            confirmBtn.disabled = false; respinBtn.disabled = false;
            confirmBtn.innerHTML = '<i class="bi bi-check2-circle"></i> Bestätigen';
        }
    };

    window.onWinnerRespin = function () {
        if (state.spinning) return;
        document.getElementById('winnerModal').classList.remove('open');
        state.winner = null;
        // Im naechsten Tick wieder drehen (Vollbild bleibt aktiv → kein Wachstum)
        setTimeout(onSpin, 50);
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

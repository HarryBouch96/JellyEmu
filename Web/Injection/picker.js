// EXPERIMENT (game streaming): "Play on" picker. Pressing Play asks where to run the game:
// "This device" (the in-browser emulator) or a gaming PC that streams it. Options that can't be
// used right now (system not supported here, PC offline or in use) are shown greyed out with the
// reason. Works with a controller (incl. the Xbox app's key codes), keyboard or mouse.
(function () {
    const JE = window.JellyEmu = window.JellyEmu || {};

    const STYLE = `
        .jellyemu-picker { position: fixed; inset: 0; z-index: 100000; display: flex; align-items: center;
            justify-content: center; background: rgba(0,0,0,.72); font-family: inherit; }
        .jellyemu-picker-panel { min-width: 340px; max-width: 90vw; padding: 24px; border-radius: 12px;
            background: #202020; color: #fff; box-shadow: 0 10px 40px rgba(0,0,0,.6); }
        .jellyemu-picker-title { margin: 0 0 16px; font-size: 1.4em; font-weight: 600; }
        .jellyemu-picker-option { display: block; width: 100%; margin: 8px 0; padding: 14px 18px; border: 2px solid transparent;
            border-radius: 8px; background: #2e2e2e; color: #fff; font: inherit; font-size: 1.1em; text-align: left; cursor: pointer; }
        .jellyemu-picker-option.je-focus { border-color: #00a4dc; background: #34404a; }
        .jellyemu-picker-option[aria-disabled="true"] { opacity: .45; cursor: default; }
        .jellyemu-picker-reason { display: block; margin-top: 4px; font-size: .8em; color: rgba(255,255,255,.7); }
        .jellyemu-picker-hint { margin-top: 14px; font-size: .85em; color: rgba(255,255,255,.55); }
    `;

    // Key codes: arrows, Enter, Escape/Backspace, plus Xbox/Edge gamepad codes
    // (195 A, 196 B, 203-206 D-pad, 211-214 left stick). On the Xbox app, B arrives as Escape.
    const ACTIONS = {
        38: 'up', 203: 'up', 211: 'up',
        40: 'down', 204: 'down', 212: 'down',
        37: 'up', 205: 'up', 214: 'up',
        39: 'down', 206: 'down', 213: 'down',
        13: 'select', 32: 'select', 195: 'select',
        27: 'cancel', 8: 'cancel', 196: 'cancel'
    };

    // Browser emulators exist for these but are too slow to be playable, so only gaming PCs are offered.
    const TOO_SLOW_IN_BROWSER = new Set(['PlayStation 2']);

    function rememberKey(platform) { return 'jellyemu-play-on:' + platform; }

    /**
     * Asks where to play. Resolves to "local", a gaming PC id, or null if cancelled.
     * Resolves to "local" straight away when no gaming PC is set up.
     */
    JE.choosePlayDevice = function (itemId) {
        return JE.fetch('/jellyemu/stream/devices/' + itemId)
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; })
            .then(function (info) {
                if (!info || !info.devices || !info.devices.length) return 'local';
                const unsupported = JE.ejsUnsupportedPlatforms.has(info.platform);
                const tooSlow = TOO_SLOW_IN_BROWSER.has(info.platform);
                const localOk = !unsupported && !tooSlow;
                const localReason = unsupported ? "Can't play this system here" : tooSlow ? 'Too demanding to play here' : '';
                const options = [{ id: 'local', name: 'This device', available: localOk, reason: localReason }]
                    .concat(info.devices);
                return showPicker(info.platform, options);
            });
    };

    function showPicker(platform, options) {
        return new Promise(function (resolve) {
            if (!document.getElementById('jellyemu-picker-style')) {
                const style = document.createElement('style');
                style.id = 'jellyemu-picker-style';
                style.textContent = STYLE;
                document.head.appendChild(style);
            }

            const overlay = document.createElement('div');
            overlay.className = 'jellyemu-picker';
            overlay.innerHTML = '<div class="jellyemu-picker-panel" role="dialog" aria-label="Play on">' +
                '<h2 class="jellyemu-picker-title">Play on</h2><div class="jellyemu-picker-list"></div>' +
                '<div class="jellyemu-picker-hint">A: choose · B: cancel</div></div>';
            const list = overlay.querySelector('.jellyemu-picker-list');

            const buttons = options.map(function (opt) {
                const b = document.createElement('button');
                b.type = 'button';
                b.className = 'jellyemu-picker-option';
                b.textContent = opt.name;
                if (!opt.available) {
                    b.setAttribute('aria-disabled', 'true');
                    const reason = document.createElement('span');
                    reason.className = 'jellyemu-picker-reason';
                    reason.textContent = opt.reason;
                    b.appendChild(reason);
                }
                b.addEventListener('click', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    if (opt.available) finish(opt.id);
                });
                list.appendChild(b);
                return b;
            });

            const usable = options.map(function (o, i) { return o.available ? i : -1; }).filter(function (i) { return i >= 0; });
            let remembered = null;
            try { remembered = localStorage.getItem(rememberKey(platform)); } catch (e) { /* ignore */ }
            let focus = usable.find(function (i) { return options[i].id === remembered; });
            if (focus === undefined) focus = usable.length ? usable[0] : -1;

            function setFocus(i) {
                buttons.forEach(function (b) { b.classList.remove('je-focus'); });
                focus = i;
                if (i >= 0) { buttons[i].classList.add('je-focus'); buttons[i].focus({ preventScroll: true }); }
            }

            let lastAction = '', lastAt = 0;
            function onKey(e) {
                const action = ACTIONS[e.keyCode];
                e.preventDefault();
                e.stopImmediatePropagation();
                if (!action) return;
                // Some hosts send the same press twice (native key + gamepad-to-key); ignore the echo.
                const now = Date.now();
                if (action === lastAction && now - lastAt < 150) return;
                lastAction = action; lastAt = now;

                if (action === 'cancel') return finish(null);
                if (action === 'select') { if (focus >= 0) finish(options[focus].id); return; }
                if (!usable.length) return;
                const pos = usable.indexOf(focus);
                const next = action === 'up' ? Math.max(0, pos - 1) : Math.min(usable.length - 1, pos + 1);
                setFocus(usable[next]);
            }
            function swallow(e) { e.stopImmediatePropagation(); }

            function finish(result) {
                window.removeEventListener('keydown', onKey, true);
                window.removeEventListener('keyup', swallow, true);
                overlay.remove();
                if (result) { try { localStorage.setItem(rememberKey(platform), result); } catch (e) { /* ignore */ } }
                resolve(result);
            }

            overlay.addEventListener('click', function (e) { if (e.target === overlay) finish(null); });
            window.addEventListener('keydown', onKey, true);
            window.addEventListener('keyup', swallow, true);
            document.body.appendChild(overlay);
            setFocus(focus);
        });
    }
})();

// EXPERIMENT (game streaming): "Play on" picker. Pressing Play asks where to run the game:
// "This device" (the in-browser emulator) or a gaming PC that streams it. Options that can't be
// used right now (system not supported here, PC offline or in use) are shown with the reason.
// Works with controllers, keyboards, remotes, mice and touch; the text never assumes which.
(function () {
    const JE = window.JellyEmu = window.JellyEmu || {};

    // Browser emulators exist for these but are too slow to be playable, so only gaming PCs are offered.
    const TOO_SLOW_IN_BROWSER = new Set(['PlayStation 2']);

    const ICONS = {
        local: '<svg viewBox="0 0 24 24"><path d="M21 3H3c-1.1 0-2 .9-2 2v11c0 1.1.9 2 2 2h6v2H7v2h10v-2h-2v-2h6c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 13H3V5h18v11z"/></svg>',
        pc: '<svg viewBox="0 0 24 24"><path d="M4 4h10v16H4V4zm2 2v2h6V6H6zm0 4v2h6v-2H6zm4 6a1 1 0 1 0 0 2 1 1 0 0 0 0-2zm6-10h4v2h-4V6zm0 4h4v2h-4v-2zm0 4h4v6h-4v-6z"/></svg>'
    };

    const STYLE = `
        .jellyemu-picker { position: fixed; inset: 0; z-index: 100000; display: flex; align-items: center; justify-content: center;
            background: rgba(0,0,0,.6); backdrop-filter: blur(6px); animation: je-picker-fade .15s ease-out; }
        @keyframes je-picker-fade { from { opacity: 0; } to { opacity: 1; } }
        .jellyemu-picker-panel { width: min(460px, 92vw); padding: 22px; border-radius: 16px; color: #fff;
            background: linear-gradient(180deg, #262a31 0%, #1b1d22 100%); border: 1px solid rgba(255,255,255,.08);
            box-shadow: 0 24px 60px rgba(0,0,0,.55); animation: je-picker-rise .18s ease-out; }
        @keyframes je-picker-rise { from { transform: translateY(12px); opacity: 0; } to { transform: none; opacity: 1; } }
        .jellyemu-picker-head { display: flex; align-items: center; gap: 16px; margin-bottom: 18px; }
        .jellyemu-picker-art { width: 64px; height: 64px; flex: none; border-radius: 10px; background: #33373f center / cover no-repeat; }
        .jellyemu-picker-kicker { font-size: .8em; letter-spacing: .08em; text-transform: uppercase; color: rgba(255,255,255,.55); }
        .jellyemu-picker-game { font-size: 1.3em; font-weight: 600; line-height: 1.25; margin-top: 2px; }
        .jellyemu-picker-list { display: flex; flex-direction: column; gap: 10px; }
        .jellyemu-picker-option { display: flex; align-items: center; gap: 14px; width: 100%; padding: 14px 16px; margin: 0;
            border: 2px solid transparent; border-radius: 12px; background: rgba(255,255,255,.06); color: #fff; font: inherit;
            text-align: left; cursor: pointer; outline: none; transition: background .12s, border-color .12s, transform .12s; }
        .jellyemu-picker-option:hover:not([aria-disabled="true"]) { background: rgba(255,255,255,.1); }
        .jellyemu-picker-option.je-focus { border-color: #00a4dc; background: rgba(0,164,220,.16); transform: scale(1.015); }
        .jellyemu-picker-option[aria-disabled="true"] { cursor: default; }
        .jellyemu-picker-option[aria-disabled="true"] .jellyemu-picker-icon,
        .jellyemu-picker-option[aria-disabled="true"] .jellyemu-picker-name { opacity: .45; }
        .jellyemu-picker-icon { width: 40px; height: 40px; flex: none; display: flex; align-items: center; justify-content: center;
            border-radius: 10px; background: rgba(255,255,255,.08); }
        .jellyemu-picker-icon svg { width: 24px; height: 24px; fill: currentColor; }
        .jellyemu-picker-name { font-size: 1.05em; font-weight: 600; }
        .jellyemu-picker-status { display: flex; align-items: center; gap: 6px; margin-top: 3px; font-size: .85em; color: rgba(255,255,255,.65); }
        .jellyemu-picker-dot { width: 8px; height: 8px; border-radius: 50%; background: #6b7078; }
        .jellyemu-picker-dot.je-ready { background: #3ecf6e; box-shadow: 0 0 6px rgba(62,207,110,.7); }
        .jellyemu-picker-checking { padding: 18px 4px; color: rgba(255,255,255,.65); }
        .jellyemu-picker-cancel { display: block; margin: 16px auto 0; padding: 10px 26px; border: 2px solid transparent; border-radius: 999px;
            background: transparent; color: rgba(255,255,255,.75); font: inherit; cursor: pointer; outline: none; }
        .jellyemu-picker-cancel:hover { color: #fff; background: rgba(255,255,255,.08); }
        .jellyemu-picker-cancel.je-focus { color: #fff; border-color: #00a4dc; }
    `;

    // Navigation keys: arrows, Enter/Space, Escape/Backspace, plus the gamepad key codes some
    // hosts send (195/196 face buttons, 203-206 D-pad, 211-214 left stick).
    const ACTIONS = {
        38: 'prev', 203: 'prev', 211: 'prev', 37: 'prev', 205: 'prev', 214: 'prev',
        40: 'next', 204: 'next', 212: 'next', 39: 'next', 206: 'next', 213: 'next',
        13: 'select', 32: 'select', 195: 'select',
        27: 'cancel', 8: 'cancel', 196: 'cancel'
    };

    function rememberKey(platform) { return 'jellyemu-play-on:' + platform; }

    function el(tag, className, text) {
        const e = document.createElement(tag);
        if (className) e.className = className;
        if (text) e.textContent = text;
        return e;
    }

    /**
     * Asks where to play. Resolves to "local", a gaming PC id, or null if cancelled.
     * Resolves to "local" without showing anything when no gaming PC is set up.
     */
    JE.choosePlayDevice = function (itemId) {
        return new Promise(function (resolve) {
            const picker = openPicker(itemId, resolve);
            JE.fetch('/jellyemu/stream/devices/' + itemId)
                .then(function (r) { return r.ok ? r.json() : null; })
                .catch(function () { return null; })
                .then(function (info) {
                    if (picker.closed) return;
                    if (!info || !info.devices || !info.devices.length) { picker.close('local'); return; }
                    const unsupported = JE.ejsUnsupportedPlatforms.has(info.platform);
                    const tooSlow = TOO_SLOW_IN_BROWSER.has(info.platform);
                    const local = {
                        id: 'local', name: 'This device', icon: 'local', available: !unsupported && !tooSlow,
                        reason: unsupported ? "Can't play this system here" : tooSlow ? 'Too demanding to play here' : ''
                    };
                    picker.show(info.name, info.platform,
                        [local].concat(info.devices.map(function (d) { return Object.assign({ icon: 'pc' }, d); })));
                });
        });
    };

    function openPicker(itemId, resolve) {
        if (!document.getElementById('jellyemu-picker-style')) {
            const style = el('style');
            style.id = 'jellyemu-picker-style';
            style.textContent = STYLE;
            document.head.appendChild(style);
        }

        const overlay = el('div', 'jellyemu-picker');
        const panel = el('div', 'jellyemu-picker-panel');
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', 'Play on');
        const head = el('div', 'jellyemu-picker-head');
        const art = el('div', 'jellyemu-picker-art');
        art.style.backgroundImage = 'url("' + JE.getUrl('/Items/' + itemId + '/Images/Primary?fillHeight=128&quality=90') + '")';
        const titles = el('div');
        titles.appendChild(el('div', 'jellyemu-picker-kicker', 'Play on'));
        const game = el('div', 'jellyemu-picker-game', '');
        titles.appendChild(game);
        head.appendChild(art);
        head.appendChild(titles);
        const list = el('div', 'jellyemu-picker-list');
        list.appendChild(el('div', 'jellyemu-picker-checking', 'Checking where this game can play…'));
        const cancel = el('button', 'jellyemu-picker-cancel', 'Cancel');
        cancel.type = 'button';
        panel.appendChild(head);
        panel.appendChild(list);
        panel.appendChild(cancel);
        overlay.appendChild(panel);

        const state = { closed: false, platform: null, targets: [cancel], focus: 0 };

        function setFocus(i) {
            state.targets.forEach(function (t) { t.classList.remove('je-focus'); });
            state.focus = i;
            const t = state.targets[i];
            if (t) { t.classList.add('je-focus'); t.focus({ preventScroll: true }); }
        }

        function close(result) {
            if (state.closed) return;
            state.closed = true;
            window.removeEventListener('keydown', onKey, true);
            window.removeEventListener('keyup', swallow, true);
            overlay.remove();
            if (result && state.platform) {
                try { localStorage.setItem(rememberKey(state.platform), result); } catch (e) { /* ignore */ }
            }
            resolve(result);
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

            if (action === 'cancel') return close(null);
            if (action === 'select') { state.targets[state.focus].click(); return; }
            const n = state.targets.length;
            setFocus(action === 'prev' ? Math.max(0, state.focus - 1) : Math.min(n - 1, state.focus + 1));
        }
        function swallow(e) { e.stopImmediatePropagation(); }

        cancel.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); close(null); });
        overlay.addEventListener('click', function (e) { if (e.target === overlay) close(null); });
        window.addEventListener('keydown', onKey, true);
        window.addEventListener('keyup', swallow, true);
        document.body.appendChild(overlay);
        setFocus(0);

        return {
            get closed() { return state.closed; },
            close: close,
            show: function (name, platform, options) {
                state.platform = platform;
                game.textContent = name || '';
                list.textContent = '';
                const usable = [];
                options.forEach(function (opt) {
                    const b = el('button', 'jellyemu-picker-option');
                    b.type = 'button';
                    const icon = el('div', 'jellyemu-picker-icon');
                    icon.innerHTML = ICONS[opt.icon] || ICONS.pc;
                    const text = el('div');
                    text.appendChild(el('div', 'jellyemu-picker-name', opt.name));
                    const status = el('div', 'jellyemu-picker-status');
                    status.appendChild(el('span', 'jellyemu-picker-dot' + (opt.available ? ' je-ready' : '')));
                    status.appendChild(document.createTextNode(opt.available ? 'Ready' : opt.reason));
                    text.appendChild(status);
                    b.appendChild(icon);
                    b.appendChild(text);
                    if (opt.available) {
                        b.addEventListener('click', function (e) { e.preventDefault(); e.stopPropagation(); close(opt.id); });
                        usable.push({ id: opt.id, button: b });
                    } else {
                        b.setAttribute('aria-disabled', 'true');
                        b.tabIndex = -1;
                    }
                    list.appendChild(b);
                });
                // Unavailable options are shown but skipped when moving between choices.
                state.targets = usable.map(function (u) { return u.button; }).concat([cancel]);
                let remembered = null;
                try { remembered = localStorage.getItem(rememberKey(platform)); } catch (e) { /* ignore */ }
                const start = usable.findIndex(function (u) { return u.id === remembered; });
                setFocus(start >= 0 ? start : 0);
            }
        };
    }
})();

/**
 * JellyEmu stream host: the Jellyfin page around a game streamed from a gaming PC.
 *
 * Holds the stream (the bridge's page, in an iframe) and everything JellyEmu adds to it:
 *  - asks JellyEmu for a one-time pass to the stream, and keeps the gaming PC reserved while open;
 *  - sends the player's control bindings to the stream layer inside the iframe
 *    (jellyemu.streamlayer.js), which turns every input into one controller for the gaming PC;
 *  - the menu (Escape / the controller combo / the on-screen menu button): resume, on-screen
 *    controls, exit;
 *  - on-screen controls for touch screens, shown per device like in the browser emulator;
 *  - a loading screen until the picture is ready (progress, Cancel, Details with the full log),
 *    which also explains why a stream couldn't start or stopped.
 *
 * Configured by the page through window.JE_STREAM:
 *   { itemId, deviceQuery, exitUrl, gameName, deviceName, scheme, customBindings }
 * Per-device settings (JellyEmu settings > Game Streaming), in localStorage:
 *   jellyemu-stream-transport  auto | webrtc | websocket   how the stream connects
 *   jellyemu-stream-details    "1": open Details on the loading screen straight away
 */
(function () {
    'use strict';

    var C = window.JE_STREAM || {};
    var JE = window.JellyEmu;
    var frame = document.getElementById('je-stream');
    var streamOrigin = null;
    var ids = { dpad: [4, 5, 6, 7], face: [0, 8, 1, 9] };

    // ---- Look ---------------------------------------------------------------------------------
    try {
        var accent = localStorage.getItem('jellyemu-accent');
        if (accent && /^[#a-z0-9(),.\s%-]+$/i.test(accent)) document.documentElement.style.setProperty('--je-accent', accent);
    } catch (e) { /* ignore */ }

    var css = document.createElement('style');
    css.textContent = [
        ':root { --je-accent: #00a4dc; }',
        '#je-menu { position: fixed; inset: 0; z-index: 20; display: none; align-items: center; justify-content: center;',
        '  background: rgba(0,0,0,.6); font-family: system-ui, sans-serif; }',
        '#je-menu.je-open { display: flex; }',
        '#je-menu .je-panel:focus { outline: none; }',
        '#je-menu .je-panel { min-width: min(80vw, 340px); padding: 18px; border-radius: 14px; background: rgba(24,24,28,.96);',
        '  box-shadow: 0 10px 40px rgba(0,0,0,.6); display: flex; flex-direction: column; gap: 10px; }',
        '#je-menu h2 { margin: 0 0 4px; color: #fff; font-size: 18px; font-weight: 600; text-align: center; }',
        '#je-menu button { font: 500 17px system-ui, sans-serif; color: #fff; background: rgba(255,255,255,.08);',
        '  border: 2px solid transparent; border-radius: 10px; padding: 13px 16px; text-align: left; cursor: pointer; outline: none; }',
        '#je-menu button.je-focus { border-color: var(--je-accent); background: rgba(0,164,220,.18);',
        '  background: color-mix(in srgb, var(--je-accent) 22%, transparent); }',
        // Loading screen: progress while the stream connects, and what went wrong if it can't.
        '#je-loading { position: fixed; inset: 0; z-index: 25; display: flex; align-items: center; justify-content: center;',
        '  background: #0b0b0e; color: #fff; font-family: system-ui, sans-serif; padding: 24px; box-sizing: border-box; }',
        '#je-loading.je-hidden { display: none; }',
        '#je-loading .je-card { width: min(92vw, 620px); display: flex; flex-direction: column; align-items: center; gap: 14px; text-align: center; }',
        '#je-loading .je-card:focus { outline: none; }',
        '#je-loading h1 { margin: 0; font-size: clamp(20px, 4vmin, 30px); font-weight: 600; }',
        '#je-loading .je-sub { color: #aaa; font-size: 15px; margin-top: -6px; }',
        '#je-loading .je-status { color: #ddd; font-size: 16px; line-height: 1.45; min-height: 1.45em; white-space: pre-line; }',
        '#je-loading.je-error .je-status { color: #ffb4a8; }',
        '#je-loading .je-spin { width: 34px; height: 34px; border-radius: 50%; border: 3px solid rgba(255,255,255,.15);',
        '  border-top-color: var(--je-accent); animation: je-spin 0.9s linear infinite; }',
        '#je-loading.je-error .je-spin { display: none; }',
        '@keyframes je-spin { to { transform: rotate(360deg); } }',
        '#je-loading .je-actions { display: flex; gap: 10px; margin-top: 6px; }',
        '#je-loading button { font: 500 16px system-ui, sans-serif; color: #fff; background: rgba(255,255,255,.08); cursor: pointer;',
        '  border: 2px solid transparent; border-radius: 10px; padding: 10px 20px; outline: none; }',
        '#je-loading button.je-focus { border-color: var(--je-accent); background: rgba(0,164,220,.18);',
        '  background: color-mix(in srgb, var(--je-accent) 22%, transparent); }',
        '#je-loading .je-log { display: none; width: 100%; max-height: 38vh; overflow: auto; text-align: left; margin: 0;',
        '  background: rgba(255,255,255,.05); border-radius: 8px; padding: 10px 12px; box-sizing: border-box;',
        '  font: 12px/1.5 ui-monospace, Consolas, monospace; color: #bbb; white-space: pre-wrap; word-break: break-word; }',
        '#je-loading.je-details .je-log { display: block; }',
        '#je-menu-btn { position: fixed; z-index: 15; top: 10px; left: 50%; transform: translateX(-50%); display: none;',
        '  width: 46px; height: 34px; border-radius: 17px; border: 0; background: rgba(0,0,0,.45); color: #fff;',
        '  font: 20px/34px system-ui, sans-serif; touch-action: none; }',
        '#je-menu-btn.je-show { display: block; }',
        '#je-touch { position: fixed; inset: 0; z-index: 10; pointer-events: none; display: none; user-select: none;',
        '  -webkit-user-select: none; -webkit-touch-callout: none; }',
        'body.je-touch-on #je-touch { display: block; }',
        '.je-ctl { position: absolute; pointer-events: auto; touch-action: none; color: rgba(255,255,255,.85);',
        '  font: 600 calc(var(--s) * .3) system-ui, sans-serif; }',
        '.je-btn { width: var(--s); height: var(--s); border-radius: 50%; background: rgba(255,255,255,.14);',
        '  border: 2px solid rgba(255,255,255,.35); display: flex; align-items: center; justify-content: center; box-sizing: border-box; }',
        '.je-btn.je-down, .je-pill.je-down, .je-sh.je-down { background: rgba(255,255,255,.4); }',
        '.je-pill { height: 7vmin; padding: 0 3vmin; border-radius: 4vmin; background: rgba(255,255,255,.14);',
        '  border: 2px solid rgba(255,255,255,.3); display: flex; align-items: center; font-size: 3vmin; box-sizing: border-box; }',
        '.je-sh { width: 15vmin; height: 9vmin; border-radius: 3vmin; background: rgba(255,255,255,.14); border: 2px solid rgba(255,255,255,.3);',
        '  display: flex; align-items: center; justify-content: center; font-size: 3.6vmin; box-sizing: border-box; }',
        '.je-dpad { width: var(--s); height: var(--s); }',
        '.je-dpad i { position: absolute; background: rgba(255,255,255,.14); border: 2px solid rgba(255,255,255,.35); box-sizing: border-box; }',
        '.je-dpad i.je-h { left: 0; right: 0; top: 33%; height: 34%; border-radius: 2vmin; }',
        '.je-dpad i.je-v { top: 0; bottom: 0; left: 33%; width: 34%; border-radius: 2vmin; }',
        '.je-dpad b { position: absolute; width: 34%; height: 34%; background: rgba(255,255,255,.35); display: none; }',
        '.je-dpad b.je-down { display: block; }',
        '.je-stick { width: var(--s); height: var(--s); border-radius: 50%; background: rgba(255,255,255,.08);',
        '  border: 2px solid rgba(255,255,255,.3); box-sizing: border-box; }',
        '.je-stick span { position: absolute; left: 30%; top: 30%; width: 40%; height: 40%; border-radius: 50%;',
        '  background: rgba(255,255,255,.35); pointer-events: none; }',
        // Portrait: the picture goes to the top, the controls below it.
        '@media (orientation: portrait) {',
        '  body.je-touch-on #je-stream { height: 56vh; }',
        '  .je-top { top: calc(56vh + 3vmin) !important; }',
        '  .je-row { bottom: auto !important; top: calc(56vh + 14vmin); }',
        '  .je-analog .je-p-dpad { left: 3vmin !important; }',
        '  .je-analog .je-p-lstick { bottom: 38vmin !important; }',
        '  .je-analog .je-p-rstick { right: 6vmin !important; }',
        '  .je-analog .je-p-face { bottom: 34vmin !important; }',
        '}'
    ].join('\n');
    document.head.appendChild(css);

    // ---- Bindings -----------------------------------------------------------------------------
    // The player's saved bindings for this system (camelCase), else the scheme's defaults.
    function lower(b) {
        return {
            kb1: b.kb1 !== undefined ? b.kb1 : (b.Kb1 || 0), kb2: b.kb2 !== undefined ? b.kb2 : (b.Kb2 || 0),
            gp1: b.gp1 !== undefined ? b.gp1 : (b.Gp1 || ''), gp2: b.gp2 !== undefined ? b.gp2 : (b.Gp2 || '')
        };
    }
    function buildBindings() {
        var scheme = C.scheme || {};
        var defaults = scheme.defaultBindings || scheme.DefaultBindings || {};
        var saved = C.customBindings && typeof C.customBindings === 'object' ? C.customBindings : null;
        var out = {};
        var buttons = scheme.buttons || scheme.Buttons || [];
        buttons.forEach(function (btn) {
            var id = btn.id !== undefined ? btn.id : btn.Id;
            var b = (saved && saved[id]) || defaults[id];
            if (b) out[id] = lower(b);
        });
        if (!out[31]) out[31] = { kb1: 27, kb2: 0, gp1: 'LEFT_BOTTOM_SHOULDER+RIGHT_BOTTOM_SHOULDER+LEFT_STICK+RIGHT_STICK', gp2: '' };
        return out;
    }
    var bindings = buildBindings();

    function schemeButtons() {
        var scheme = C.scheme || {};
        var map = {};
        (scheme.buttons || scheme.Buttons || []).forEach(function (btn) {
            map[btn.id !== undefined ? btn.id : btn.Id] = String(btn.label || btn.Label || '');
        });
        return map;
    }

    // ---- Talking to the stream layer ----------------------------------------------------------
    function post(type, data) {
        if (!streamOrigin || !frame || !frame.contentWindow) return;
        var msg = { je: true, type: type };
        if (data) for (var k in data) msg[k] = data[k];
        frame.contentWindow.postMessage(msg, streamOrigin);
    }

    var started = false;
    window.addEventListener('message', function (e) {
        if (!frame || e.source !== frame.contentWindow || e.origin !== streamOrigin || !e.data || e.data.je !== true) return;
        switch (e.data.type) {
            case 'ready':
                post('config', { bindings: bindings });
                post('menu', { open: menuOpen });
                break;
            case 'started':
                started = true;
                updateTouch();
                break;
            case 'input':
                JE.noteInput(e.data.kind);
                updateTouch();
                break;
            case 'stream':
                onStreamInfo(e.data);
                break;
            case 'menu-toggle':
                if (loadingVisible) break;   // the loading screen has its own Cancel
                if (menuOpen) closeMenu(); else openMenu();
                break;
            case 'exit':
                exitGame();
                break;
        }
    });

    // ---- Loading screen -----------------------------------------------------------------------
    // Covers the stream until its picture is ready: what's happening (from the bridge's progress,
    // passed on by the stream layer), Cancel, and Details (the full log). If the stream can't start,
    // or stops, it says why. Works with a controller, touch, mouse or keyboard.
    var loadingEl = null, loadingVisible = false, loadingError = false, loadingStarted = Date.now();
    var loadingButtons = [], loadingFocus = 0, loadingPrev = null, loadingNextAt = 0;
    var fatalLines = [], fatalTimer = null, slowTimer = null, fellBack = false;

    function deviceName() { return C.deviceName || 'the gaming PC'; }

    function buildLoading() {
        loadingEl = document.createElement('div');
        loadingEl.id = 'je-loading';
        loadingEl.innerHTML =
            '<div class="je-card" tabindex="-1" role="dialog" aria-live="polite">' +
            '<h1></h1><div class="je-sub"></div><div class="je-spin"></div><div class="je-status"></div>' +
            '<div class="je-actions"><button type="button" data-act="cancel"></button>' +
            '<button type="button" data-act="details"></button></div><pre class="je-log"></pre></div>';
        loadingEl.querySelector('h1').textContent = C.gameName || 'Starting the game';
        loadingEl.querySelector('.je-sub').textContent = 'Streamed from ' + deviceName();
        loadingButtons = Array.prototype.slice.call(loadingEl.querySelectorAll('button'));
        loadingEl.addEventListener('click', function (e) {
            var b = e.target.closest && e.target.closest('button');
            if (b) activateLoading(b.getAttribute('data-act'));
        });
        if (localSetting('jellyemu-stream-details', '') === '1') loadingEl.classList.add('je-details');
        document.body.appendChild(loadingEl);
        refreshLoadingButtons();
    }

    function refreshLoadingButtons() {
        loadingButtons[0].textContent = loadingError ? 'Back' : 'Cancel';
        loadingButtons[1].textContent = loadingEl.classList.contains('je-details') ? 'Hide details' : 'Details';
    }

    function setLoadingFocus(i) {
        loadingFocus = (i + loadingButtons.length) % loadingButtons.length;
        loadingButtons.forEach(function (b, n) { b.classList.toggle('je-focus', n === loadingFocus); });
    }

    function showLoading() {
        if (!loadingEl) buildLoading();
        loadingEl.classList.remove('je-hidden');
        loadingVisible = true;
        // Keys come here (not to the stream) while it shows; the card takes focus, not the buttons,
        // so a host that also turns a controller press into a click can't press one twice.
        try { loadingEl.querySelector('.je-card').focus({ preventScroll: true }); } catch (e) { /* ignore */ }
        setLoadingFocus(0);
        var gp = firstPad();
        loadingPrev = { a: pressed(gp, 0), b: pressed(gp, 1), dir: loadingDirection(gp) };
        loadingNextAt = Date.now() + 400;
        requestAnimationFrame(pollLoading);
        if (!loadingError && !slowTimer) {
            setLoadingStatus('Connecting to ' + deviceName() + '…');
            slowTimer = setTimeout(function () {
                if (loadingVisible && !loadingError) {
                    setLoadingStatus(statusText + '\nThis is taking longer than usual. Check that ' + deviceName() + ' is switched on and awake.');
                }
            }, 45000);
        }
    }

    function hideLoading() {
        if (!loadingEl || loadingError) return;
        loadingEl.classList.add('je-hidden');
        loadingVisible = false;
        clearTimeout(slowTimer);
        if (frame) try { frame.focus(); } catch (e) { /* ignore */ }
    }

    var statusText = '';
    function setLoadingStatus(text) {
        statusText = text;
        if (loadingEl) loadingEl.querySelector('.je-status').textContent = text;
    }

    function addLog(line) {
        if (!loadingEl) buildLoading();
        var log = loadingEl.querySelector('.je-log');
        var seconds = ((Date.now() - loadingStarted) / 1000).toFixed(1);
        log.textContent += (log.textContent ? '\n' : '') + seconds.padStart(6, ' ') + 's  ' + line;
        log.scrollTop = log.scrollHeight;
    }

    function showLoadingError(title, message) {
        loadingError = true;
        clearTimeout(slowTimer);
        showLoading();
        loadingEl.classList.add('je-error');
        loadingEl.querySelector('h1').textContent = title;
        setLoadingStatus(message);
        addLog('ERROR: ' + message);
        refreshLoadingButtons();
    }

    function activateLoading(act) {
        if (act === 'cancel') {
            // Nothing to quit if JellyEmu never gave a pass (e.g. the PC was in use by someone else).
            if (streamOrigin) exitGame(); else leave();
        } else if (act === 'details') {
            loadingEl.classList.toggle('je-details');
            refreshLoadingButtons();
        }
    }

    function loadingDirection(gp) {
        if (!gp) return null;
        var ax = gp.axes[0] || 0, ay = gp.axes[1] || 0;
        if (pressed(gp, 12) || pressed(gp, 14) || ax < -0.5 || ay < -0.5) return -1;
        if (pressed(gp, 13) || pressed(gp, 15) || ax > 0.5 || ay > 0.5) return 1;
        return null;
    }

    function pollLoading() {
        if (!loadingVisible) return;
        requestAnimationFrame(pollLoading);
        if (menuOpen) return;
        var gp = firstPad();
        var a = pressed(gp, 0), b = pressed(gp, 1), dir = loadingDirection(gp);
        if (a && !loadingPrev.a) activateLoading(loadingButtons[loadingFocus].getAttribute('data-act'));
        else if (b && !loadingPrev.b) activateLoading('cancel');
        var now = Date.now();
        if (dir !== loadingPrev.dir) { if (dir) { setLoadingFocus(loadingFocus + dir); loadingNextAt = now + 400; } }
        else if (dir && now >= loadingNextAt) { setLoadingFocus(loadingFocus + dir); loadingNextAt = now + 180; }
        loadingPrev = { a: a, b: b, dir: dir };
    }

    // What the bridge reports, in words for people (the raw lines go to Details).
    function onStreamInfo(info) {
        var line = info.line || info.message || '';
        if (line) addLog((info.level ? '[' + info.level + '] ' : '') + line);
        switch (info.kind) {
            case 'connectionComplete':
                if (!loadingError) setLoadingStatus('Starting the game on ' + deviceName() + '…');
                break;
            case 'serverMessage':
                if (!loadingError && info.message) setLoadingStatus(info.message);
                break;
            case 'videoReady':
                hideLoading();
                break;
            case 'addDebugLine':
                if (info.level === 'fatal' || info.level === 'fatalDescription') {
                    // A fatal error can come as several lines: gather them for a moment.
                    fatalLines.push(line);
                    clearTimeout(fatalTimer);
                    fatalTimer = setTimeout(function () {
                        showLoadingError(started ? 'The stream stopped' : 'Couldn\'t start the stream', fatalLines.join('\n'));
                        fatalLines = [];
                    }, 400);
                } else if (loadingError) {
                    // (only the log after an error)
                } else if (/Falling back to Web Socket/i.test(line)) {
                    fellBack = true;
                    setLoadingStatus('A direct connection isn\'t possible on this network, so the stream goes through Jellyfin instead (a little slower)…');
                } else if (/Trying WebRTC transport/i.test(line)) {
                    setLoadingStatus('Connecting directly to ' + deviceName() + '…');
                } else if (/Trying Web Socket transport/i.test(line) && !fellBack) {
                    setLoadingStatus('Connecting to ' + deviceName() + ' through Jellyfin…');
                }
                break;
        }
    }

    // ---- Starting and leaving -----------------------------------------------------------------
    // Per-device streaming settings (JellyEmu settings > Game Streaming).
    function localSetting(key, fallback) {
        try { return localStorage.getItem(key) || fallback; } catch (e) { return fallback; }
    }
    var transport = localSetting('jellyemu-stream-transport', 'auto');
    if (!/^(auto|webrtc|websocket)$/.test(transport)) transport = 'auto';

    showLoading();
    addLog('Asking JellyEmu for a stream from ' + deviceName() + ' (connection: ' + transport + ')');
    JE.fetch('/jellyemu/stream/pass/' + encodeURIComponent(C.itemId) + (C.deviceQuery || ''), { method: 'POST' })
        .then(function (r) {
            if (r.status === 409) return r.json().then(function (d) { throw new Error(d.message); });
            if (r.status === 503) throw new Error('No gaming PC is set up for streaming.');
            if (!r.ok) throw new Error('JellyEmu couldn\'t start the stream (HTTP ' + r.status + ').');
            return r.json();
        })
        .then(function (d) {
            streamOrigin = new URL(d.url).origin;
            addLog('Opening the stream at ' + streamOrigin);
            // Settings for the stream layer ride along in the fragment (kept through the redirect).
            frame.src = d.url + '#je-transport=' + encodeURIComponent(transport);
            frame.addEventListener('load', function () {
                if (!loadingVisible) try { frame.focus(); } catch (e) { /* ignore */ }
            });
        })
        .catch(function (e) { showLoadingError('Can\'t play right now', e.message); });

    // Tell JellyEmu the stream is still open, so nobody else takes over the gaming PC.
    setInterval(function () {
        JE.fetch('/jellyemu/stream/heartbeat' + (C.deviceQuery || ''), { method: 'POST' }).catch(function () {});
    }, 15000);

    // Back to the game's page. Step back rather than loading it again, so the page isn't in the
    // history twice. The iframe is removed first so its own history can't swallow the step.
    function leave() {
        if (frame && frame.parentNode) frame.parentNode.removeChild(frame);
        frame = null;
        var here = location.href;
        if (history.length > 1) history.back();
        setTimeout(function () { if (location.href === here) location.replace(C.exitUrl); }, 1500);
    }

    var exiting = false;
    function exitGame() {
        if (exiting) return;
        exiting = true;
        // Quit the game on the gaming PC, not just the stream.
        try { JE.fetch('/jellyemu/stream/quit' + (C.deviceQuery || ''), { method: 'POST', keepalive: true }).catch(function () {}); } catch (e) { /* ignore */ }
        leave();
    }

    // ---- Controllers (this page reads them only for the menu) ---------------------------------
    function firstPad() {
        var pads = [];
        try { pads = navigator.getGamepads ? navigator.getGamepads() : []; } catch (e) { /* ignore */ }
        for (var i = 0; i < pads.length; i++) {
            if (pads[i] && pads[i].connected !== false && pads[i].mapping === 'standard') return pads[i];
        }
        return null;
    }
    function pressed(gp, i) { var b = gp && gp.buttons[i]; return !!(b && (b.pressed || b.value > 0.5)); }

    // ---- Menu ---------------------------------------------------------------------------------
    var menu = document.createElement('div');
    menu.id = 'je-menu';
    menu.innerHTML = '<div class="je-panel" role="dialog" aria-label="Menu"><h2>Menu</h2>' +
        '<button type="button" data-act="resume">Resume</button>' +
        '<button type="button" data-act="touch"></button>' +
        '<button type="button" data-act="exit">Exit game</button></div>';
    document.body.appendChild(menu);
    var menuButtons = Array.prototype.slice.call(menu.querySelectorAll('button'));
    // Keys come here while the menu is open. The panel takes focus, not the buttons, so a host
    // that also turns a controller press into a click can't press a button twice.
    var panel = menu.querySelector('.je-panel');
    panel.tabIndex = -1;
    var menuOpen = false;
    var focusIndex = 0;

    function setFocus(i) {
        focusIndex = (i + menuButtons.length) % menuButtons.length;
        menuButtons.forEach(function (b, n) { b.classList.toggle('je-focus', n === focusIndex); });
    }

    function refreshMenu() {
        menu.querySelector('[data-act="touch"]').textContent = touchVisible() ? 'Hide on-screen controls' : 'Show on-screen controls';
    }

    function openMenu() {
        if (menuOpen || exiting) return;
        menuOpen = true;
        post('menu', { open: true });
        releaseTouches();
        refreshMenu();
        menu.classList.add('je-open');
        var gp = firstPad();
        padPrev = { a: pressed(gp, 0), b: pressed(gp, 1), dir: padDirection(gp) };
        dirNextAt = Date.now() + 400;
        setFocus(0);
        try { panel.focus({ preventScroll: true }); } catch (e) { /* ignore */ }
        requestAnimationFrame(pollMenu);
    }

    function closeMenu() {
        if (!menuOpen) return;
        menuOpen = false;
        menu.classList.remove('je-open');
        post('menu', { open: false });
        if (frame) try { frame.focus(); } catch (e) { /* ignore */ }
    }

    function activate(act) {
        if (act === 'resume') closeMenu();
        else if (act === 'exit') { closeMenu(); exitGame(); }
        else if (act === 'touch') {
            JE.setTouchControlsMode(touchVisible() ? 'off' : 'on');
            updateTouch();
            refreshMenu();
        }
    }

    menu.addEventListener('click', function (e) {
        var b = e.target.closest && e.target.closest('button');
        if (b) activate(b.getAttribute('data-act'));
        else if (e.target === menu) closeMenu();
    });

    // Keyboard and TV remotes. Key events some hosts make up for controller buttons (key
    // "Unidentified") are left to the controller polling below.
    window.addEventListener('keydown', function (e) {
        if (e.key === 'Unidentified') return;
        if (loadingVisible && !menuOpen) {
            var used = true;
            if (/^Arrow(Left|Up)$/.test(e.key)) setLoadingFocus(loadingFocus - 1);
            else if (/^Arrow(Right|Down)$/.test(e.key) || e.key === 'Tab') setLoadingFocus(loadingFocus + 1);
            else if (e.key === 'Enter' || e.key === ' ') activateLoading(loadingButtons[loadingFocus].getAttribute('data-act'));
            else if (e.key === 'Escape' || e.key === 'Backspace') activateLoading('cancel');
            else used = false;
            if (used) { e.preventDefault(); e.stopPropagation(); }
            return;
        }
        if (!menuOpen) {
            // Focus ended up out here (a tap on the controls, say): Escape still opens the
            // menu, and anything else goes back to the game.
            if (e.key === 'Escape') { e.preventDefault(); openMenu(); }
            else if (frame) try { frame.focus(); } catch (err) { /* ignore */ }
            return;
        }
        var handled = true;
        if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') setFocus(focusIndex - 1);
        else if (e.key === 'ArrowDown' || e.key === 'ArrowRight' || e.key === 'Tab') setFocus(focusIndex + 1);
        else if (e.key === 'Enter' || e.key === ' ') activate(menuButtons[focusIndex].getAttribute('data-act'));
        else if (e.key === 'Escape' || e.key === 'Backspace') closeMenu();
        else handled = false;
        if (handled) { e.preventDefault(); e.stopPropagation(); }
    }, true);

    var padPrev = { a: false, b: false, dir: null };
    var dirNextAt = 0;
    function padDirection(gp) {
        if (!gp) return null;
        var ay = gp.axes[1] || 0;
        if (pressed(gp, 12) || pressed(gp, 14) || ay < -0.5) return -1;
        if (pressed(gp, 13) || pressed(gp, 15) || ay > 0.5) return 1;
        return null;
    }
    function pollMenu() {
        if (!menuOpen) return;
        var gp = firstPad();
        var a = pressed(gp, 0), b = pressed(gp, 1), dir = padDirection(gp);
        if (a && !padPrev.a) activate(menuButtons[focusIndex].getAttribute('data-act'));
        else if (b && !padPrev.b) closeMenu();
        var now = Date.now();
        if (dir !== padPrev.dir) { if (dir) { setFocus(focusIndex + dir); dirNextAt = now + 400; } }
        else if (dir && now >= dirNextAt) { setFocus(focusIndex + dir); dirNextAt = now + 130; }
        padPrev = { a: a, b: b, dir: dir };
        if (menuOpen) requestAnimationFrame(pollMenu);
    }

    // The menu button, for touch screens (a controller or keyboard has its own way in).
    var menuBtn = document.createElement('button');
    menuBtn.id = 'je-menu-btn';
    menuBtn.type = 'button';
    menuBtn.setAttribute('aria-label', 'Menu');
    menuBtn.textContent = '☰';
    menuBtn.addEventListener('pointerdown', function (e) { e.preventDefault(); openMenu(); });
    document.body.appendChild(menuBtn);

    // ---- On-screen controls -------------------------------------------------------------------
    // Shown per device, like in the browser emulator: "auto" follows what the player last used
    // (a tap shows them, a controller, remote or keyboard hides them), "on"/"off" always/never.
    function touchVisible() {
        var mode = JE.getTouchControlsMode();
        if (mode === 'on') return true;
        if (mode === 'off') return false;
        return JE.usingTouch();
    }
    function updateTouch() {
        var show = started && touchVisible();
        if (show && !pad) buildPad();
        document.body.classList.toggle('je-touch-on', show);
        menuBtn.classList.toggle('je-show', started && (show || JE.usingTouch()));
        if (!show) releaseTouches();
    }

    var pad = null;        // the controls' container, built on first use
    var pointers = {};     // pointerId -> { x, y, stick: element or null, cx, cy, r }
    var touchIds = {};
    var sticks = [];

    function el(cls, style, parent, html) {
        var d = document.createElement('div');
        d.className = cls;
        if (style) d.style.cssText = style;
        if (html) d.innerHTML = html;
        (parent || pad).appendChild(d);
        return d;
    }

    function shortLabel(label, id) {
        var first = (label || '').split(' ')[0];
        return first || String(id);
    }

    function buildPad() {
        pad = document.createElement('div');
        pad.id = 'je-touch';
        document.body.appendChild(pad);

        var labels = schemeButtons();
        var has = function (id) { return Object.prototype.hasOwnProperty.call(labels, id); };
        var leftStick = has(16) || has(17), rightStick = has(20) || has(21);
        var analog = leftStick || rightStick;
        if (analog) pad.classList.add('je-analog');

        // D-pad and left stick on the left, face buttons and right stick on the right.
        if (ids.dpad.some(has)) {
            var dp = el('je-ctl je-dpad je-p-dpad', analog
                ? '--s: 30vmin; left: 22vmin; bottom: 3vmin;'
                : '--s: 36vmin; left: 5vmin; bottom: 6vmin;', null,
                '<i class="je-h"></i><i class="je-v"></i><b data-d="4" style="left:33%;top:0"></b><b data-d="5" style="left:33%;bottom:0"></b>' +
                '<b data-d="6" style="left:0;top:33%"></b><b data-d="7" style="right:0;top:33%"></b>');
            dp.setAttribute('data-ctl', 'dpad');
        }
        if (leftStick) makeStick('je-p-lstick', '--s: 28vmin; left: 3vmin; bottom: 34vmin;', [16, 17, 18, 19]);
        if (rightStick) makeStick('je-p-rstick', '--s: 26vmin; right: 26vmin; bottom: 3vmin;', [20, 21, 22, 23]);

        var face = ids.face.filter(has);
        if (face.length) {
            var size = analog ? 34 : 36;
            var box = el('je-ctl je-p-face', 'left:auto; right: 4vmin; bottom: ' + (analog ? 30 : 6) + 'vmin; width:' + size + 'vmin; height:' + size + 'vmin; pointer-events:none;');
            var twoButton = face.length === 2 && has(0) && has(8);
            // Positions: south, east, west, north (two-button systems sit on a diagonal).
            var pos = twoButton
                ? { 0: 'left:0; bottom:8%;', 8: 'right:0; top:8%;' }
                : { 0: 'left:32%; bottom:0;', 8: 'right:0; top:32%;', 1: 'left:0; top:32%;', 9: 'left:32%; top:0;' };
            face.forEach(function (id) {
                var b = el('je-ctl je-btn', '--s:' + (twoButton ? 15 : 12) + 'vmin; ' + pos[id], box);
                b.textContent = shortLabel(labels[id], id);
                b.setAttribute('data-ctl', 'btn');
                b.setAttribute('data-ids', String(id));
            });
        }

        // Shoulders along the top: left side 12 then 10, right side 11 then 13.
        var shoulder = function (id, style) {
            if (!has(id)) return;
            var s = el('je-ctl je-sh je-top', 'top: 3vmin; ' + style);
            s.textContent = shortLabel(labels[id], id);
            s.setAttribute('data-ctl', 'btn');
            s.setAttribute('data-ids', String(id));
        };
        shoulder(12, 'left: 3vmin;');
        shoulder(10, has(12) ? 'left: 20vmin;' : 'left: 3vmin;');
        shoulder(13, 'right: 3vmin;');
        shoulder(11, has(13) ? 'right: 20vmin;' : 'right: 3vmin;');

        // Along the bottom middle: stick clicks (if any), Select, Start.
        var row = el('je-row', 'position:absolute; left:0; right:0; bottom:3vmin; display:flex; justify-content:center; gap:2vmin; pointer-events:none;');
        [14, 2, 3, 15].forEach(function (id) {
            if (!has(id)) return;
            var s = el('je-ctl je-pill', 'position: relative;', row);
            s.textContent = labels[id] || String(id);
            s.setAttribute('data-ctl', 'btn');
            s.setAttribute('data-ids', String(id));
        });

        document.addEventListener('pointerdown', onPointer, { passive: false });
        document.addEventListener('pointermove', onPointer, { passive: false });
        document.addEventListener('pointerup', onPointerEnd);
        document.addEventListener('pointercancel', onPointerEnd);
        pad.addEventListener('contextmenu', function (e) { e.preventDefault(); });
    }

    function makeStick(cls, style, dirIds) {
        var s = el('je-ctl je-stick ' + cls, style, null, '<span></span>');
        s.setAttribute('data-ctl', 'stick');
        s._ids = dirIds; // right, left, down, up
        sticks.push(s);
    }

    function onPointer(e) {
        if (!document.body.classList.contains('je-touch-on') || menuOpen) return;
        var p = pointers[e.pointerId];
        if (e.type === 'pointerdown') {
            var ctl = e.target.closest && e.target.closest('#je-touch [data-ctl]');
            if (!ctl) return;
            e.preventDefault();
            p = pointers[e.pointerId] = { x: e.clientX, y: e.clientY, stick: null };
            if (ctl.getAttribute('data-ctl') === 'stick') {
                var r = ctl.getBoundingClientRect();
                p.stick = ctl; p.cx = r.left + r.width / 2; p.cy = r.top + r.height / 2; p.r = r.width / 2;
            }
        } else if (!p) {
            return;
        }
        p.x = e.clientX; p.y = e.clientY;
        recompute();
    }

    function onPointerEnd(e) {
        if (!pointers[e.pointerId]) return;
        var p = pointers[e.pointerId];
        delete pointers[e.pointerId];
        if (p.stick) p.stick.firstChild.style.transform = '';
        recompute();
    }

    function recompute() {
        var next = {};
        var dpadDown = {};
        Object.keys(pointers).forEach(function (pid) {
            var p = pointers[pid];
            if (p.stick) {
                var dx = (p.x - p.cx) / p.r, dy = (p.y - p.cy) / p.r;
                var len = Math.sqrt(dx * dx + dy * dy);
                if (len > 1) { dx /= len; dy /= len; }
                p.stick.firstChild.style.transform = 'translate(' + (dx * p.r * 0.6) + 'px,' + (dy * p.r * 0.6) + 'px)';
                if (len < 0.12) return;
                var d = p.stick._ids;
                if (dx > 0) next[d[0]] = Math.max(next[d[0]] || 0, dx); else next[d[1]] = Math.max(next[d[1]] || 0, -dx);
                if (dy > 0) next[d[2]] = Math.max(next[d[2]] || 0, dy); else next[d[3]] = Math.max(next[d[3]] || 0, -dy);
                return;
            }
            // Buttons and the D-pad follow the finger, so it can slide from one to another.
            var hit = document.elementFromPoint(p.x, p.y);
            var ctl = hit && hit.closest && hit.closest('#je-touch [data-ctl]');
            if (!ctl) return;
            var kind = ctl.getAttribute('data-ctl');
            if (kind === 'dpad') {
                var r = ctl.getBoundingClientRect();
                var x = (p.x - r.left) / r.width - 0.5, y = (p.y - r.top) / r.height - 0.5;
                if (y < -0.15) next[4] = dpadDown[4] = 1;
                if (y > 0.15) next[5] = dpadDown[5] = 1;
                if (x < -0.15) next[6] = dpadDown[6] = 1;
                if (x > 0.15) next[7] = dpadDown[7] = 1;
            } else if (kind === 'btn') {
                next[ctl.getAttribute('data-ids')] = 1;
            }
        });
        pad.querySelectorAll('[data-ids]').forEach(function (b) { b.classList.toggle('je-down', !!next[b.getAttribute('data-ids')]); });
        pad.querySelectorAll('.je-dpad b').forEach(function (b) { b.classList.toggle('je-down', !!dpadDown[b.getAttribute('data-d')]); });
        setTouchIds(next);
    }

    function releaseTouches() {
        pointers = {};
        sticks.forEach(function (s) { s.firstChild.style.transform = ''; });
        if (pad) pad.querySelectorAll('.je-down').forEach(function (b) { b.classList.remove('je-down'); });
        setTouchIds({});
    }

    function setTouchIds(next) {
        var changed = false, newPress = false;
        var k;
        for (k in next) { if (next[k] !== touchIds[k]) changed = true; if (!touchIds[k] && next[k] >= 1) newPress = true; }
        for (k in touchIds) if (!(k in next)) changed = true;
        if (!changed) return;
        touchIds = next;
        if (newPress && navigator.vibrate) try { navigator.vibrate(12); } catch (e) { /* ignore */ }
        post('touch', { ids: next });
    }
})();

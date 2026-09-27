/**
 * JellyEmu stream layer (inside the game-streaming bridge's page).
 *
 * Loaded by one line added to the moonlight-web-stream bridge's stream.html:
 *   <script src="https://<jellyfin>/jellyemu/assets/streamlayer.js"></script>
 * so its behaviour ships with JellyEmu instead of living in edits to the bridge.
 *
 * When the stream is embedded in JellyEmu's stream page (the "host", on the Jellyfin origin):
 *  - The bridge sees exactly one controller: JellyEmu's. Each frame it is built from the real
 *    controller, the keyboard and the host's on-screen controls, all through the player's
 *    JellyEmu bindings for this system (RetroPad ids), and sent as a standard (Xbox-layout)
 *    controller. So remaps apply to streams, and keyboards/remotes work for every system.
 *  - Raw keyboard, mouse and touch never reach the gaming PC (no clicking around its desktop).
 *  - The menu hotkey (Escape / the controller combo by default) asks the host to open its menu.
 *  - A start screen takes the first tap or press, which browsers require before playing sound.
 * Opened directly (not embedded), the bridge page is left untouched.
 */
(function () {
    'use strict';
    if (window.__jeStreamLayer || window.self === window.top) return;
    window.__jeStreamLayer = true;

    var script = document.currentScript;
    var HOST_ORIGIN = script && script.src ? new URL(script.src).origin : null;
    if (!HOST_ORIGIN) return;

    function send(type, data) {
        var msg = { je: true, type: type };
        if (data) for (var k in data) msg[k] = data[k];
        window.parent.postMessage(msg, HOST_ORIGIN);
    }

    // ---- Bridge page tweaks -------------------------------------------------------------------
    var style = document.createElement('style');
    style.textContent =
        '#sidebar-root { display: none !important; }' +
        '#je-start { position: fixed; inset: 0; z-index: 2147483647; display: flex; align-items: center; justify-content: center;' +
        '  background: rgba(0,0,0,.55); color: #fff; font: 600 clamp(18px, 3.2vmin, 30px) system-ui, sans-serif; text-align: center;' +
        '  padding: 24px; cursor: pointer; }';
    (document.head || document.documentElement).appendChild(style);

    // ---- Input sources ------------------------------------------------------------------------
    var realGetGamepads = navigator.getGamepads ? navigator.getGamepads.bind(navigator) : function () { return []; };
    var BUTTON_LABELS = ['BUTTON_1', 'BUTTON_2', 'BUTTON_3', 'BUTTON_4', 'LEFT_TOP_SHOULDER', 'RIGHT_TOP_SHOULDER',
        'LEFT_BOTTOM_SHOULDER', 'RIGHT_BOTTOM_SHOULDER', 'SELECT', 'START', 'LEFT_STICK', 'RIGHT_STICK',
        'DPAD_UP', 'DPAD_DOWN', 'DPAD_LEFT', 'DPAD_RIGHT', 'HOME'];
    var AXIS_LABELS = { LEFT_STICK_X: 0, LEFT_STICK_Y: 1, RIGHT_STICK_X: 2, RIGHT_STICK_Y: 3 };
    var DEADZONE = 0.15;

    // RetroPad id -> standard gamepad button (by position: B bottom, A right, Y left, X top).
    var ID_TO_BUTTON = { 0: 0, 8: 1, 1: 2, 9: 3, 10: 4, 11: 5, 12: 6, 13: 7, 2: 8, 3: 9, 14: 10, 15: 11, 4: 12, 5: 13, 6: 14, 7: 15 };
    var MENU_ID = 31, EXIT_ID = 30;

    var bindings = null;          // { id: { kb1, kb2, gp1, gp2 } } from the host
    var keysDown = {};            // keyCode -> true
    var touch = {};               // id -> value, from the host's on-screen controls
    var menuOpen = false;
    var holdUntilReleased = false; // after the menu closes, until the press that closed it ends
    var started = false;
    var hotkeyWasDown = {};
    var lastRealPress = false;

    function firstRealPad() {
        var pads = [];
        try { pads = realGetGamepads() || []; } catch (e) { /* ignore */ }
        for (var i = 0; i < pads.length; i++) {
            if (pads[i] && pads[i].connected !== false && pads[i].mapping === 'standard') return pads[i];
        }
        return null;
    }

    // Value (0..1) of one binding label on a real controller: a button, an axis direction
    // ("LEFT_STICK_X:+1"), or a combo of them ("A+B", all must be held).
    function labelValue(pad, label) {
        if (!pad || !label || typeof label !== 'string') return 0;
        var parts = label.split(/(?<!:)\+/);
        if (parts.length > 1) {
            var min = 1;
            for (var i = 0; i < parts.length; i++) min = Math.min(min, labelValue(pad, parts[i]) > 0.5 ? 1 : 0);
            return min;
        }
        var axis = label.match(/^([A-Z_]+):([+-])1$/);
        if (axis) {
            var a = pad.axes[AXIS_LABELS[axis[1]]] || 0;
            var v = axis[2] === '+' ? a : -a;
            return v > DEADZONE ? Math.min(1, v) : 0;
        }
        var index = BUTTON_LABELS.indexOf(label === 'GUIDE' ? 'HOME' : label);
        var b = index >= 0 ? pad.buttons[index] : null;
        return b ? (b.pressed ? Math.max(b.value || 1, 0.51) : (b.value || 0)) : 0;
    }

    // ---- JellyEmu's controller, as the bridge sees it ------------------------------------------
    var rumble = {
        effects: ['dual-rumble'],
        type: 'dual-rumble',
        playEffect: function (type, params) {
            var real = firstRealPad();
            if (real && real.vibrationActuator && real.vibrationActuator.playEffect) return real.vibrationActuator.playEffect(type, params);
            return Promise.resolve('complete');
        },
        reset: function () { return Promise.resolve('complete'); }
    };

    function makePad(buttons, axes) {
        return {
            id: 'JellyEmu Controller (STANDARD GAMEPAD)',
            index: 0,
            connected: true,
            mapping: 'standard',
            timestamp: performance.now(),
            buttons: buttons.map(function (v) { return { pressed: v > 0.5, touched: v > 0, value: v }; }),
            axes: axes,
            vibrationActuator: rumble
        };
    }

    // The bridge scans getGamepads() when it starts (this script runs before it), so it finds
    // JellyEmu's controller straight away. Real controllers connecting or disconnecting must not
    // add a second controller on the gaming PC, or remove this one.
    var pad = makePad(new Array(17).fill(0), [0, 0, 0, 0]);
    var lastUpdate = 0;
    function getGamepads() {
        if (performance.now() - lastUpdate > 4) {
            // Never let a problem here break the bridge's own controller loop.
            try { update(); } catch (e) { lastUpdate = performance.now(); console.error('[JellyEmu] stream layer', e); }
        }
        return [pad];
    }
    try {
        Object.defineProperty(navigator, 'getGamepads', { configurable: true, value: getGamepads });
    } catch (e) {
        navigator.getGamepads = getGamepads;
    }
    ['gamepadconnected', 'gamepaddisconnected'].forEach(function (type) {
        window.addEventListener(type, function (e) { e.stopImmediatePropagation(); }, true);
    });

    // Rebuilt whenever the bridge reads the controller, and every frame for the hotkeys.
    function update() {
        lastUpdate = performance.now();
        var real = firstRealPad();
        var v = {};
        if (bindings) {
            for (var key in bindings) {
                var id = +key, b = bindings[key];
                if (!b) continue;
                var val = Math.max(labelValue(real, b.gp1), labelValue(real, b.gp2));
                if ((b.kb1 && keysDown[b.kb1]) || (b.kb2 && keysDown[b.kb2])) val = 1;
                v[id] = val;
            }
        } else if (keysDown[27]) {
            v[MENU_ID] = 1; // before the bindings arrive, Escape still opens the menu
        }
        // On-screen controls press the button itself, whatever it's bound to.
        for (var t in touch) v[t] = Math.max(v[t] || 0, +touch[t] || 0);

        // Hotkeys fire once per press.
        [[MENU_ID, 'menu-toggle'], [EXIT_ID, 'exit']].forEach(function (h) {
            var down = (v[h[0]] || 0) > 0.5;
            if (down && !hotkeyWasDown[h[0]]) send(h[1]);
            hotkeyWasDown[h[0]] = down;
        });

        // Tell the host when a real controller is used (it hides on-screen controls in "auto").
        var realPress = !!(real && real.buttons.some(function (b) { return b && b.pressed; }));
        if (realPress && !lastRealPress) { send('input', { kind: 'controller' }); begin(); unlockMedia(); }
        lastRealPress = realPress;

        if (holdUntilReleased) {
            var anyHeld = Object.keys(keysDown).length > 0 || Object.keys(touch).length > 0;
            for (var hid in v) if (v[hid] > 0.5) anyHeld = true;
            if (!anyHeld) holdUntilReleased = false;
        }

        var buttons = new Array(17).fill(0);
        var axes = [0, 0, 0, 0];
        if (!menuOpen && !holdUntilReleased) {
            for (var rid in ID_TO_BUTTON) buttons[ID_TO_BUTTON[rid]] = v[rid] || 0;
            axes = [
                (v[16] || 0) - (v[17] || 0), (v[18] || 0) - (v[19] || 0),
                (v[20] || 0) - (v[21] || 0), (v[22] || 0) - (v[23] || 0)
            ];
        }
        pad = makePad(buttons, axes);
    }
    (function tick() {
        requestAnimationFrame(tick);
        getGamepads();
    })();

    // ---- Keyboard, mouse and touch: consumed here, never sent raw to the gaming PC ----------
    function onKey(e) {
        e.preventDefault();
        e.stopImmediatePropagation();
        // Some hosts (the Xbox app) also send key events for controller buttons, with key
        // "Unidentified". The controller itself is read above, so they don't count as keys, but
        // they are the user's action, which the bridge needs before it plays video and sound.
        if (e.key === 'Unidentified') {
            if (e.type === 'keydown') { begin(); unlockMedia(); }
            return;
        }
        if (e.type === 'keydown') {
            if (!e.repeat) { send('input', { kind: 'keyboard' }); begin(); unlockMedia(); }
            keysDown[e.keyCode] = true;
        } else {
            delete keysDown[e.keyCode];
        }
    }
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('keyup', onKey, true);
    window.addEventListener('blur', function () { keysDown = {}; });

    // The bridge's own dialogs (errors, "stream ended") still take taps and clicks.
    function inBridgeDialog(target) {
        var overlay = document.getElementById('modal-overlay');
        return !!(overlay && !overlay.classList.contains('modal-disabled') && target && overlay.contains(target));
    }

    ['pointerdown', 'pointerup', 'pointermove', 'mousedown', 'mouseup', 'mousemove', 'click', 'dblclick',
        'contextmenu', 'wheel', 'touchstart', 'touchmove', 'touchend'].forEach(function (type) {
        window.addEventListener(type, function (e) {
            if (inBridgeDialog(e.target)) return;
            if (type === 'touchstart') send('input', { kind: 'touch' });
            if (type === 'pointerdown' || type === 'touchstart' || type === 'mousedown') { begin(); unlockMedia(); }
            e.stopImmediatePropagation();
            if (type !== 'pointermove' && type !== 'mousemove' && e.cancelable) e.preventDefault();
        }, { capture: true, passive: false });
    });

    // The bridge unmutes its audio (and starts its video) on the user's first input, but its input
    // listeners never see anything now. A paste event with nothing to paste runs that same step
    // without sending anything to the gaming PC. Dispatched inside the tap or keypress, so the
    // browser counts it as the user's action.
    function unlockMedia() {
        try { document.dispatchEvent(new Event('paste')); } catch (e) { /* ignore */ }
    }

    // ---- Start screen -------------------------------------------------------------------------
    var startScreen = null;
    function showStart() {
        if (started || startScreen || !document.body) return;
        startScreen = document.createElement('div');
        startScreen.id = 'je-start';
        startScreen.textContent = 'Tap or press any button to start';
        document.body.appendChild(startScreen);
    }
    function begin() {
        if (started) return;
        started = true;
        if (startScreen) startScreen.remove();
        send('started');
    }
    if (document.body) showStart(); else document.addEventListener('DOMContentLoaded', showStart);

    // ---- Messages from the host ---------------------------------------------------------------
    window.addEventListener('message', function (e) {
        if (e.origin !== HOST_ORIGIN || !e.data || e.data.je !== true) return;
        if (e.data.type === 'config') bindings = e.data.bindings || null;
        else if (e.data.type === 'touch') { touch = e.data.ids || {}; begin(); }
        else if (e.data.type === 'menu') {
            if (menuOpen && !e.data.open) holdUntilReleased = true;
            menuOpen = !!e.data.open;
            keysDown = {};
        }
    });

    // Keep saying hello until the host has sent the bindings (it may attach its listener later).
    (function hello() {
        if (bindings) return;
        send('ready');
        setTimeout(hello, 500);
    })();
})();

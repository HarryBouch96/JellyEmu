/**
 * JellyEmu Controller Menu
 *
 * Opens the top bar and dock with a controller via the Open Menu hotkey
 * (default LT+RT+L3+R3), pauses the game and lets the D-pad / left stick move
 * between buttons, A select and B go back. Made for TV clients such as the
 * Jellyfin Xbox app, which have no mouse to reveal the toolbar.
 *
 * Game input is held back while the menu is open (see _jeSimulate in
 * ejs.input.js), and until every button is released after it closes, so the
 * B or combo press that closed it never reaches the game.
 */
(function () {
    'use strict';

    var FOCUSABLE = 'button, input, select, textarea, a[href], .je-tab, [tabindex]:not([tabindex="-1"])';
    var TEXT_INPUT = /^(text|search|email|url|password|number|)$/i;
    var REPEAT_DELAY_MS = 400;
    var REPEAT_RATE_MS = 130;
    var STICK_THRESHOLD = 0.5;

    var open = false;
    var pausedByMenu = false;
    var current = null;
    var returnTo = null;    // dock button that opened the current popup
    var hint = null;
    var prevA = false;
    var prevB = false;
    var dirHeld = null;
    var dirNextAt = 0;

    window._jeMenuOpen = false;
    window._jeMenuHold = false;

    function emu() { return window.EJS_emulator; }
    function activePopup() { return document.querySelector('.je-overlay.je-open'); }

    function isVisible(el) {
        if (!el || el.disabled || el.type === 'hidden') return false;
        var r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) return false;
        return window.getComputedStyle(el).visibility !== 'hidden';
    }

    // Everything that can be selected right now: the open popup, or else the bars.
    function candidates() {
        var pop = activePopup();
        var roots = pop ? [pop] : [document.getElementById('je-topbar'), document.getElementById('je-dock')];
        var list = [];
        roots.forEach(function (root) {
            if (!root) return;
            root.querySelectorAll(FOCUSABLE).forEach(function (el) {
                if (isVisible(el)) list.push(el);
            });
        });
        return list;
    }

    // Highlight only; no DOM focus, so the Xbox's own A-button handling can't
    // press the element a second time. Text fields get real focus on A.
    function setFocus(el) {
        if (current) current.classList.remove('je-gp-focus');
        current = el || null;
        if (!current) return;
        current.classList.add('je-gp-focus');
        try { current.scrollIntoView({ block: 'nearest', inline: 'nearest' }); } catch (e) {}
    }

    function center(r) { return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }

    function nearestTo(point, list) {
        var best = null, bestD = Infinity;
        list.forEach(function (el) {
            var c = center(el.getBoundingClientRect());
            var d = Math.abs(c.x - point.x) + Math.abs(c.y - point.y);
            if (d < bestD) { bestD = d; best = el; }
        });
        return best;
    }

    function move(dir) {
        var list = candidates();
        if (!list.length) return;
        if (!current || list.indexOf(current) === -1) { setFocus(list[0]); return; }
        var from = center(current.getBoundingClientRect());
        var best = null, bestScore = Infinity;
        list.forEach(function (el) {
            if (el === current) return;
            var c = center(el.getBoundingClientRect());
            var dx = c.x - from.x, dy = c.y - from.y;
            var main, cross;
            if (dir === 'left') { main = -dx; cross = Math.abs(dy); }
            else if (dir === 'right') { main = dx; cross = Math.abs(dy); }
            else if (dir === 'up') { main = -dy; cross = Math.abs(dx); }
            else { main = dy; cross = Math.abs(dx); }
            if (main <= 1) return;
            var score = main + cross * 2;
            if (score < bestScore) { bestScore = score; best = el; }
        });
        if (best) setFocus(best);
    }

    // Left/right changes sliders and dropdowns instead of moving.
    function adjust(el, delta) {
        if (el.tagName === 'SELECT') {
            var n = el.options.length;
            if (!n) return false;
            var next = Math.max(0, Math.min(n - 1, el.selectedIndex + delta));
            if (next === el.selectedIndex) return true;
            el.selectedIndex = next;
        } else if (el.tagName === 'INPUT' && el.type === 'range') {
            var min = parseFloat(el.min || '0'), max = parseFloat(el.max || '100');
            var step = parseFloat(el.step) || (max - min) / 20;
            el.value = Math.max(min, Math.min(max, parseFloat(el.value) + delta * step));
            el.dispatchEvent(new Event('input', { bubbles: true }));
        } else {
            return false;
        }
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
    }

    function direction(dir) {
        if ((dir === 'left' || dir === 'right') && current && adjust(current, dir === 'left' ? -1 : 1)) return;
        move(dir);
    }

    function activate() {
        var el = current;
        if (!el) { move('down'); return; }
        if (el.tagName === 'SELECT') {
            if (el.options.length) {
                el.selectedIndex = (el.selectedIndex + 1) % el.options.length;
                el.dispatchEvent(new Event('change', { bubbles: true }));
            }
            return;
        }
        if (el.tagName === 'INPUT' && el.type === 'range') return;
        if (el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && TEXT_INPUT.test(el.type))) {
            el.focus(); // brings up the on-screen keyboard
            return;
        }

        var popBefore = activePopup();
        var where = center(el.getBoundingClientRect());
        el.click();

        // The click may open or close a popup, or swap the button (Pause/Play).
        setTimeout(function () {
            if (!open) return;
            var popAfter = activePopup();
            var list = candidates();
            if (popAfter !== popBefore) {
                if (popAfter && !popBefore) returnTo = el;
                setFocus(popAfter ? list[0] : (returnTo && isVisible(returnTo) ? returnTo : nearestTo(where, list)));
            } else if (list.indexOf(current) === -1) {
                setFocus(nearestTo(where, list));
            }
        }, 80);
    }

    function back() {
        if (activePopup()) {
            if (window._jeCloseAllPopups) window._jeCloseAllPopups();
            if (window._jeShowDocks) window._jeShowDocks();
            var list = candidates();
            setFocus(returnTo && list.indexOf(returnTo) !== -1 ? returnTo : list[0]);
            returnTo = null;
            return;
        }
        closeMenu();
    }

    function showHint(on) {
        if (on && !hint) {
            hint = document.createElement('div');
            hint.id = 'je-gp-hint';
            hint.textContent = 'D-pad: move  ·  A: select  ·  B: back  ·  LT+RT+L3+R3: close';
            document.body.appendChild(hint);
        }
        if (hint) hint.style.display = on ? '' : 'none';
    }

    function firstPad() {
        var pads = [];
        try { pads = navigator.getGamepads ? navigator.getGamepads() : []; } catch (e) {}
        for (var i = 0; i < pads.length; i++) {
            if (pads[i] && pads[i].connected !== false) return pads[i];
        }
        return null;
    }

    function isPressed(gp, i) {
        var b = gp.buttons[i];
        return !!(b && (b.pressed || b.value > 0.5));
    }

    function readDirection(gp) {
        var ax = gp.axes[0] || 0, ay = gp.axes[1] || 0;
        if (isPressed(gp, 12) || ay < -STICK_THRESHOLD) return 'up';
        if (isPressed(gp, 13) || ay > STICK_THRESHOLD) return 'down';
        if (isPressed(gp, 14) || ax < -STICK_THRESHOLD) return 'left';
        if (isPressed(gp, 15) || ax > STICK_THRESHOLD) return 'right';
        return null;
    }

    function openMenu() {
        var e = emu();
        var topbar = document.getElementById('je-topbar');
        if (!e || !e.started || !topbar || !topbar.classList.contains('je-active')) return;

        open = true;
        window._jeMenuOpen = true;
        if (window._jeReleaseAllInputs) window._jeReleaseAllInputs();

        if (!e.paused) {
            var btnPause = document.getElementById('je-btn-pause');
            if (btnPause) { btnPause.click(); pausedByMenu = true; }
        }
        if (window._jeExpandDock) window._jeExpandDock();
        if (window._jeShowDocks) window._jeShowDocks();
        document.body.classList.add('je-menu-open');
        showHint(true);

        // Buttons already held (the combo) must not count as new presses.
        var gp = firstPad();
        prevA = gp ? isPressed(gp, 0) : false;
        prevB = gp ? isPressed(gp, 1) : false;
        dirHeld = gp ? readDirection(gp) : null;
        dirNextAt = Date.now() + REPEAT_DELAY_MS;

        var list = candidates();
        var saves = document.getElementById('je-btn-saves');
        setFocus(list.indexOf(saves) !== -1 ? saves : list[0]);
    }

    function closeMenu() {
        if (activePopup() && window._jeCloseAllPopups) window._jeCloseAllPopups();
        open = false;
        window._jeMenuOpen = false;
        window._jeMenuHold = true; // cleared once every button is released
        returnTo = null;
        setFocus(null);
        document.body.classList.remove('je-menu-open');
        showHint(false);

        var e = emu();
        if (pausedByMenu && e && e.paused) {
            var btnPlay = document.getElementById('je-btn-play');
            if (btnPlay) btnPlay.click();
        }
        pausedByMenu = false;
        if (window._jeShowDocks) window._jeShowDocks(); // restarts auto-hide
        if (window._jeRefocusGame) window._jeRefocusGame();
    }

    window._jeToggleGamepadMenu = function () {
        if (open) closeMenu(); else openMenu();
    };

    function poll() {
        requestAnimationFrame(poll);
        if (!open && !window._jeMenuHold) return;

        var gp = firstPad();
        if (!gp) { window._jeMenuHold = false; return; }

        if (!open) {
            var anyDown = false;
            for (var i = 0; i < gp.buttons.length; i++) {
                if (isPressed(gp, i)) { anyDown = true; break; }
            }
            if (!anyDown) window._jeMenuHold = false;
            return;
        }

        var a = isPressed(gp, 0), b = isPressed(gp, 1);
        var dir = readDirection(gp);

        // Leave A/B alone while the Input Mapping screen waits for a button.
        if (window._jeGpListening && window._jeGpListening()) {
            prevA = a; prevB = b; dirHeld = dir;
            return;
        }

        if (a && !prevA) activate();
        if (b && !prevB) back();
        prevA = a;
        prevB = b;

        var now = Date.now();
        if (dir !== dirHeld) {
            dirHeld = dir;
            if (dir) { direction(dir); dirNextAt = now + REPEAT_DELAY_MS; }
        } else if (dir && now >= dirNextAt) {
            direction(dir);
            dirNextAt = now + REPEAT_RATE_MS;
        }
    }
    requestAnimationFrame(poll);
})();

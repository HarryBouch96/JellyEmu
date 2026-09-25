/**
 * JellyEmu Save States + SRAM Cloud Backup Manager
 *
 * Handles the unified Saves & States popup (Cloud & Local) with responsive tabs.
 *
 * Depends on:
 *   - window.JellyEmuConfig      { itemId, userId, token }
 *   - window._jeEnsureBinary     exposed by the main template IIFE
 *   - window._jeOpenPopup        exposed by the main template IIFE
 *   - window._jeClosePopup       exposed by the main template IIFE
 *   - window.EJS_emulator / gameManager
 */
(function () {
    'use strict';

    var cfg    = window.JellyEmuConfig || {};
    var itemId = cfg.itemId || '';
    var userId = cfg.userId || '';
    var token  = cfg.token || '';
    var isM3u  = !!cfg.isM3u;

    function gm() {
        var e = window.EJS_emulator;
        return e ? e.gameManager : null;
    }

    function ensureBinary(data) {
        return window._jeEnsureBinary ? window._jeEnsureBinary(data) : null;
    }

    function uploadScreenshot(slot, afterPromise) {
        if (window._jeUploadScreenshot) window._jeUploadScreenshot(slot, afterPromise);
    }

    // Helper for authenticated requests
    function jeFetch(url, options) {
        options = options || {};
        if (token) {
            options.headers = options.headers || {};
            options.headers['Authorization'] = 'MediaBrowser Token="' + token + '"';
        }
        if (window.JellyEmu && typeof window.JellyEmu.getUrl === 'function') {
            return fetch(window.JellyEmu.getUrl(url), options);
        }
        return fetch(url, options);
    }

    // Tab Management
    var tabBtnStates = document.getElementById('je-tab-btn-states');
    var tabBtnSram   = document.getElementById('je-tab-btn-sram');
    var panelStates  = document.getElementById('je-panel-states');
    var panelSram    = document.getElementById('je-panel-sram');

    var statesBuilt = false;
    var sramBuilt   = false;

    function setActiveTab(tab) {
        if (tab === 'states') {
            tabBtnStates.classList.add('je-tab-active');
            tabBtnSram.classList.remove('je-tab-active');
            panelStates.style.display = 'flex';
            panelSram.style.display = 'none';
            if (!statesBuilt) {
                statesBuilt = true;
                buildSaveSlots();
            }
        } else {
            tabBtnSram.classList.add('je-tab-active');
            tabBtnStates.classList.remove('je-tab-active');
            panelSram.style.display = 'flex';
            panelStates.style.display = 'none';
            if (!sramBuilt) {
                sramBuilt = true;
                buildSramSlots();
            }
        }
    }

    if (tabBtnStates && tabBtnSram) {
        tabBtnStates.addEventListener('click', function () { setActiveTab('states'); });
        tabBtnSram.addEventListener('click', function () { setActiveTab('sram'); });
    }

    // Screenshots
    function loadSlotScreenshot(s, thumbEl) {
        jeFetch('/jellyemu/save-screenshot/' + itemId + '/' + userId + '/' + s)
            .then(function (r) {
                if (r.ok) return r.json();
                throw new Error();
            })
            .then(function (data) {
                if (data && data.dataUrl) {
                    thumbEl.innerHTML = '<img src="' + data.dataUrl + '" style="width:100%;height:100%;object-fit:cover">';
                    thumbEl.style.opacity = '1';
                } else {
                    showPlaceholder();
                }
            })
            .catch(function () {
                showPlaceholder();
            });

        function showPlaceholder() {
            thumbEl.innerHTML = '<svg viewBox="0 0 24 24" style="width:20px;height:20px;fill:rgba(255,255,255,.3)"><path d="M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z"/></svg>';
            thumbEl.style.opacity = '0.5';
        }
    }

    function loadSramPlaceholder(thumbEl) {
        thumbEl.innerHTML = '<svg viewBox="0 0 24 24" style="width:20px;height:20px;fill:rgba(255,255,255,.3)"><path d="M17 3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z"/></svg>';
        thumbEl.style.opacity = '0.5';
    }

    // Save States (Cloud)
    function buildSaveSlots() {
        var body = document.getElementById('je-saves-body');
        if (!body) return;
        body.innerHTML = '';

        for (var i = 1; i <= 5; i++) {
            var slot = document.createElement('div');
            slot.className = 'je-slot';
            slot.innerHTML =
                '<div class="je-slot-num">' + i + '</div>' +
                '<div class="je-slot-thumb" id="je-state-thumb-' + i + '"></div>' +
                '<div class="je-slot-info"><div>Slot ' + i + '</div>' +
                '<small id="je-slot-status-' + i + '">Checking…</small></div>' +
                '<div class="je-slot-actions">' +
                '<button class="je-btn" data-save="' + i + '">Save</button>' +
                '<button class="je-btn je-btn-primary" data-load="' + i + '">Load</button>' +
                '</div>';
            body.appendChild(slot);

            var thumbEl = document.getElementById('je-state-thumb-' + i);
            loadSlotScreenshot(i, thumbEl);

            (function (s) {
                jeFetch('/jellyemu/save/' + itemId + '/' + userId + '?slot=' + s, { method: 'HEAD' })
                    .then(function (r) {
                        var el = document.getElementById('je-slot-status-' + s);
                        if (el) el.textContent = r.ok ? 'Has save data' : 'Empty';
                    })
                    .catch(function () {
                        var el = document.getElementById('je-slot-status-' + s);
                        if (el) el.textContent = 'Empty';
                    });
            })(i);
        }

        // Save buttons
        body.querySelectorAll('[data-save]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var s = parseInt(btn.getAttribute('data-save'));
                var g = gm(); if (!g) return;

                Promise.resolve(g.getState()).then(function (rawState) {
                    var state = ensureBinary(rawState);
                    if (!state) return;

                    var headers = { 'Content-Type': 'application/octet-stream' };
                    jeFetch('/jellyemu/save/' + itemId + '/' + userId + '?slot=' + s, {
                        method: 'POST',
                        headers: headers,
                        body: state
                    }).then(function (r) {
                        if (!r.ok) throw new Error('Save rejected');
                        var el = document.getElementById('je-slot-status-' + s);
                        if (el) el.textContent = 'Saved!';
                        var thumbEl = document.getElementById('je-state-thumb-' + s);
                        uploadScreenshot(s, new Promise(function(resolve) {
                            setTimeout(function() {
                                if (thumbEl) loadSlotScreenshot(s, thumbEl);
                                resolve();
                            }, 500);
                        }));
                    }).catch(function (err) {
                        console.error('[JellyEmu] Save failed:', err);
                        var el = document.getElementById('je-slot-status-' + s);
                        if (el) el.textContent = 'Save Failed';
                    });
                });
            });
        });

        // Load buttons
        body.querySelectorAll('[data-load]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var s = parseInt(btn.getAttribute('data-load'));

                jeFetch('/jellyemu/save/' + itemId + '/' + userId + '?slot=' + s)
                    .then(function (r) {
                        if (!r.ok) throw new Error('No save');
                        return r.arrayBuffer();
                    })
                    .then(function (buf) {
                        var g = gm(); if (!g) return;
                        window._jeClosePopup('je-pop-saves');
                        setTimeout(function () {
                            g.loadState(new Uint8Array(buf));
                        }, 100);
                    })
                    .catch(function () {
                        var el = document.getElementById('je-slot-status-' + s);
                        if (el) el.textContent = 'No save to load';
                    });
            });
        });
    }

    // SRAM (Cloud Backups)
    function buildSramSlots() {
        var body = document.getElementById('je-sram-body');
        if (!body) return;
        body.innerHTML = '';

        for (var i = 1; i <= 5; i++) {
            var slot = document.createElement('div');
            slot.className = 'je-slot';
            slot.innerHTML =
                '<div class="je-slot-num">' + i + '</div>' +
                '<div class="je-slot-thumb" id="je-sram-thumb-' + i + '"></div>' +
                '<div class="je-slot-info"><div>Slot ' + i + '</div>' +
                '<small id="je-sram-status-' + i + '">Checking…</small></div>' +
                '<div class="je-slot-actions">' +
                '<button class="je-btn" data-save-sram="' + i + '">Backup</button>' +
                '<button class="je-btn je-btn-primary" data-load-sram="' + i + '">Restore</button>' +
                '</div>';
            body.appendChild(slot);

            var thumbEl = document.getElementById('je-sram-thumb-' + i);
            loadSramPlaceholder(thumbEl);

            (function (s) {
                jeFetch('/jellyemu/sram/' + itemId + '/' + userId + '?slot=' + s, { method: 'HEAD' })
                    .then(function (r) {
                        var el = document.getElementById('je-sram-status-' + s);
                        if (el) el.textContent = r.ok ? 'Has backup' : 'Empty';
                    })
                    .catch(function () {
                        var el = document.getElementById('je-sram-status-' + s);
                        if (el) el.textContent = 'Empty';
                    });
            })(i);
        }

        // Backup buttons
        body.querySelectorAll('[data-save-sram]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var s = parseInt(btn.getAttribute('data-save-sram'));
                var g = gm(); if (!g) return;

                var rawSave = g.getSaveFile();
                if (!rawSave) return alert('No in-game SRAM data available. Make sure you saved in-game first!');

                var saveBlob = ensureBinary(rawSave);
                var headers = { 'Content-Type': 'application/octet-stream' };

                jeFetch('/jellyemu/sram/' + itemId + '/' + userId + '?slot=' + s, {
                    method: 'POST',
                    headers: headers,
                    body: saveBlob
                }).then(function (r) {
                    if (!r.ok) throw new Error('SRAM backup rejected');
                    var el = document.getElementById('je-sram-status-' + s);
                    if (el) el.textContent = 'Backed up!';
                }).catch(function (err) {
                    console.error('[JellyEmu] SRAM backup failed:', err);
                    var el = document.getElementById('je-sram-status-' + s);
                    if (el) el.textContent = 'Backup Failed';
                });
            });
        });

        // Restore buttons
        body.querySelectorAll('[data-load-sram]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var s = parseInt(btn.getAttribute('data-load-sram'));
                var g = gm(); if (!g) return;

                if (!confirm('Restoring this SRAM backup will restart the game and overwrite any unsaved progress. Continue?')) return;

                jeFetch('/jellyemu/sram/' + itemId + '/' + userId + '?slot=' + s)
                    .then(function (r) {
                        if (!r.ok) throw new Error('No SRAM backup');
                        return r.arrayBuffer();
                    })
                    .then(function (buf) {
                        var sramPath = g.getSaveFilePath();
                        if (!sramPath) return alert('SRAM not supported by this core.');
                        var uint8 = new Uint8Array(buf);

                        try { g.FS.unlink(sramPath); } catch (err) {}
                        g.FS.writeFile(sramPath, uint8);
                        g.loadSaveFiles();
                        
                        window._jeClosePopup('je-pop-saves');
                        alert('SRAM backup restored! Restarting...');
                        g.restart();
                    })
                    .catch(function () {
                        var el = document.getElementById('je-sram-status-' + s);
                        if (el) el.textContent = 'No backup to restore';
                    });
            });
        });
    }

    // Dock Save States button triggers our unified modal
    document.getElementById('je-btn-saves').addEventListener('click', function () {
        statesBuilt = false;
        sramBuilt = false;
        setActiveTab('states');
        window._jeOpenPopup('je-pop-saves');
    });

    // Local Import / Export

    // Export Save State (.state)
    document.getElementById('je-io-exp-state').addEventListener('click', function () {
        var g = gm(); if (!g) return;
        Promise.resolve(g.getState()).then(function (rawState) {
            var stateBlob = ensureBinary(rawState);
            if (!stateBlob || stateBlob.size === 0) return alert('No state data available.');

            var url = URL.createObjectURL(stateBlob);
            var a = document.createElement('a');
            a.href = url;
            a.download = (window.EJS_gameName || 'game').replace(/[^a-z0-9]/gi, '_') + '.state';
            a.click();
            URL.revokeObjectURL(url);
        });
    });

    // Export SRAM (.sav)
    document.getElementById('je-io-exp-sram').addEventListener('click', function () {
        var g = gm(); if (!g) return;

        var rawSave = g.getSaveFile();
        if (!rawSave) return alert('No in-game SRAM data available. Make sure you saved in-game first!');

        var saveBlob = ensureBinary(rawSave);
        var url = URL.createObjectURL(saveBlob);
        var a = document.createElement('a');
        a.href = url;
        a.download = (window.EJS_gameName || 'game').replace(/[^a-z0-9]/gi, '_') + '.sav';
        a.click();
        URL.revokeObjectURL(url);
    });

    // Wire up State drag/drop and file click
    setupDropzone('je-state-dropzone', 'je-state-file', false);
    // Wire up SRAM drag/drop and file click
    setupDropzone('je-sram-dropzone', 'je-sram-file', true);

    function setupDropzone(dropzoneId, fileInputId, isSram) {
        var dropzone = document.getElementById(dropzoneId);
        var fileInput = document.getElementById(fileInputId);
        if (!dropzone || !fileInput) return;

        dropzone.addEventListener('click', function () { fileInput.click(); });

        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(function (evt) {
            dropzone.addEventListener(evt, function (e) { e.preventDefault(); e.stopPropagation(); }, false);
        });

        ['dragenter', 'dragover'].forEach(function (evt) {
            dropzone.addEventListener(evt, function () {
                dropzone.style.borderColor = 'rgba(100,200,255,.8)';
                dropzone.style.background  = 'rgba(100,200,255,.1)';
            }, false);
        });

        ['dragleave', 'drop'].forEach(function (evt) {
            dropzone.addEventListener(evt, function () {
                dropzone.style.borderColor = 'rgba(255,255,255,.2)';
                dropzone.style.background  = 'transparent';
            }, false);
        });

        dropzone.addEventListener('drop', function (e) {
            if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
                handleImport(e.dataTransfer.files[0], isSram);
            }
        }, false);

        fileInput.addEventListener('change', function (e) {
            if (e.target.files && e.target.files.length > 0) {
                handleImport(e.target.files[0], isSram);
                e.target.value = '';
            }
        });
    }

    function handleImport(file, isSram) {
        var g = gm(); if (!g) return;

        var reader = new FileReader();
        reader.onload = function (e) {
            try {
                var uint8 = new Uint8Array(e.target.result);

                if (isSram) {
                    var sramPath = g.getSaveFilePath();
                    if (!sramPath) {
                        alert('Could not determine the SRAM path for this emulator core.');
                        return;
                    }
                    try { g.FS.unlink(sramPath); } catch (err) {}
                    g.FS.writeFile(sramPath, uint8);
                    g.loadSaveFiles();
                    window._jeClosePopup('je-pop-saves');
                    alert('SRAM imported successfully! Restarting game...');
                    g.restart();
                } else {
                    g.loadState(uint8);
                    window._jeClosePopup('je-pop-saves');
                }
            } catch (err) {
                console.error('[JellyEmu] Import error:', err);
                alert('Failed to import file. The data may be corrupt or incompatible with this emulator core.');
            }
        };
        reader.readAsArrayBuffer(file);
    }

    // Multi-Disc / Playlist (J3U/M3U) Swapping
    if (isM3u) {
        // Subscribe to jellyemu:gamestart to check and restore Slot 99 SRAM
        window.addEventListener('jellyemu:gamestart', function () {
            setTimeout(function () {
                jeFetch('/jellyemu/sram/' + itemId + '/' + userId + '?slot=99')
                    .then(function (r) {
                        if (r.ok) return r.arrayBuffer();
                        throw new Error('No Slot 99 backup');
                    })
                    .then(function (buf) {
                        var g = gm();
                        if (!g) return;
                        var sramPath = g.getSaveFilePath ? g.getSaveFilePath() : '';
                        if (!sramPath) return;
                        try { g.FS.unlink(sramPath); } catch (_) {}
                        g.FS.writeFile(sramPath, new Uint8Array(buf));
                        g.loadSaveFiles();
                        g.restart();
                        console.log('[JellyEmu] Restored Slot 99 SRAM for next disc.');

                        // Delete the slot 99 save from server
                        jeFetch('/jellyemu/sram/' + itemId + '/' + userId + '?slot=99', {
                            method: 'DELETE'
                        }).catch(function () {});
                    })
            }, 500);
        });

        // Wire up disc swap UI triggers
        var btnNext = document.getElementById('je-btn-nextdisc');
        var btnSel = document.getElementById('je-btn-selectdisc');
        
        if (btnNext) {
            btnNext.addEventListener('click', function () {
                triggerDiscSwap('next');
            });
        }
        
        if (btnSel) {
            btnSel.addEventListener('click', function () {
                var listEl = document.getElementById('je-disc-list');
                listEl.innerHTML = '<div style="opacity:.4;font-size:13px;text-align:center;padding:12px 0;">Loading discs…</div>';
                
                jeFetch('/jellyemu/playlist/' + itemId + '/discs/' + userId)
                    .then(function (r) { return r.json(); })
                    .then(function (data) {
                        listEl.innerHTML = '';
                        if (!data.discs || data.discs.length === 0) {
                            listEl.innerHTML = '<div style="opacity:.4;font-size:13px;text-align:center;padding:12px 0;">No discs found.</div>';
                            return;
                        }
                        data.discs.forEach(function (disc) {
                            var item = document.createElement('div');
                            item.className = 'je-disc-item' + (disc.index === data.activeDiscIndex ? ' je-active' : '');
                            item.textContent = disc.name + ' (' + disc.filename + ')';
                            
                            item.addEventListener('click', function () {
                                if (disc.index === data.activeDiscIndex) {
                                    window._jeClosePopup('je-pop-selectdisc');
                                    return;
                                }
                                triggerDiscSwap(disc.index);
                            });
                            listEl.appendChild(item);
                        });
                    })
                    .catch(function () {
                        listEl.innerHTML = '<div style="opacity:.4;font-size:13px;text-align:center;padding:12px 0;color:#f44">Failed to load discs.</div>';
                    });
                window._jeOpenPopup('je-pop-selectdisc');
            });
        }
    }

    function triggerDiscSwap(targetDisc) {
        var btnNext = document.getElementById('je-btn-nextdisc');
        var btnSel = document.getElementById('je-btn-selectdisc');
        if (btnNext) btnNext.disabled = true;
        if (btnSel) btnSel.disabled = true;
        
        var statusEl = document.getElementById('je-loader-status');
        if (statusEl) statusEl.textContent = 'Saving progress & swapping disc...';
        
        var loader = document.getElementById('je-loader');
        if (loader) {
            loader.style.display = 'flex';
            loader.classList.remove('je-dismiss');
        }
        
        // Get SRAM
        var g = gm();
        var rawSave = g ? g.getSaveFile() : null;
        var savePromise = Promise.resolve();
        
        if (rawSave) {
            var saveBlob = ensureBinary(rawSave);
            if (saveBlob) {
                var headers = { 'Content-Type': 'application/octet-stream' };
                // 2. Upload to slot 99
                savePromise = jeFetch('/jellyemu/sram/' + itemId + '/' + userId + '?slot=99', {
                    method: 'POST',
                    headers: headers,
                    body: saveBlob
                });
            }
        }
        
        // Swap index and reload
        savePromise.finally(function () {
            jeFetch('/jellyemu/playlist/' + itemId + '/swap/' + userId + '?disc=' + targetDisc, {
                method: 'POST'
            })
            .finally(function () {
                window.location.reload();
            });
        });
    }

    // Automatic SRAM (battery save) sync
    //
    // Keeps native in-game saves on the server so they survive between
    // sessions on clients with unreliable browser storage (e.g. Xbox).
    // Uses the existing /jellyemu/sram endpoints with a dedicated slot that
    // is never shown in the manual Backup/Restore UI (slots 1-5) and does
    // not collide with the multi-disc hand-off slot (99).
    //
    // Change detection uses EmulatorJS's documented EJS_onSaveUpdate +
    // EJS_fixedSaveInterval (EmulatorJS >= 4.3). Older builds (the "stable"
    // 4.2.x CDN channel) lack both, so we fall back to flushing and reading
    // the save file ourselves on the same interval.
    var AUTO_SRAM_SLOT = 100;
    var AUTO_SRAM_INTERVAL_MS = 7000;
    var AUTO_SRAM_RETRY_MS = 15000;
    var AUTO_SRAM_RESTORE_TIMEOUT_MS = 10000;

    if (itemId && userId && token) {
        var autoSramUrl = '/jellyemu/sram/' + itemId + '/' + userId + '?slot=' + AUTO_SRAM_SLOT;
        var autoSramReady = false;       // true once the launch-time restore check is finished
        var autoSramStarted = false;     // guards against handling game start twice
        var autoSramUploaded = null;     // hash of the SRAM the server is known to have
        var autoSramInFlight = null;     // hash currently being uploaded
        var autoSramPending = null;      // newest bytes waiting to be uploaded
        var autoSramRetryTimer = null;

        var toBytes = function (data) {
            if (!data) return null;
            if (data instanceof Uint8Array) return data;
            if (data instanceof ArrayBuffer) return new Uint8Array(data);
            if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
            return null;
        };

        // FNV-1a over the bytes plus the length; only used to detect changes.
        var hashBytes = function (bytes) {
            var h = 0x811c9dc5;
            for (var i = 0; i < bytes.length; i++) {
                h ^= bytes[i];
                h = Math.imul(h, 0x01000193);
            }
            return (h >>> 0).toString(16) + ':' + bytes.length;
        };

        var readLocalSram = function (flush) {
            try {
                var g = gm();
                if (!g || typeof g.getSaveFile !== 'function') return null;
                return toBytes(g.getSaveFile(flush));
            } catch (err) {
                return null;
            }
        };

        var autoSramChain = null;        // promise for the upload currently running

        var uploadAutoSram = function () {
            if (autoSramChain) return autoSramChain;
            if (!autoSramReady || !autoSramPending) return Promise.resolve();
            var bytes = autoSramPending;
            var hash = hashBytes(bytes);
            autoSramPending = null;
            if (hash === autoSramUploaded) return Promise.resolve();

            var failed = false;
            autoSramInFlight = hash;
            autoSramChain = jeFetch(autoSramUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/octet-stream' },
                body: bytes
            }).then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                autoSramUploaded = hash;
                console.log('[JellyEmu] Auto SRAM synced (' + bytes.length + ' bytes)');
            }).catch(function (err) {
                failed = true;
                console.warn('[JellyEmu] Auto SRAM sync failed, will retry:', err);
                if (!autoSramPending) autoSramPending = bytes;
                clearTimeout(autoSramRetryTimer);
                autoSramRetryTimer = setTimeout(uploadAutoSram, AUTO_SRAM_RETRY_MS);
            }).then(function () {
                autoSramInFlight = null;
                autoSramChain = null;
                // Newer data arrived while uploading; send it now (retries wait for the timer).
                if (!failed && autoSramPending) return uploadAutoSram();
            });
            return autoSramChain;
        };

        var queueAutoSram = function (data) {
            var bytes = toBytes(data);
            if (!bytes || bytes.length < 8) return Promise.resolve();
            var hash = hashBytes(bytes);
            if (hash === autoSramUploaded) return Promise.resolve();
            if (hash === autoSramInFlight) return autoSramChain || Promise.resolve();
            autoSramPending = bytes;
            return uploadAutoSram();
        };

        // Documented EmulatorJS hooks; must be defined before loader.js runs.
        window.EJS_onSaveUpdate = function (e) {
            if (!autoSramReady || !e) return;
            queueAutoSram(e.save);
        };
        if (typeof window.EJS_fixedSaveInterval === 'undefined') {
            window.EJS_fixedSaveInterval = AUTO_SRAM_INTERVAL_MS;
        }

        // useLocalBaseline: treat the SRAM currently in the emulator as already
        // on the server, so it is only uploaded once the game changes it. Used
        // when we could not confirm the server copy, to avoid overwriting a
        // newer server save with stale local data.
        var finishAutoSramRestore = function (useLocalBaseline) {
            if (autoSramReady) return;
            if (useLocalBaseline) {
                var local = readLocalSram(false);
                if (local) autoSramUploaded = hashBytes(local);
            }
            autoSramReady = true;

            var e = window.EJS_emulator;
            var hasSaveUpdate = e && typeof e.enableSaveUpdateEvent === 'function';
            if (!hasSaveUpdate) {
                // EmulatorJS < 4.3: no saveUpdate event or fixed interval.
                setInterval(function () {
                    var em = window.EJS_emulator;
                    if (!em || !em.started) return;
                    queueAutoSram(readLocalSram(true));
                }, AUTO_SRAM_INTERVAL_MS);
            }
        };

        // Launch-time restore: runs once per page load, never re-triggers itself.
        var restoreAutoSram = function () {
            if (autoSramStarted) return;
            autoSramStarted = true;

            // A save state is being loaded at launch; it carries its own SRAM
            // and a restart would discard it. Just start tracking changes.
            if (window.EJS_loadStateURL) {
                setTimeout(function () { finishAutoSramRestore(true); }, 3000);
                return;
            }

            var controller = (typeof AbortController === 'function') ? new AbortController() : null;
            var abortTimer = controller ? setTimeout(function () { controller.abort(); }, AUTO_SRAM_RESTORE_TIMEOUT_MS) : null;

            var skipForDiscSwap = isM3u
                ? jeFetch('/jellyemu/sram/' + itemId + '/' + userId + '?slot=99', { method: 'HEAD' })
                    .then(function (r) { return r.ok; }, function () { return false; })
                : Promise.resolve(false);

            var discSwap = false;
            skipForDiscSwap.then(function (skip) {
                // The multi-disc hand-off (slot 99) owns this launch; start
                // tracking once its own restore/restart has had time to run.
                if (skip) { discSwap = true; return null; }
                return jeFetch(autoSramUrl, controller ? { signal: controller.signal } : undefined)
                    .then(function (r) {
                        if (r.status === 404) return null;
                        if (!r.ok) throw new Error('HTTP ' + r.status);
                        return r.arrayBuffer();
                    });
            }).then(function (buf) {
                if (discSwap) {
                    setTimeout(function () { finishAutoSramRestore(false); }, 5000);
                    return;
                }
                // Nothing on the server yet: upload the current save on the first flush.
                if (!buf || buf.byteLength < 8) {
                    finishAutoSramRestore(false);
                    return;
                }
                var serverBytes = new Uint8Array(buf);
                var serverHash = hashBytes(serverBytes);
                autoSramUploaded = serverHash;

                var local = readLocalSram(false);
                if (local && hashBytes(local) === serverHash) {
                    finishAutoSramRestore(false);
                    return;
                }

                var g = gm();
                var sramPath = g && g.getSaveFilePath ? g.getSaveFilePath() : '';
                if (!sramPath) {
                    finishAutoSramRestore(true);
                    return;
                }
                try { g.FS.unlink(sramPath); } catch (_) {}
                g.FS.writeFile(sramPath, serverBytes);
                g.loadSaveFiles();
                g.restart();
                console.log('[JellyEmu] Restored automatic SRAM backup (' + serverBytes.length + ' bytes)');
                finishAutoSramRestore(false);
            }).catch(function (err) {
                console.warn('[JellyEmu] Automatic SRAM restore skipped:', err);
                finishAutoSramRestore(true);
            }).then(function () {
                if (abortTimer) clearTimeout(abortTimer);
            });
        };

        window.addEventListener('jellyemu:gamestart', function () {
            setTimeout(restoreAutoSram, 300);
        });

        // Final flush on exit / backgrounding. Resolves within a few seconds
        // even if the server is unreachable so it never blocks leaving the game.
        window._jeFlushAutoSram = function () {
            if (!autoSramReady) return Promise.resolve();
            var done = queueAutoSram(readLocalSram(true));
            var timeout = new Promise(function (resolve) { setTimeout(resolve, 4000); });
            return Promise.race([done, timeout]).catch(function () {});
        };
        document.addEventListener('visibilitychange', function () {
            if (document.visibilityState === 'hidden') window._jeFlushAutoSram();
        });
    }

})();
/**
 * JellyEmu settings: Gaming PCs tab (EXPERIMENT, game streaming).
 * Lists the gaming PCs games can be streamed from, creates setup commands for new ones, and edits
 * the stream address and library paths. Talks to JellyEmuStreamPcController (administrators only).
 */
(function () {
    'use strict';

    function api(method, path, body) {
        var init = {
            method: method,
            headers: { 'Authorization': 'MediaBrowser Token="' + ApiClient.accessToken() + '"' }
        };
        if (body !== undefined) {
            init.headers['Content-Type'] = 'application/json';
            init.body = JSON.stringify(body);
        }
        return fetch(ApiClient.getUrl(path.replace(/^\//, '')), init).then(function (r) {
            if (r.ok) return r.status === 204 ? null : r.json();
            return r.json().catch(function () { return {}; }).then(function (d) {
                throw new Error((d && d.message) || ('Request failed (HTTP ' + r.status + ')'));
            });
        });
    }

    function el(tag, props, children) {
        var e = document.createElement(tag);
        if (props) Object.keys(props).forEach(function (k) {
            if (k === 'style') e.style.cssText = props[k];
            else if (k === 'text') e.textContent = props[k];
            else e[k] = props[k];
        });
        (children || []).forEach(function (c) { if (c) e.appendChild(c); });
        return e;
    }

    function serverAddress() {
        var s = (typeof ApiClient !== 'undefined' && ApiClient.serverAddress && ApiClient.serverAddress()) || window.location.origin;
        return s.replace(/\/+$/, '');
    }

    var STATUS_COLOURS = { 'Ready': '#52B54B', 'Playing': '#00a4dc', 'Not ready': '#e0a030', 'Offline': '#777', 'Setting up': '#e0a030' };

    function ago(iso) {
        if (!iso) return 'never';
        var s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
        if (s < 90) return 'just now';
        if (s < 5400) return Math.round(s / 60) + ' minutes ago';
        if (s < 129600) return Math.round(s / 3600) + ' hours ago';
        return Math.round(s / 86400) + ' days ago';
    }

    window.jeInitGamingPcsTab = function (page) {
        var root = page.querySelector('#je-gpcs');
        if (!root) return;
        var $ = function (id) { return root.querySelector('#' + id); };
        var libraryFolders = [];
        var timer = null;

        function renderCaddy(origin) {
            var host = '';
            try { host = new URL(origin).host; } catch (e) { host = 'stream.example.com'; }
            $('je-gp-caddy').textContent = [
                host + ' {',
                '    route {',
                '        # Never trust these from a visitor; only JellyEmu\'s check sets them.',
                '        request_header -X-JellyEmu-Stream-User',
                '        request_header -X-JellyEmu-Upstream',
                '        # Swap a one-time pass from JellyEmu for a session cookie.',
                '        handle /jellyemu-auth {',
                '            rewrite * /jellyemu/stream/login?{query}',
                '            reverse_proxy 127.0.0.1:8096',
                '        }',
                '        # Everything else needs a valid session; JellyEmu says which gaming PC it goes to.',
                '        forward_auth 127.0.0.1:8096 {',
                '            uri /jellyemu/stream/check',
                '            copy_headers X-JellyEmu-Stream-User X-JellyEmu-Upstream',
                '            header_up -Connection',
                '            header_up -Upgrade',
                '            header_up -Sec-WebSocket-Key',
                '            header_up -Sec-WebSocket-Version',
                '            header_up -Sec-WebSocket-Extensions',
                '        }',
                '        reverse_proxy {http.request.header.X-JellyEmu-Upstream}',
                '    }',
                '}'
            ].join('\n');
        }

        function pathRow(p) {
            var server = el('input', { type: 'text', className: 'emby-input je-gp-server', value: p.server || '', placeholder: '/media/games/' });
            var pc = el('input', { type: 'text', className: 'emby-input je-gp-pcpath', value: p.pc || '', placeholder: '\\\\nas\\games\\' });
            var remove = el('button', { type: 'button', className: 'je-gp-btn je-gp-quiet', text: 'Remove' });
            var row = el('div', { className: 'je-gp-row' }, [server, el('span', { text: '→', style: 'color:#777' }), pc, remove]);
            remove.addEventListener('click', function () { row.remove(); });
            return row;
        }

        function renderPcs(pcs) {
            var list = $('je-gp-list');
            list.innerHTML = '';
            if (!pcs.length) {
                list.appendChild(el('p', { className: 'je-gp-desc', text: 'No gaming PCs yet. Add one below.' }));
                return;
            }
            pcs.forEach(function (pc) {
                var meta = [
                    pc.platforms && pc.platforms.length ? pc.platforms.join(', ') : 'No systems yet',
                    'Checked in ' + ago(pc.lastCheckIn) + (pc.version ? ' · setup v' + pc.version : '') + (pc.managed ? '' : ' · set up by hand')
                ];
                var dot = el('span', { className: 'je-gp-dot', style: 'background:' + (STATUS_COLOURS[pc.status] || '#777') });
                var info = el('div', null, [
                    el('div', { className: 'je-gp-name' }, [dot, el('span', { text: pc.name }), el('span', { text: pc.status, style: 'font-weight:400;color:#999;font-size:0.85em' })]),
                    el('div', { className: 'je-gp-meta', text: meta[0] }),
                    el('div', { className: 'je-gp-meta', text: meta[1], style: 'margin-top:0' })
                ]);
                var rename = el('button', { type: 'button', className: 'je-gp-btn je-gp-quiet', text: 'Rename' });
                var remove = el('button', { type: 'button', className: 'je-gp-btn je-gp-danger', text: 'Remove' });
                rename.addEventListener('click', function () {
                    var name = window.prompt('New name for ' + pc.name, pc.name);
                    if (!name || name === pc.name) return;
                    api('PATCH', '/jellyemu/stream/admin/pcs/' + encodeURIComponent(pc.id), { name: name }).then(load, function (e) { window.alert(e.message); });
                });
                remove.addEventListener('click', function () {
                    if (!window.confirm('Remove ' + pc.name + '? Players won\'t see it any more and its key stops working. To remove the software from the PC itself, run its setup with -Uninstall.')) return;
                    api('DELETE', '/jellyemu/stream/admin/pcs/' + encodeURIComponent(pc.id)).then(load, function (e) { window.alert(e.message); });
                });
                list.appendChild(el('div', { className: 'je-gp-pc' }, [info, el('div', { style: 'display:flex;gap:6px;flex-shrink:0' }, pc.managed ? [rename, remove] : [rename])]));
            });
        }

        function load() {
            if (!document.body.contains(root)) { clearInterval(timer); return; }
            return api('GET', '/jellyemu/stream/admin').then(function (d) {
                renderPcs(d.pcs || []);
                libraryFolders = d.libraryFolders || [];
                if (!root.dataset.loaded) {
                    root.dataset.loaded = '1';
                    $('je-gp-origin').value = d.streamOrigin || '';
                    renderCaddy(d.streamOrigin || '');
                    var paths = $('je-gp-paths');
                    paths.innerHTML = '';
                    (d.libraryPaths && d.libraryPaths.length ? d.libraryPaths : [{ server: libraryFolders[0] || '', pc: '' }])
                        .forEach(function (p) { paths.appendChild(pathRow(p)); });
                }
            }).catch(function (e) {
                $('je-gp-list').innerHTML = '';
                $('je-gp-list').appendChild(el('p', { className: 'je-gp-desc', text: 'Couldn\'t load the gaming PCs: ' + e.message }));
            });
        }

        $('je-gp-origin').addEventListener('input', function () { renderCaddy(this.value.trim()); });
        $('je-gp-addpath').addEventListener('click', function () { $('je-gp-paths').appendChild(pathRow({ server: '', pc: '' })); });
        $('je-gp-refresh').addEventListener('click', load);

        $('je-gp-save').addEventListener('click', function () {
            var paths = Array.prototype.map.call(root.querySelectorAll('#je-gp-paths .je-gp-row'), function (row) {
                return { server: row.querySelector('.je-gp-server').value.trim(), pc: row.querySelector('.je-gp-pcpath').value.trim() };
            });
            $('je-gp-saved').textContent = 'Saving…';
            api('PUT', '/jellyemu/stream/admin/settings', { streamOrigin: $('je-gp-origin').value.trim(), libraryPaths: paths })
                .then(function () { $('je-gp-saved').textContent = 'Saved'; }, function (e) { $('je-gp-saved').textContent = e.message; });
        });

        $('je-gp-create').addEventListener('click', function () {
            var name = $('je-gp-newname').value.trim();
            if (!name) { $('je-gp-newname').focus(); return; }
            api('POST', '/jellyemu/stream/admin/setup-code', { name: name }).then(function (d) {
                var server = serverAddress();
                $('je-gp-command').textContent =
                    'iwr -UseBasicParsing ' + server + '/jellyemu/stream/pc/setup.ps1 -OutFile "$env:TEMP\\jellyemu-setup.ps1"; ' +
                    'powershell -NoProfile -ExecutionPolicy Bypass -File "$env:TEMP\\jellyemu-setup.ps1" -Server ' + server + ' -Code ' + d.code;
                $('je-gp-setup').style.display = '';
                $('je-gp-copied').textContent = '';
            }, function (e) { window.alert(e.message); });
        });

        $('je-gp-copy').addEventListener('click', function () {
            var text = $('je-gp-command').textContent;
            (navigator.clipboard ? navigator.clipboard.writeText(text) : Promise.reject())
                .then(function () { $('je-gp-copied').textContent = 'Copied'; }, function () { $('je-gp-copied').textContent = 'Select the text and copy it'; });
        });

        load();
        timer = setInterval(load, 15000);
    };
})();

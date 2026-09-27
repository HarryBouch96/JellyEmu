using System.Net.Mime;
using System.Text.Encodings.Web;
using JellyEmu.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Entities;
using Scriban;

namespace JellyEmu.Controllers
{
    /// <summary>
    /// Serves the EmulatorJS HTML play page, ROM files, and core resolution.
    /// Routes: /jellyemu/play/*, /jellyemu/rom/*, /jellyemu/core/*
    /// </summary>
    public class JellyEmuPlayController : JellyEmuBaseController
    {
        private readonly JellyEmuSaveLinkService _saveLinks;

        public JellyEmuPlayController(
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            ILogger<JellyEmuPlayController> logger,
            JellyEmuEjsManager ejsManager,
            JellyEmuSessionService sessionService,
            IHttpClientFactory httpClientFactory,
            JellyEmuSaveLinkService saveLinks)
            : base(libraryManager, appPaths, logger, ejsManager, sessionService, httpClientFactory)
        {
            _saveLinks = saveLinks;
        }

        [HttpGet("/jellyemu/play/{itemId}")]
        public async Task<IActionResult> Play(string itemId, [FromQuery] string? userId, [FromQuery] int? slot, [FromQuery] string? core,
            [FromServices] IHttpClientFactory httpClientFactory)
        {
            if (!IsValidId(itemId) || (!string.IsNullOrEmpty(userId) && !IsValidId(userId)))
                return BadRequest("Invalid item or user ID.");

            var item = LibraryManager.GetItemById(itemId);
            if (item == null) return NotFound();

            // Reattach saves left behind if this game's file was moved or renamed.
            _saveLinks.OnGameLaunch(item);

            // EXPERIMENT (experiment/game-streaming): some platforms stream from the gaming PC.
            if (JellyEmuStreamController.IsStreamedPlatform(AppPaths, ResolvePlatformTag(item)))
                return StreamTest(itemId);

            var resolvedCore = ResolveCore(item, userId, core);

            return resolvedCore switch
            {
                "pico8" => PlayPico8(itemId),
                "play"  => await PlayPlay(itemId, userId, slot),
                _       => await PlayEjs(itemId, userId, slot, core, httpClientFactory)
            };
        }

        /// <summary>
        /// EXPERIMENT: full-screen page embedding a moonlight-web-stream session (Sunshine on the
        /// gaming laptop, bridged to WebRTC). Gets a one-time pass from JellyEmuStreamController,
        /// sends heartbeats while open, and on LT+RT+L3+R3 quits the game and goes back to
        /// Jellyfin. Logs diagnostics to the Jellyfin client log.
        /// </summary>
        private ContentResult StreamTest(string itemId)
        {
            var baseUrl = ToAppUrl(string.Empty).TrimEnd('/');
            var js = JavaScriptEncoder.Default;

            var html = $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>Streaming</title>
                <script src="{{baseUrl}}/jellyemu/assets/jellyemu.utils.js"></script>
                <style>
                  html, body { margin: 0; height: 100%; background: #000; overflow: hidden; }
                  iframe { border: 0; width: 100%; height: 100%; display: block; }
                  #dbg { position: fixed; top: 6px; left: 6px; z-index: 9; max-width: 60vw; color: #7CFC00;
                         font: 13px/1.35 monospace; background: rgba(0,0,0,.7); padding: 4px 8px;
                         white-space: pre-wrap; pointer-events: none; border-radius: 4px; }
                </style>
                </head>
                <body>
                <iframe id="f" allow="gamepad *; autoplay *; fullscreen *; keyboard-map *; clipboard-read *; clipboard-write *" allowfullscreen></iframe>
                <div id="dbg"></div>
                <script>
                (function () {
                  var exitUrl = "{{js.Encode(baseUrl + "/web/#/details?id=" + itemId)}}";
                  var t0 = Date.now(), lines = [], unsent = [], dbg = document.getElementById('dbg'), f = document.getElementById('f');
                  function log(m) {
                    var l = ((Date.now() - t0) / 1000).toFixed(1) + 's ' + m;
                    lines.push(l); unsent.push(l);
                    dbg.textContent = 'JellyEmu stream test - LT+RT+L3+R3 to exit\n' + lines.slice(-12).join('\n');
                  }
                  function flush() {
                    if (!unsent.length || !window.JellyEmu) return;
                    var token = JellyEmu.getAuthToken(); if (!token) return;
                    var body = unsent.join('\n'); unsent = [];
                    fetch(JellyEmu.getUrl('/ClientLog/Document'), { method: 'POST', keepalive: true, body: body,
                      headers: { 'Content-Type': 'text/plain', 'Authorization': 'MediaBrowser Client="JellyEmu-StreamTest", Device="Xbox", DeviceId="jellyemu-streamtest", Version="1.0", Token="' + token + '"' } }).catch(function () {});
                  }
                  setInterval(flush, 4000);
                  window.addEventListener('pagehide', flush);
                  setTimeout(function () { dbg.style.display = 'none'; }, 15000);

                  log('UA: ' + navigator.userAgent);
                  log('page: ' + location.origin + ' secure=' + window.isSecureContext);
                  f.addEventListener('load', function () { log('iframe loaded'); try { f.focus(); log('iframe focused'); } catch (e) { log('focus failed ' + e); } });
                  // Ask JellyEmu (with this user's Jellyfin login) for a one-time pass to the stream.
                  JellyEmu.fetch('/jellyemu/stream/pass/{{js.Encode(itemId)}}', { method: 'POST' })
                    .then(function (r) {
                      if (r.status === 409) return r.json().then(function (d) { throw new Error(d.message); });
                      if (!r.ok) throw new Error('Could not start the stream (HTTP ' + r.status + ').');
                      return r.json();
                    })
                    .then(function (d) { log('pass received'); f.src = d.url; })
                    .catch(function (e) { showMessage(e.message); log('no stream: ' + e.message); flush(); });

                  function showMessage(text) {
                    var m = document.createElement('div');
                    m.textContent = text + ' Press B to go back.';
                    m.style.cssText = 'position:fixed;inset:0;display:flex;align-items:center;justify-content:center;color:#fff;font:24px sans-serif;text-align:center;padding:40px';
                    document.body.appendChild(m);
                    window.addEventListener('keydown', function () { location.replace(exitUrl); }, { once: true });
                  }

                  // Tell JellyEmu the stream is still open, so nobody else takes over the gaming PC.
                  setInterval(function () { JellyEmu.fetch('/jellyemu/stream/heartbeat', { method: 'POST' }).catch(function () {}); }, 30000);
                  window.addEventListener('blur', function () { log('top window blur (focus moved into iframe?)'); });
                  window.addEventListener('focus', function () { log('top window focus'); });
                  ['keydown'].forEach(function (n) { window.addEventListener(n, function (e) { log(n + ' key=' + e.key + ' keyCode=' + e.keyCode); }, true); });

                  // Gamepad visibility in the top frame, plus the exit combo.
                  var seen = false, exiting = false;
                  function pressed(gp, i) { var b = gp.buttons[i]; return !!(b && (b.pressed || b.value > 0.5)); }
                  function poll() {
                    var pads = []; try { pads = navigator.getGamepads ? navigator.getGamepads() : []; } catch (e) {}
                    for (var i = 0; i < pads.length; i++) {
                      var gp = pads[i]; if (!gp) continue;
                      if (!seen) { seen = true; log('top frame sees gamepad: ' + gp.id); }
                      if (!exiting && pressed(gp, 6) && pressed(gp, 7) && pressed(gp, 10) && pressed(gp, 11)) {
                        // Leave without history.back(): the iframe's own navigations share the
                        // tab history, so "back" would only step the iframe (e.g. to its login page).
                        exiting = true; log('exit combo'); flush();
                        // Quit the game on the gaming PC, not just the stream.
                        try { JellyEmu.fetch('/jellyemu/stream/quit', { method: 'POST', keepalive: true }).catch(function () {}); } catch (e) {}
                        if (f.parentNode) f.parentNode.removeChild(f);
                        location.replace(exitUrl);
                        setTimeout(function () { exiting = false; }, 3000); // allow a retry if navigation failed
                      }
                    }
                    requestAnimationFrame(poll);
                  }
                  requestAnimationFrame(poll);
                  setTimeout(function () { if (!seen) log('top frame sees no gamepad after 10s'); }, 10000);
                })();
                </script>
                </body>
                </html>
                """;
            return Content(html, "text/html");
        }

        /// <summary>
        /// Returns a standalone Play! PS2 HTML play page for the given item.
        /// Loads the Play! WebAssembly runtime and streams the disc via worker-based IO.
        ///
        /// Path: GET /jellyemu/play/play/{itemId}
        /// </summary>
        [HttpGet("/jellyemu/play/play/{itemId}")]
        [HttpGet("/jellyemu/ps2/play/{itemId}")]
        [Produces(MediaTypeNames.Text.Html)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> PlayPlay(string itemId, [FromQuery] string? userId = null, [FromQuery] int? slot = null)
        {
            if (!IsValidId(itemId) || (!string.IsNullOrEmpty(userId) && !IsValidId(userId)))
                return BadRequest("Invalid item or user ID.");

            var item = LibraryManager.GetItemById(itemId);
            if (item == null)
            {
                Logger.LogWarning("[JellyEmu] PlayPlay: item {ItemId} not found", SanitizeForLog(itemId));
                return NotFound();
            }

            // A game in its own folder with a single ROM is served as that file, so name the URL after it.
            var romFileForUrl = JellyEmuFileService.GetSingleRomFileInFolder(item.Path ?? string.Empty) ?? item.Path;
            var ext = !string.IsNullOrEmpty(romFileForUrl) ? Path.GetExtension(romFileForUrl) : ".iso";
            var filename = !string.IsNullOrEmpty(romFileForUrl) ? Path.GetFileNameWithoutExtension(romFileForUrl) : itemId;
            var cleanFilename = CleanCosmeticFilename(filename);
            if (string.IsNullOrWhiteSpace(cleanFilename)) cleanFilename = itemId;

            var romUrl = ToAppUrl($"jellyemu/rom/{itemId}/{cleanFilename}{ext}");
            if (!string.IsNullOrEmpty(userId)) romUrl += $"?userId={userId}";

            var hasSaves = !string.IsNullOrEmpty(userId);
            var activeSlot = Math.Max(1, slot ?? 1);
            var saveGetUrl = hasSaves ? ToAppUrl($"jellyemu/save/{itemId}/{userId}") : "";
            var savePostUrl = hasSaves ? ToAppUrl($"jellyemu/save/{itemId}/{userId}") : "";

            var platformTag = "PlayStation 2";
            var effectivePrefs = hasSaves
                ? await PreferenceService.GetEffectivePreferencesAsync(userId!, platformTag)
                : JellyEmuPreferenceService.SystemDefaults;

            var customBindingsJson = !string.IsNullOrWhiteSpace(effectivePrefs.Controls) && effectivePrefs.Controls.Trim().StartsWith("{")
                ? effectivePrefs.Controls.Trim()
                : "null";

            var gameName = HtmlEncoder.Default.Encode(item.Name);

            var assembly = typeof(JellyEmuPlayController).Assembly;
            var resourceName = "JellyEmu.Templates.play.html";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Logger.LogError("[JellyEmu] Play! PS2 embedded template not found at {ResourceName}", resourceName);
                return StatusCode(StatusCodes.Status500InternalServerError, "Template resource is missing.");
            }

            using StreamReader reader = new StreamReader(stream);
            var templateContent = reader.ReadToEnd();
            var template = Template.Parse(templateContent);

            if (template.HasErrors)
            {
                var errors = string.Join(", ", template.Messages);
                Logger.LogError("[JellyEmu] Play! PS2 template parse error: {Errors}", errors);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error parsing Scriban template.");
            }

            var html = template.Render(new
            {
                game_name = gameName,
                item_id = itemId,
                user_id = userId ?? string.Empty,
                base_url = GetPathBase(),
                rom_url = romUrl,
                save_get_url = saveGetUrl,
                save_post_url = savePostUrl,
                active_slot = activeSlot,
                custom_bindings_json = customBindingsJson,
                screenshots_to_library = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ScreenshotsFolder),
                version = Plugin.Instance?.Version.ToString() ?? "1.0.8"
            });

            ApplyCrossOriginIsolationHeaders();

            return Content(html, MediaTypeNames.Text.Html);
        }

        /// <summary>
        /// Returns a standalone PICO-8 HTML play page for the given item.
        /// The page replicates the Lexaloffle BBS shell JS and loads the
        /// PICO-8 runtime from /jellyemu/pico8/runtime.js, passing the cart
        /// via Module.arguments exactly as the BBS does.
        ///
        /// Supports .p8 and .p8.png cart files.
        /// No save-state support — PICO-8 manages its own internal storage.
        ///
        /// Path: GET /jellyemu/pico8/play/{itemId}
        /// </summary>
        [HttpGet("/jellyemu/pico8/play/{itemId}")]
        [Produces(MediaTypeNames.Text.Html)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult PlayPico8(string itemId)
        {
            if (!IsValidId(itemId))
                return BadRequest("Invalid item ID.");

            var item = LibraryManager.GetItemById(itemId);
            if (item == null)
            {
                Logger.LogWarning("[JellyEmu] Pico8Play: item {ItemId} not found", SanitizeForLog(itemId));
                return NotFound();
            }

            var ext = RomExtensions.GetPico8Extension(item);

            var cartUrl = ToAppUrl($"jellyemu/rom/{itemId}/{itemId}{ext}");
            var gameName = HtmlEncoder.Default.Encode(item.Name);

            Logger.LogInformation("[JellyEmu] PICO-8 play: {Name} ({ItemId}) cart={CartUrl}",
                item.Name, itemId, cartUrl);

            var assembly = typeof(JellyEmuPlayController).Assembly;
            var resourceName = "JellyEmu.Templates.pico8.html";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Logger.LogError("[JellyEmu] PICO-8 embedded template not found at {ResourceName}", resourceName);
                return StatusCode(StatusCodes.Status500InternalServerError, "Template resource is missing.");
            }

            using StreamReader reader = new StreamReader(stream);
            var templateContent = reader.ReadToEnd();

            var template = Template.Parse(templateContent);

            if (template.HasErrors)
            {
                var errors = string.Join(", ", template.Messages);
                Logger.LogError("[JellyEmu] PICO-8 template parse error: {Errors}", errors);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error parsing Scriban template.");
            }

            var html = template.Render(new
            {
                game_name = gameName,
                base_url = GetPathBase(),
                cart_url = cartUrl,
                item_id = itemId,
                screenshots_to_library = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ScreenshotsFolder)
            });

            ApplyCrossOriginIsolationHeaders();

            return Content(html, MediaTypeNames.Text.Html);
        }

        /// <summary>
        /// Returns a standalone EmulatorJS HTML page for the given item.
        /// No authentication required — the ROM is fetched via /jellyemu/rom/{itemId}.
        /// 
        /// Path: GET /jellyemu/play/{itemId}
        /// Parameters: 
        ///   - itemId (string, path): The unique ID of the library item.
        ///   - userId (string, query, optional): Allows wire up of per-user save states.
        /// Returns Example: `200 OK` (Content-Type: text/html)
        /// </summary>
        [HttpGet("/jellyemu/ejs/play/{itemId}")]
        [Produces(MediaTypeNames.Text.Html)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        public async Task<IActionResult> PlayEjs(string itemId, [FromQuery] string? userId, [FromQuery] int? slot, [FromQuery] string? core,
            [FromServices] IHttpClientFactory httpClientFactory)
        {
            if (!IsValidId(itemId) || (!string.IsNullOrEmpty(userId) && !IsValidId(userId)))
                return BadRequest("Invalid item or user ID.");

            var item = LibraryManager.GetItemById(itemId);
            if (item == null)
            {
                Logger.LogWarning("[JellyEmu] Play: item {ItemId} not found", SanitizeForLog(itemId));
                return NotFound();
            }

            var resolvedCore = ResolveCore(item, userId, core);
            if (string.IsNullOrEmpty(resolvedCore))
            {
                Logger.LogWarning("[JellyEmu] Play: could not resolve a core for {Name} ({ItemId}), path={Path}",
                    item.Name, SanitizeForLog(itemId), item.Path);
                return UnprocessableEntity(
                    $"JellyEmu could not determine which emulator core to use for \"{item.Name}\". " +
                    "Tag the item with its console name (e.g. \"SNES\") or rename the ROM to use a recognised file extension.");
            }

            var platformTag = ResolvePlatformTag(item);
            var availableCores = GetAvailableCoresForItem(item);
            var availableCoresJson = System.Text.Json.JsonSerializer.Serialize(availableCores.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                needsThreads = c.NeedsThreads
            }));
            // A game in its own folder with a single ROM is served as that file, so name the URL after it.
            var romFileForUrl = JellyEmuFileService.GetSingleRomFileInFolder(item.Path ?? string.Empty) ?? item.Path;
            var ext = !string.IsNullOrEmpty(romFileForUrl) ? Path.GetExtension(romFileForUrl) : ".zip";
            var filename = !string.IsNullOrEmpty(romFileForUrl) ? Path.GetFileNameWithoutExtension(romFileForUrl) : itemId;
            var cleanFilename = CleanCosmeticFilename(filename);
            if (string.IsNullOrWhiteSpace(cleanFilename))
            {
                cleanFilename = itemId;
            }
            var romUrl = ToAppUrl($"jellyemu/rom/{itemId}/{cleanFilename}{ext}");
            if (!string.IsNullOrEmpty(userId))
            {
                romUrl += $"?userId={userId}";
            }

            var isJ3u = !string.IsNullOrEmpty(item.Path) && item.Path.EndsWith(".j3u", StringComparison.OrdinalIgnoreCase);

            var hasSaves = !string.IsNullOrEmpty(userId);
            var effectivePrefs = hasSaves
                ? await PreferenceService.GetEffectivePreferencesAsync(userId!, platformTag)
                : JellyEmuPreferenceService.SystemDefaults;

            var activeSlot = Math.Max(1, slot ?? 1);
            var activeShader = effectivePrefs.Shader;
            var videoRotation = effectivePrefs.VideoRotation;
            var saveGetUrl = hasSaves ? ToAppUrl($"jellyemu/save/{itemId}/{userId}") : "";
            var savePostUrl = hasSaves ? ToAppUrl($"jellyemu/save/{itemId}/{userId}") : "";

            var saveExists = hasSaves && System.IO.File.Exists(GetSavePath(userId!, itemId, activeSlot));

            var igdbId = item.GetProviderId("IGDB");
            int numericGameId;
            if (!int.TryParse(igdbId, out numericGameId) || numericGameId <= 0)
            {
                numericGameId = (item.Id.GetHashCode() & 0x7FFFFFFF);
                if (numericGameId == 0) numericGameId = 1;
            }

            var netplayServer = ToAppUrl("jellyemu/netplay");
            var netplayIceServers = Plugin.Instance?.Configuration.NetplayIceServers ?? string.Empty;
            var netplayIceServersJson = System.Text.Json.JsonSerializer.Serialize(netplayIceServers);
            const bool hasNetplay = true;

            var gameName = HtmlEncoder.Default.Encode(item.Name);
            var ejsBase = EjsManager.IsReady
                ? ToAppUrl("jellyemu/ejs")
                : JellyEmuEjsManager.CdnBase;

            // Load the embedded Scriban template
            var assembly = typeof(JellyEmuPlayController).Assembly;
            var resourceName = "JellyEmu.Templates.ejs.html";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                Logger.LogError("[JellyEmu] EJS embedded template not found at {ResourceName}", resourceName);
                return StatusCode(StatusCodes.Status500InternalServerError, "EJS template resource is missing.");
            }

            using StreamReader reader = new StreamReader(stream);
            var templateContent = reader.ReadToEnd();
            var template = Template.Parse(templateContent);

            if (template.HasErrors)
            {
                var errors = string.Join(", ", template.Messages);
                Logger.LogError("[JellyEmu] EJS template parse error: {Errors}", errors);
                return StatusCode(StatusCodes.Status500InternalServerError, "Error parsing EJS Scriban template.");
            }

            var biosService = HttpContext.RequestServices.GetService(typeof(JellyEmuBiosService)) as JellyEmuBiosService;
            var relBios = biosService?.ResolveBiosRelativePath(platformTag, resolvedCore);
            var biosUrl = !string.IsNullOrEmpty(relBios) ? ToAppUrl($"jellyemu/bios/file/{relBios}") : string.Empty;

            var inputService = HttpContext.RequestServices.GetService(typeof(JellyEmuInputService)) as JellyEmuInputService;
            var inputScheme = inputService?.GetScheme(platformTag ?? resolvedCore);
            var inputSchemeJson = inputScheme != null
                ? System.Text.Json.JsonSerializer.Serialize(inputScheme, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })
                : "null";

            var customBindingsJson = !string.IsNullOrWhiteSpace(effectivePrefs.Controls) && effectivePrefs.Controls.Trim().StartsWith("{")
                ? effectivePrefs.Controls.Trim()
                : "null";

            var html = template.Render(new
            {
                game_name = gameName,
                base_url = GetPathBase(),
                core = resolvedCore,
                platform_tag = platformTag,
                input_scheme_json = inputSchemeJson,
                custom_bindings_json = customBindingsJson,
                available_cores_json = availableCoresJson,
                rom_url = romUrl,
                ejs_base = ejsBase,
                item_id = itemId,
                user_id = userId,
                bios_url = biosUrl,
                active_slot = activeSlot,
                slot_value = slot ?? 0,
                has_saves = hasSaves,
                active_shader = activeShader ?? string.Empty,
                video_rotation = videoRotation,
                igdb_id = igdbId ?? string.Empty,
                game_id = numericGameId,
                has_netplay = hasNetplay,
                netplay_server = netplayServer,
                netplay_ice_servers = netplayIceServers,
                netplay_ice_servers_json = netplayIceServersJson,
                save_exists = saveExists,
                save_get_url = saveGetUrl,
                save_post_url = savePostUrl,
                is_m3u = isJ3u,
                needs_threads = IsThreadedCore(resolvedCore),
                virtual_gamepad = effectivePrefs.VirtualGamepad,
                virtual_gamepad_lefty = effectivePrefs.VirtualGamepadLefty,
                vsync = effectivePrefs.Vsync,
                ffrate = effectivePrefs.FfRate,
                smrate = effectivePrefs.SmRate,
                show_fps = effectivePrefs.ShowFps,
                scale = effectivePrefs.Scale,
                volume = effectivePrefs.Volume ?? "1",
                mute = effectivePrefs.Mute ?? "0",
                version = JellyEmuVersion.Value,
                screenshots_to_library = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ScreenshotsFolder)
            });

            // When opened as a new tab (threaded cores), these headers make the page
            // cross-origin isolated so SharedArrayBuffer is available. Harmless for iframe mode.
            ApplyCrossOriginIsolationHeaders();

            return Content(html, MediaTypeNames.Text.Html);
        }

        internal static string CleanCosmeticFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return string.Empty;

            // Remove characters that break URL routing or JavaScript string literals:
            // ', ", #, ?, &, \
            return filename
                .Replace("'", "")
                .Replace("\"", "")
                .Replace("#", "")
                .Replace("?", "")
                .Replace("&", "")
                .Replace("\\", "");
        }
    }
}
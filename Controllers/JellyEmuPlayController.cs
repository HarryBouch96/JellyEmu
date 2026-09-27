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
            [FromServices] IHttpClientFactory httpClientFactory, [FromQuery] string? device = null)
        {
            if (!IsValidId(itemId) || (!string.IsNullOrEmpty(userId) && !IsValidId(userId)))
                return BadRequest("Invalid item or user ID.");

            var item = LibraryManager.GetItemById(itemId);
            if (item == null) return NotFound();

            // Reattach saves left behind if this game's file was moved or renamed.
            _saveLinks.OnGameLaunch(item);

            // EXPERIMENT (experiment/game-streaming): "device" comes from the "Play on" picker:
            // "local" plays in this browser, anything else streams from that gaming PC. Without it,
            // platforms the browser can't play stream from a gaming PC (launches that bypass the picker).
            if (!string.IsNullOrEmpty(device) && !string.Equals(device, "local", StringComparison.OrdinalIgnoreCase))
            {
                if (device.Length > 32 || !device.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                    return BadRequest("Invalid device.");
                return await StreamPage(item, itemId, userId, device);
            }
            if (string.IsNullOrEmpty(device) && JellyEmuStreamController.StreamsWithoutPicker(AppPaths, ResolvePlatformTag(item)))
                return await StreamPage(item, itemId, userId, string.Empty);

            var resolvedCore = ResolveCore(item, userId, core);

            return resolvedCore switch
            {
                "pico8" => PlayPico8(itemId),
                "play"  => await PlayPlay(itemId, userId, slot),
                _       => await PlayEjs(itemId, userId, slot, core, httpClientFactory)
            };
        }

        /// <summary>
        /// EXPERIMENT: full-screen page for a game streamed from a gaming PC (Sunshine, bridged to
        /// WebRTC by moonlight-web-stream). The page itself is Web/stream/jellyemu.streamhost.js;
        /// this hands it the game, the gaming PC and the player's controls for this system.
        /// </summary>
        private async Task<ContentResult> StreamPage(MediaBrowser.Controller.Entities.BaseItem item, string itemId, string? userId, string device)
        {
            var baseUrl = ToAppUrl(string.Empty).TrimEnd('/');
            var platformTag = ResolvePlatformTag(item);

            var inputService = HttpContext.RequestServices.GetService(typeof(JellyEmuInputService)) as JellyEmuInputService;
            var scheme = inputService?.GetScheme(platformTag);
            var controls = string.IsNullOrEmpty(userId)
                ? null
                : (await PreferenceService.GetEffectivePreferencesAsync(userId, platformTag)).Controls;

            // Re-serialised rather than pasted in, so nothing in saved controls can end the script.
            object? customBindings = null;
            if (!string.IsNullOrWhiteSpace(controls) && controls.TrimStart().StartsWith('{'))
            {
                try { customBindings = System.Text.Json.JsonDocument.Parse(controls).RootElement.Clone(); }
                catch (System.Text.Json.JsonException) { customBindings = null; }
            }

            // The gaming PC it streams from (the first one when none was picked), for the loading screen.
            var pcs = JellyEmuStreamStore.Read(AppPaths)?.Devices;
            var pc = pcs == null ? null
                : string.IsNullOrEmpty(device) ? pcs.FirstOrDefault()
                : pcs.FirstOrDefault(d => string.Equals(d.Id, device, StringComparison.OrdinalIgnoreCase));

            var config = System.Text.Json.JsonSerializer.Serialize(new
            {
                itemId,
                deviceQuery = string.IsNullOrEmpty(device) ? string.Empty : "?device=" + Uri.EscapeDataString(device),
                exitUrl = baseUrl + "/web/#/details?id=" + itemId,
                gameName = item.Name,
                deviceName = pc?.Name ?? "the gaming PC",
                scheme,
                customBindings
            }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

            var html = $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover">
                <title>{{HtmlEncoder.Default.Encode(item.Name ?? "Streaming")}}</title>
                <style>
                  html, body { margin: 0; height: 100%; background: #000; overflow: hidden; }
                  #je-stream { border: 0; width: 100%; height: 100%; display: block; }
                </style>
                </head>
                <body>
                <iframe id="je-stream" allow="gamepad *; autoplay *; fullscreen *; keyboard-map *" allowfullscreen></iframe>
                <script>window.JE_STREAM = {{config}};</script>
                <script src="{{baseUrl}}/jellyemu/assets/jellyemu.utils.js"></script>
                <script src="{{baseUrl}}/jellyemu/assets/streamhost.js"></script>
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
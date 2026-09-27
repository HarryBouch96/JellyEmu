using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JellyEmu.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Device = JellyEmu.Services.JellyEmuStreamDevices.Device;
using Probe = JellyEmu.Services.JellyEmuStreamDevices.Probe;

namespace JellyEmu.Controllers
{
    /// <summary>
    /// EXPERIMENT (experiment/game-streaming): streams games from gaming PCs via Sunshine and the
    /// moonlight-web-stream bridge, gated behind the Jellyfin login. Caddy serves each PC's bridge
    /// at its stream host and calls the login/check endpoints.
    ///   GET  /jellyemu/stream/devices/{itemId}           - gaming PCs for the "Play on" picker
    ///   POST /jellyemu/stream/pass/{itemId}?device=      - signed-in user asks for a one-time pass
    ///   GET  /jellyemu/stream/login?pass=                - (via Caddy /jellyemu-auth) pass -> session cookie
    ///   GET  /jellyemu/stream/check                      - Caddy forward_auth before every bridge request
    ///   POST /jellyemu/stream/heartbeat?device=          - the stream page is still open
    ///   POST /jellyemu/stream/quit?device=               - quit the game on that PC
    ///   GET  /jellyemu/stream/launch                     - a PC's launcher asks which game to start
    ///   GET  /jellyemu/stream/platforms                  - platforms any PC can stream (UI shows Play)
    /// Settings: {DataPath}/jellyemu-stream.json. Launcher key: {DataPath}/jellyemu-launcher.key.
    /// </summary>
    public class JellyEmuStreamController : JellyEmuBaseController
    {
        // The bridge account Caddy signs requests in as. Its admin API is unreachable through
        // Caddy (see JellyEmuStreamAuth.IsAllowedRequest).
        private const string BridgeUser = "JellyEmuStream";
        private static readonly TimeSpan ProbeCacheFor = TimeSpan.FromSeconds(5);
        private static readonly ConcurrentDictionary<string, (Probe Result, DateTimeOffset At)> ProbeCache = new();
        private static readonly object StateLock = new();

        public JellyEmuStreamController(
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            ILogger<JellyEmuStreamController> logger,
            JellyEmuEjsManager ejsManager,
            JellyEmuSessionService sessionService,
            IHttpClientFactory httpClientFactory)
            : base(libraryManager, appPaths, logger, ejsManager, sessionService, httpClientFactory) { }

        public class CurrentGame
        {
            public string ItemId { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Platform { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public DateTimeOffset LastSeen { get; set; }
        }

        [HttpGet("/jellyemu/stream/devices/{itemId}")]
        [Authorize]
        public async Task<IActionResult> Devices(string itemId)
        {
            var userId = CurrentUserId();
            if (!IsValidId(itemId) || string.IsNullOrEmpty(userId)) return BadRequest();
            var item = LibraryManager.GetItemById(itemId);
            if (item == null) return NotFound();
            var platform = ResolvePlatformTag(item);

            var result = new List<object>();
            foreach (var device in ReadSettings()?.Devices ?? new List<Device>())
            {
                var probe = device.Supports(platform) ? await ProbeDevice(device).ConfigureAwait(false) : Probe.Offline;
                var current = ReadCurrent(device.Id);
                var status = JellyEmuStreamDevices.Evaluate(device, platform, probe, current?.UserId,
                    current?.LastSeen ?? DateTimeOffset.MinValue, userId, DateTimeOffset.UtcNow);
                result.Add(new { id = device.Id, name = device.Name, available = status.Available, reason = status.Reason });
            }
            return Ok(new { platform, devices = result });
        }

        [HttpPost("/jellyemu/stream/pass/{itemId}")]
        [Authorize]
        public async Task<IActionResult> Pass(string itemId, [FromQuery] string? device)
        {
            var userId = CurrentUserId();
            if (!IsValidId(itemId) || string.IsNullOrEmpty(userId)) return BadRequest();
            var item = LibraryManager.GetItemById(itemId);
            if (item == null) return NotFound();
            var target = FindDevice(device);
            if (target == null) return StatusCode(StatusCodes.Status503ServiceUnavailable, "No gaming PC is set up for streaming.");

            var now = DateTimeOffset.UtcNow;
            var probe = await ProbeDevice(target, fresh: true).ConfigureAwait(false);
            var current = ReadCurrent(target.Id);
            var status = JellyEmuStreamDevices.Evaluate(target, ResolvePlatformTag(item), probe, current?.UserId,
                current?.LastSeen ?? DateTimeOffset.MinValue, userId, now);
            if (!status.Available)
                return Conflict(new { message = $"{target.Name}: {status.Reason}." });

            // Every game uses the same Sunshine app, so a different game still running would be
            // resumed instead of starting the new one: quit it first.
            if (probe == Probe.Busy && current?.ItemId != itemId)
                await QuitRunningGame(target).ConfigureAwait(false);

            var pass = new JellyEmuStreamAuth.Claims("pass", userId, itemId, target.HostId, target.AppId,
                now.Add(JellyEmuStreamAuth.PassLifetime).ToUnixTimeSeconds(), JellyEmuStreamAuth.NewNonce());
            var token = JellyEmuStreamAuth.Sign(pass, GetKey());
            Logger.LogInformation("[JellyEmu] Stream pass issued for user {UserId} item {ItemId} on {Device}",
                SanitizeForLog(userId), SanitizeForLog(itemId), SanitizeForLog(target.Id));
            return Ok(new { url = $"{target.StreamOrigin.TrimEnd('/')}/jellyemu-auth?pass={Uri.EscapeDataString(token)}" });
        }

        [HttpGet("/jellyemu/stream/login")]
        [AllowAnonymous]
        public IActionResult Login([FromQuery] string? pass)
        {
            var now = DateTimeOffset.UtcNow;
            var claims = JellyEmuStreamAuth.Verify(pass, "pass", GetKey(), now);
            if (claims == null || !JellyEmuStreamAuth.ConsumePass(claims, now))
            {
                Logger.LogWarning("[JellyEmu] Stream login refused: invalid, expired or reused pass");
                return Unauthorized("This stream link has expired. Press Play again.");
            }

            var item = LibraryManager.GetItemById(claims.ItemId);
            var device = ReadSettings()?.Devices.FirstOrDefault(d => d.HostId == claims.HostId);
            if (item == null || device == null) return NotFound();
            WriteCurrent(device.Id, new CurrentGame
            {
                ItemId = claims.ItemId,
                UserId = claims.UserId,
                Name = item.Name,
                Platform = ResolvePlatformTag(item),
                Path = item.Path ?? string.Empty,
                LastSeen = now
            });

            var session = claims with
            {
                Kind = "session",
                Expires = now.Add(JellyEmuStreamAuth.SessionLifetime).ToUnixTimeSeconds(),
                Nonce = JellyEmuStreamAuth.NewNonce()
            };
            Response.Cookies.Append(JellyEmuStreamAuth.CookieName, JellyEmuStreamAuth.Sign(session, GetKey()), new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = JellyEmuStreamAuth.SessionLifetime
            });
            Logger.LogInformation("[JellyEmu] Stream session started for user {UserId}: {Name} on {Device}",
                SanitizeForLog(claims.UserId), SanitizeForLog(item.Name), SanitizeForLog(device.Id));
            return Redirect($"/stream.html?hostId={claims.HostId}&appId={claims.AppId}");
        }

        [HttpGet("/jellyemu/stream/check")]
        [AllowAnonymous]
        public IActionResult Check()
        {
            var method = Request.Headers["X-Forwarded-Method"].ToString();
            var uri = Request.Headers["X-Forwarded-Uri"].ToString();
            var session = JellyEmuStreamAuth.Verify(Request.Cookies[JellyEmuStreamAuth.CookieName], "session", GetKey(), DateTimeOffset.UtcNow);
            if (session == null)
            {
                Logger.LogInformation("[JellyEmu] Stream request refused (no valid session): {Method} {Uri}", SanitizeForLog(method), SanitizeForLog(uri));
                return Unauthorized();
            }
            if (!JellyEmuStreamAuth.IsAllowedRequest(method, uri))
            {
                Logger.LogWarning("[JellyEmu] Stream request refused (not allowed for streaming): {Method} {Uri} user {UserId}",
                    SanitizeForLog(method), SanitizeForLog(uri), SanitizeForLog(session.UserId));
                return StatusCode(StatusCodes.Status403Forbidden);
            }
            Response.Headers[JellyEmuStreamAuth.UserHeader] = BridgeUser;
            return Ok();
        }

        /// <summary>
        /// Platforms any gaming PC can stream, so the UI shows Play for them. Anonymous because the
        /// UI asks before the Jellyfin client is signed in; it only lists platform names.
        /// </summary>
        [HttpGet("/jellyemu/stream/platforms")]
        [AllowAnonymous]
        public IActionResult Platforms() =>
            Ok((ReadSettings()?.Devices ?? new List<Device>()).SelectMany(d => d.Platforms).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

        [HttpPost("/jellyemu/stream/heartbeat")]
        [Authorize]
        public IActionResult Heartbeat([FromQuery] string? device)
        {
            var target = FindDevice(device);
            if (target == null) return NoContent();
            var userId = CurrentUserId();
            lock (StateLock)
            {
                var current = ReadCurrent(target.Id);
                if (current == null || current.UserId != userId) return NoContent();
                current.LastSeen = DateTimeOffset.UtcNow;
                WriteCurrent(target.Id, current);
            }
            return NoContent();
        }

        [HttpPost("/jellyemu/stream/quit")]
        [Authorize]
        public async Task<IActionResult> Quit([FromQuery] string? device)
        {
            var target = FindDevice(device);
            if (target == null) return NoContent();
            var current = ReadCurrent(target.Id);
            // Only the person playing may quit the game.
            if (current != null && current.UserId != CurrentUserId()) return Forbid();
            await QuitRunningGame(target).ConfigureAwait(false);
            if (current != null)
            {
                current.LastSeen = DateTimeOffset.MinValue;
                WriteCurrent(target.Id, current);
            }
            return NoContent();
        }

        /// <summary>
        /// Called by a gaming PC's launcher (its single "JellyEmu" Sunshine app) to find out which
        /// game to start. Requires the shared launcher key; X-JellyEmu-Device names the PC.
        /// </summary>
        [HttpGet("/jellyemu/stream/launch")]
        [AllowAnonymous]
        public IActionResult Launch()
        {
            var expected = ReadLauncherKey();
            var given = Request.Headers["X-JellyEmu-Launcher-Key"].ToString();
            if (expected == null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
            {
                Logger.LogWarning("[JellyEmu] Launcher request refused: bad key");
                return Unauthorized();
            }
            var target = FindDevice(Request.Headers["X-JellyEmu-Device"].ToString());
            var current = target == null ? null : ReadCurrent(target.Id);
            if (current == null) return NotFound();
            Logger.LogInformation("[JellyEmu] Launcher on {Device} starting {Name} ({Platform})",
                SanitizeForLog(target!.Id), SanitizeForLog(current.Name), SanitizeForLog(current.Platform));
            return Ok(new { itemId = current.ItemId, name = current.Name, platform = current.Platform, path = current.Path });
        }

        /// <summary>Asks the PC's bridge whether its Sunshine is reachable and free. Cached briefly.</summary>
        private async Task<Probe> ProbeDevice(Device device, bool fresh = false)
        {
            var now = DateTimeOffset.UtcNow;
            if (!fresh && ProbeCache.TryGetValue(device.Id, out var cached) && now - cached.At < ProbeCacheFor)
                return cached.Result;

            Probe result;
            try
            {
                using var client = HttpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(3);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{device.BridgeUrl.TrimEnd('/')}/api/host?host_id={device.HostId}");
                request.Headers.Add(JellyEmuStreamAuth.UserHeader, BridgeUser);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    result = Probe.HostUnavailable;
                }
                else
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var host = doc.RootElement.GetProperty("host");
                    var paired = host.TryGetProperty("paired", out var p) && p.GetString() == "Paired";
                    var state = host.TryGetProperty("server_state", out var s) ? s.GetString() : null;
                    result = !paired || state == null ? Probe.HostUnavailable
                        : string.Equals(state, "Busy", StringComparison.OrdinalIgnoreCase) ? Probe.Busy
                        : Probe.Free;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                result = Probe.Offline;
            }
            catch (Exception ex) when (ex is JsonException || ex is KeyNotFoundException || ex is InvalidOperationException)
            {
                result = Probe.HostUnavailable;
            }
            ProbeCache[device.Id] = (result, now);
            return result;
        }

        private async Task QuitRunningGame(Device device)
        {
            try
            {
                using var client = HttpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                using var request = new HttpRequestMessage(HttpMethod.Post, device.BridgeUrl.TrimEnd('/') + "/api/host/cancel")
                {
                    Content = JsonContent.Create(new { host_id = device.HostId })
                };
                request.Headers.Add(JellyEmuStreamAuth.UserHeader, BridgeUser);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                ProbeCache.TryRemove(device.Id, out _);
                Logger.LogInformation("[JellyEmu] Asked {Device} to quit its game: HTTP {Status}", SanitizeForLog(device.Id), (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                Logger.LogWarning(ex, "[JellyEmu] Could not ask {Device} to quit its game", SanitizeForLog(device.Id));
            }
        }

        private string? CurrentUserId() =>
            User.FindFirstValue("Jellyfin-UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        /// <summary>The named device, or the first one when no name is given.</summary>
        private Device? FindDevice(string? id)
        {
            var devices = ReadSettings()?.Devices;
            if (devices == null || devices.Count == 0) return null;
            return string.IsNullOrEmpty(id) ? devices[0] : devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Platforms streamed from a gaming PC instead of played in the browser.</summary>
        internal static bool IsStreamedPlatform(IApplicationPaths appPaths, string platform) =>
            ReadSettings(appPaths)?.Devices.Any(d => d.Supports(platform)) == true;

        private JellyEmuStreamDevices.Settings? ReadSettings() => ReadSettings(AppPaths);

        private static JellyEmuStreamDevices.Settings? ReadSettings(IApplicationPaths appPaths)
        {
            var file = Path.Combine(appPaths.DataPath, "jellyemu-stream.json");
            if (!System.IO.File.Exists(file)) return null;
            try
            {
                var s = JsonSerializer.Deserialize<JellyEmuStreamDevices.Settings>(System.IO.File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (s == null) return null;
                s.Devices = s.Devices.Where(d => IsValidDeviceId(d.Id) && !string.IsNullOrEmpty(d.StreamOrigin) && !string.IsNullOrEmpty(d.BridgeUrl)).ToList();
                return s;
            }
            catch (JsonException) { return null; }
        }

        private static bool IsValidDeviceId(string id) =>
            !string.IsNullOrEmpty(id) && id.Length <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        private string CurrentFile(string deviceId) => Path.Combine(AppPaths.DataPath, $"jellyemu-stream-current-{deviceId}.json");

        private CurrentGame? ReadCurrent(string deviceId)
        {
            lock (StateLock)
            {
                var file = CurrentFile(deviceId);
                if (!System.IO.File.Exists(file)) return null;
                try { return JsonSerializer.Deserialize<CurrentGame>(System.IO.File.ReadAllText(file)); }
                catch (JsonException) { return null; }
            }
        }

        private void WriteCurrent(string deviceId, CurrentGame game)
        {
            lock (StateLock)
            {
                System.IO.File.WriteAllText(CurrentFile(deviceId), JsonSerializer.Serialize(game));
            }
        }

        private string? ReadLauncherKey()
        {
            var file = Path.Combine(AppPaths.DataPath, "jellyemu-launcher.key");
            if (!System.IO.File.Exists(file)) return null;
            var key = System.IO.File.ReadAllText(file).Trim();
            return key.Length >= 32 ? key : null;
        }

        private byte[] GetKey()
        {
            var file = Path.Combine(AppPaths.DataPath, "jellyemu-stream.key");
            lock (StateLock)
            {
                if (System.IO.File.Exists(file))
                {
                    var existing = System.IO.File.ReadAllBytes(file);
                    if (existing.Length >= 32) return existing;
                }
                var key = RandomNumberGenerator.GetBytes(32);
                System.IO.File.WriteAllBytes(file, key);
                return key;
            }
        }
    }
}

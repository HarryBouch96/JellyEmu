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

namespace JellyEmu.Controllers
{
    /// <summary>
    /// EXPERIMENT (experiment/game-streaming): streams games (PS2, GameCube) from a gaming PC via
    /// Sunshine and the moonlight-web-stream bridge, gated behind the Jellyfin login. Caddy serves
    /// the bridge at the stream host and calls these endpoints:
    ///   POST /jellyemu/stream/pass/{itemId} - signed-in user asks for a one-time pass
    ///   GET  /jellyemu/stream/login?pass=   - (via Caddy /jellyemu-auth) pass -> session cookie
    ///   GET  /jellyemu/stream/check         - Caddy forward_auth before every bridge request
    ///   POST /jellyemu/stream/heartbeat     - the stream page is still open
    ///   POST /jellyemu/stream/quit          - quit the game on the gaming PC
    ///   GET  /jellyemu/stream/launch        - the gaming PC's launcher asks which game to start
    /// Settings: {DataPath}/jellyemu-stream.json. Launcher key: {DataPath}/jellyemu-launcher.key.
    /// </summary>
    public class JellyEmuStreamController : JellyEmuBaseController
    {
        // The bridge account Caddy signs requests in as. Its admin API is unreachable through
        // Caddy (see JellyEmuStreamAuth.IsAllowedRequest).
        private const string BridgeUser = "JellyEmuStream";
        private static readonly TimeSpan BusyWindow = TimeSpan.FromSeconds(90);
        private static readonly object StateLock = new();

        public JellyEmuStreamController(
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            ILogger<JellyEmuStreamController> logger,
            JellyEmuEjsManager ejsManager,
            JellyEmuSessionService sessionService,
            IHttpClientFactory httpClientFactory)
            : base(libraryManager, appPaths, logger, ejsManager, sessionService, httpClientFactory) { }

        public class StreamSettings
        {
            public string StreamOrigin { get; set; } = string.Empty;
            public long HostId { get; set; }
            public long AppId { get; set; }
            public string BridgeUrl { get; set; } = string.Empty;
            public List<string> Platforms { get; set; } = new();
        }

        public class CurrentGame
        {
            public string ItemId { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Platform { get; set; } = string.Empty;
            public string Path { get; set; } = string.Empty;
            public DateTimeOffset LastSeen { get; set; }
        }

        [HttpPost("/jellyemu/stream/pass/{itemId}")]
        [Authorize]
        public async Task<IActionResult> Pass(string itemId)
        {
            var userId = CurrentUserId();
            if (!IsValidId(itemId) || string.IsNullOrEmpty(userId)) return BadRequest();
            var item = LibraryManager.GetItemById(itemId);
            if (item == null) return NotFound();
            var settings = ReadSettings();
            if (settings == null) return StatusCode(StatusCodes.Status503ServiceUnavailable, "Streaming is not configured.");

            var now = DateTimeOffset.UtcNow;
            var current = ReadCurrent();
            if (current != null && current.ItemId != itemId)
            {
                // Someone else is still streaming: don't take their game away.
                if (current.UserId != userId && now - current.LastSeen < BusyWindow)
                    return Conflict(new { message = "The gaming PC is busy: someone else is playing " + current.Name + "." });
                // Otherwise quit the previous game so the new one starts (same Sunshine app for every game).
                await QuitRunningGame(settings).ConfigureAwait(false);
            }

            var pass = new JellyEmuStreamAuth.Claims("pass", userId, itemId, settings.HostId, settings.AppId,
                now.Add(JellyEmuStreamAuth.PassLifetime).ToUnixTimeSeconds(), JellyEmuStreamAuth.NewNonce());
            var token = JellyEmuStreamAuth.Sign(pass, GetKey());
            Logger.LogInformation("[JellyEmu] Stream pass issued for user {UserId} item {ItemId}", SanitizeForLog(userId), SanitizeForLog(itemId));
            return Ok(new { url = $"{settings.StreamOrigin}/jellyemu-auth?pass={Uri.EscapeDataString(token)}" });
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
            if (item == null) return NotFound();
            WriteCurrent(new CurrentGame
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
            Logger.LogInformation("[JellyEmu] Stream session started for user {UserId}: {Name}", SanitizeForLog(claims.UserId), SanitizeForLog(item.Name));
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

        [HttpPost("/jellyemu/stream/heartbeat")]
        [Authorize]
        public IActionResult Heartbeat()
        {
            var userId = CurrentUserId();
            lock (StateLock)
            {
                var current = ReadCurrent();
                if (current == null || current.UserId != userId) return NoContent();
                current.LastSeen = DateTimeOffset.UtcNow;
                WriteCurrent(current);
            }
            return NoContent();
        }

        [HttpPost("/jellyemu/stream/quit")]
        [Authorize]
        public async Task<IActionResult> Quit()
        {
            var settings = ReadSettings();
            if (settings == null) return NoContent();
            var current = ReadCurrent();
            // Only the person playing may quit the game.
            if (current != null && current.UserId != CurrentUserId()) return Forbid();
            await QuitRunningGame(settings).ConfigureAwait(false);
            if (current != null)
            {
                current.LastSeen = DateTimeOffset.MinValue;
                WriteCurrent(current);
            }
            return NoContent();
        }

        /// <summary>
        /// Called by the launcher on the gaming PC (the single "JellyEmu" Sunshine app) to find out
        /// which game to start. Requires the shared launcher key.
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
            var current = ReadCurrent();
            if (current == null) return NotFound();
            Logger.LogInformation("[JellyEmu] Launcher starting {Name} ({Platform})", SanitizeForLog(current.Name), SanitizeForLog(current.Platform));
            return Ok(new { itemId = current.ItemId, name = current.Name, platform = current.Platform, path = current.Path });
        }

        private async Task QuitRunningGame(StreamSettings settings)
        {
            try
            {
                using var client = HttpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.BridgeUrl.TrimEnd('/') + "/api/host/cancel")
                {
                    Content = JsonContent.Create(new { host_id = settings.HostId })
                };
                request.Headers.Add(JellyEmuStreamAuth.UserHeader, BridgeUser);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                Logger.LogInformation("[JellyEmu] Asked the gaming PC to quit its game: HTTP {Status}", (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                Logger.LogWarning(ex, "[JellyEmu] Could not ask the gaming PC to quit its game");
            }
        }

        private string? CurrentUserId() =>
            User.FindFirstValue("Jellyfin-UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        /// <summary>Platforms streamed from the gaming PC instead of played in the browser.</summary>
        internal static bool IsStreamedPlatform(IApplicationPaths appPaths, string platform)
        {
            var settings = ReadSettings(appPaths);
            return settings != null && settings.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
        }

        private StreamSettings? ReadSettings() => ReadSettings(AppPaths);

        private static StreamSettings? ReadSettings(IApplicationPaths appPaths)
        {
            var file = Path.Combine(appPaths.DataPath, "jellyemu-stream.json");
            if (!System.IO.File.Exists(file)) return null;
            try
            {
                var s = JsonSerializer.Deserialize<StreamSettings>(System.IO.File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return s != null && !string.IsNullOrEmpty(s.StreamOrigin) && !string.IsNullOrEmpty(s.BridgeUrl) ? s : null;
            }
            catch (JsonException) { return null; }
        }

        private string CurrentFile => Path.Combine(AppPaths.DataPath, "jellyemu-stream-current.json");

        private CurrentGame? ReadCurrent()
        {
            lock (StateLock)
            {
                if (!System.IO.File.Exists(CurrentFile)) return null;
                try { return JsonSerializer.Deserialize<CurrentGame>(System.IO.File.ReadAllText(CurrentFile)); }
                catch (JsonException) { return null; }
            }
        }

        private void WriteCurrent(CurrentGame game)
        {
            lock (StateLock)
            {
                System.IO.File.WriteAllText(CurrentFile, JsonSerializer.Serialize(game));
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

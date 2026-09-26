using System.Security.Claims;
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
    /// EXPERIMENT (experiment/game-streaming): gates the moonlight-web-stream bridge behind the
    /// Jellyfin login. Caddy serves the bridge at the stream host and calls these endpoints:
    ///   POST /jellyemu/stream/pass/{itemId} - signed-in user asks for a one-time pass
    ///   GET  /jellyemu/stream/login?pass=   - (via Caddy /jellyemu-auth) pass -> session cookie
    ///   GET  /jellyemu/stream/check         - Caddy forward_auth before every bridge request
    /// The stream target is read from {DataPath}/jellyemu-stream-test.url.
    /// </summary>
    public class JellyEmuStreamController : JellyEmuBaseController
    {
        // The bridge account Caddy signs requests in as. Its admin API is unreachable through
        // Caddy (see JellyEmuStreamAuth.IsAllowedRequest).
        private const string BridgeUser = "JellyEmuStream";
        private static readonly object KeyLock = new();

        public JellyEmuStreamController(
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            ILogger<JellyEmuStreamController> logger,
            JellyEmuEjsManager ejsManager,
            JellyEmuSessionService sessionService,
            IHttpClientFactory httpClientFactory)
            : base(libraryManager, appPaths, logger, ejsManager, sessionService, httpClientFactory) { }

        [HttpPost("/jellyemu/stream/pass/{itemId}")]
        [Authorize]
        public IActionResult Pass(string itemId)
        {
            var userId = User.FindFirstValue("Jellyfin-UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!IsValidId(itemId) || string.IsNullOrEmpty(userId)) return BadRequest();
            if (LibraryManager.GetItemById(itemId) == null) return NotFound();
            var target = ReadTarget();
            if (target == null) return StatusCode(StatusCodes.Status503ServiceUnavailable, "Streaming is not configured.");

            var now = DateTimeOffset.UtcNow;
            var pass = new JellyEmuStreamAuth.Claims("pass", userId, itemId, target.Value.HostId, target.Value.AppId,
                now.Add(JellyEmuStreamAuth.PassLifetime).ToUnixTimeSeconds(), JellyEmuStreamAuth.NewNonce());
            var token = JellyEmuStreamAuth.Sign(pass, GetKey());
            Logger.LogInformation("[JellyEmu] Stream pass issued for user {UserId} item {ItemId}", SanitizeForLog(userId), SanitizeForLog(itemId));
            return Ok(new { url = $"{target.Value.Origin}/jellyemu-auth?pass={Uri.EscapeDataString(token)}" });
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
            Logger.LogInformation("[JellyEmu] Stream session started for user {UserId} item {ItemId}", SanitizeForLog(claims.UserId), SanitizeForLog(claims.ItemId));
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

        private (string Origin, long HostId, long AppId)? ReadTarget()
        {
            var file = Path.Combine(AppPaths.DataPath, "jellyemu-stream-test.url");
            if (!System.IO.File.Exists(file)) return null;
            if (!Uri.TryCreate(System.IO.File.ReadAllText(file).Trim(), UriKind.Absolute, out var url)) return null;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query);
            if (!long.TryParse(query["hostId"], out var hostId) || !long.TryParse(query["appId"], out var appId)) return null;
            return (url.GetLeftPart(UriPartial.Authority), hostId, appId);
        }

        private byte[] GetKey()
        {
            var file = Path.Combine(AppPaths.DataPath, "jellyemu-stream.key");
            lock (KeyLock)
            {
                if (System.IO.File.Exists(file))
                {
                    var existing = System.IO.File.ReadAllBytes(file);
                    if (existing.Length >= 32) return existing;
                }
                var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                System.IO.File.WriteAllBytes(file, key);
                return key;
            }
        }
    }
}

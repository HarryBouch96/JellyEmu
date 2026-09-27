using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JellyEmu.Services
{
    /// <summary>
    /// EXPERIMENT (game streaming): signed passes and sessions that let a signed-in Jellyfin
    /// user through Caddy to the moonlight-web-stream bridge.
    ///
    /// Flow: the stream page (which has the user's Jellyfin token) asks for a pass; the pass
    /// is valid for 60 seconds and once only. Opening it on the stream host swaps it for a
    /// session cookie (12 hours). Caddy asks JellyEmu to check that cookie, and which bridge
    /// path is being requested, before every request reaches the bridge.
    /// </summary>
    public static class JellyEmuStreamAuth
    {
        public const string CookieName = "je_stream";
        public const string UserHeader = "X-JellyEmu-Stream-User";
        /// <summary>
        /// "host:port" of the bridge Caddy should send the request to, set by the check. Caddy must
        /// remove any copy sent by the client before asking (see the Caddy config in the Gaming PCs tab).
        /// </summary>
        public const string UpstreamHeader = "X-JellyEmu-Upstream";
        public static readonly TimeSpan PassLifetime = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

        private static readonly ConcurrentDictionary<string, DateTimeOffset> UsedPasses = new();

        /// <param name="Device">The gaming PC's id (host ids are only unique per bridge). Null in
        /// passes and sessions made before PCs had ids in them; those are matched by host id.</param>
        public record Claims(string Kind, string UserId, string ItemId, long HostId, long AppId, long Expires, string Nonce, string? Device = null);

        // ---- Signing ------------------------------------------------------------------------

        public static string Sign(Claims claims, byte[] key)
        {
            var payload = B64(JsonSerializer.SerializeToUtf8Bytes(claims));
            return payload + "." + B64(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload)));
        }

        /// <summary>Returns the claims if the signature is valid, the kind matches and it hasn't expired.</summary>
        public static Claims? Verify(string? token, string kind, byte[] key, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(token)) return null;
            var dot = token.IndexOf('.');
            if (dot <= 0 || dot == token.Length - 1) return null;
            var payload = token[..dot];
            byte[] given;
            try { given = UnB64(token[(dot + 1)..]); }
            catch (FormatException) { return null; }
            var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload));
            if (!CryptographicOperations.FixedTimeEquals(given, expected)) return null;

            Claims? claims;
            try { claims = JsonSerializer.Deserialize<Claims>(UnB64(payload)); }
            catch (Exception ex) when (ex is JsonException || ex is FormatException) { return null; }
            if (claims == null || claims.Kind != kind || claims.Expires < now.ToUnixTimeSeconds()) return null;
            return claims;
        }

        /// <summary>Marks a pass as used. Returns false if it was already used.</summary>
        public static bool ConsumePass(Claims pass, DateTimeOffset now)
        {
            foreach (var old in UsedPasses.Where(p => p.Value < now).Select(p => p.Key).ToList())
                UsedPasses.TryRemove(old, out _);
            return UsedPasses.TryAdd(pass.Nonce, DateTimeOffset.FromUnixTimeSeconds(pass.Expires));
        }

        public static string NewNonce() => B64(RandomNumberGenerator.GetBytes(16));

        // ---- What a stream session may request from the bridge ------------------------------

        private static readonly string[] BlockedPages = { "/", "/index.html", "/index.js", "/admin.html", "/admin.js" };

        /// <summary>
        /// The bridge requests a stream session may make: the stream page and its static files,
        /// "who am I" (authenticate/role) and the stream connection itself. Everything that
        /// manages users, roles, hosts, pairing or lists apps is refused.
        /// </summary>
        public static bool IsAllowedRequest(string method, string pathAndQuery)
        {
            var path = pathAndQuery.Split('?', 2)[0];
            // No encoded characters or backslashes: they could disguise a path (e.g. "/api%2Fusers")
            // from these checks, and the stream page never needs them.
            if (path.Contains("..", StringComparison.Ordinal) || path.Contains('%') || path.Contains('\\')) return false;
            var get = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.Equals("/api", StringComparison.OrdinalIgnoreCase))
            {
                return (get && (path.Equals("/api/authenticate", StringComparison.OrdinalIgnoreCase)
                             || path.Equals("/api/role", StringComparison.OrdinalIgnoreCase)
                             || path.Equals("/api/host/stream", StringComparison.OrdinalIgnoreCase)))
                    || (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
                        && path.Equals("/api/host/cancel", StringComparison.OrdinalIgnoreCase));
            }

            if (!get) return false;
            return !BlockedPages.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The bridge's stream connection (a WebSocket), which starts or resumes a game.</summary>
        public static bool IsStreamConnection(string pathAndQuery) =>
            pathAndQuery.Split('?', 2)[0].Equals("/api/host/stream", StringComparison.OrdinalIgnoreCase);

        private static string B64(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] UnB64(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + ((4 - s.Length % 4) % 4), '='));
        }
    }
}

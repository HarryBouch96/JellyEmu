using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace JellyEmu.Services
{
    /// <summary>
    /// EXPERIMENT (game streaming): gaming PCs games can be streamed from, and whether one can
    /// be used right now. Settings live in {DataPath}/jellyemu-stream.json (see JellyEmuStreamStore).
    /// </summary>
    public static class JellyEmuStreamDevices
    {
        public class Settings
        {
            /// <summary>
            /// Public HTTPS address every gaming PC's stream is served at (Caddy sends each request
            /// on to the right PC's bridge), e.g. https://stream.example.com.
            /// </summary>
            public string StreamOrigin { get; set; } = string.Empty;

            /// <summary>Where the game library is on the server, and how gaming PCs reach the same folder.</summary>
            public List<LibraryPath> LibraryPaths { get; set; } = new();

            public List<Device> Devices { get; set; } = new();
        }

        public class LibraryPath
        {
            /// <summary>Folder as Jellyfin sees it, e.g. /media/games/.</summary>
            public string Server { get; set; } = string.Empty;
            /// <summary>The same folder from a gaming PC, e.g. \\nas\games\.</summary>
            public string Pc { get; set; } = string.Empty;
        }

        public class Device
        {
            /// <summary>Short id used in URLs, e.g. "laptop".</summary>
            public string Id { get; set; } = string.Empty;
            /// <summary>Shown in the "Play on" picker, e.g. "Gaming laptop".</summary>
            public string Name { get; set; } = string.Empty;
            /// <summary>
            /// Public HTTPS origin of its bridge through Caddy. Empty: the shared
            /// <see cref="Settings.StreamOrigin"/> (PCs added with the setup script).
            /// </summary>
            public string StreamOrigin { get; set; } = string.Empty;
            /// <summary>Its bridge on the LAN, for status checks, quitting games and Caddy's upstream.</summary>
            public string BridgeUrl { get; set; } = string.Empty;
            public long HostId { get; set; }
            public long AppId { get; set; }
            /// <summary>Platforms it has emulators (and BIOS files) for, e.g. "PlayStation 2".</summary>
            public List<string> Platforms { get; set; } = new();

            /// <summary>
            /// SHA-256 (hex) of this PC's own key, given to it by the setup script. Empty for PCs set up
            /// by hand, which use the shared launcher key instead.
            /// </summary>
            public string KeyHash { get; set; } = string.Empty;
            /// <summary>When the PC last checked in (at setup and every start-up).</summary>
            public DateTimeOffset? LastCheckIn { get; set; }
            /// <summary>Version of the JellyEmu setup on the PC.</summary>
            public string Version { get; set; } = string.Empty;

            public bool Supports(string platform) => Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);

            /// <summary>Whether the PC was added with the setup script (and so has its own key).</summary>
            public bool Managed => !string.IsNullOrEmpty(KeyHash);

            public string OriginIn(Settings settings) =>
                (string.IsNullOrEmpty(StreamOrigin) ? settings.StreamOrigin : StreamOrigin).TrimEnd('/');
        }

        /// <summary>What a status check of the device's bridge found.</summary>
        public enum Probe
        {
            /// <summary>The bridge didn't answer: the PC is off, asleep or away.</summary>
            Offline,
            /// <summary>The bridge answered but its Sunshine didn't (or isn't paired).</summary>
            HostUnavailable,
            Free,
            /// <summary>Sunshine is running a game.</summary>
            Busy
        }

        public record Status(bool Available, string Reason);

        /// <summary>
        /// A stream page checks in every 15 seconds (see the stream page's heartbeat); a PC whose
        /// stream hasn't checked in for this long is treated as free (e.g. the app was closed).
        /// </summary>
        public static readonly TimeSpan BusyWindow = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Can <paramref name="userId"/> play <paramref name="platform"/> on this device now?
        /// One player per gaming PC: while any stream on it is open (recent heartbeat), it's in
        /// use, even for the same user on another screen. A game left running by a stream that
        /// has gone away doesn't block it; that game gets quit to make room.
        /// </summary>
        public static Status Evaluate(Device device, string platform, Probe probe, string? currentUserId,
            DateTimeOffset currentLastSeen, string userId, DateTimeOffset now)
        {
            if (!device.Supports(platform)) return new Status(false, "Can't play this system");
            switch (probe)
            {
                case Probe.Offline: return new Status(false, "Offline");
                case Probe.HostUnavailable: return new Status(false, "Not ready");
            }
            if (currentUserId != null && now - currentLastSeen < BusyWindow)
                return new Status(false, currentUserId == userId ? "In use on another screen" : "In use by someone else");
            return new Status(true, string.Empty);
        }

        // ---- Gaming PCs added by the setup script ------------------------------------------------

        public static string HashKey(string key) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

        /// <summary>Whether <paramref name="key"/> is this PC's own key.</summary>
        public static bool KeyMatches(Device device, string? key)
        {
            if (!device.Managed || string.IsNullOrEmpty(key)) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(HashKey(key)), Encoding.ASCII.GetBytes(device.KeyHash));
        }

        /// <summary>
        /// A bridge address a gaming PC may register: plain http to an IP address on a private
        /// network (home LAN or Tailscale), on an unprivileged port. Caddy sends stream traffic
        /// there, so a PC must not be able to point it anywhere else (the internet, the server
        /// itself, other ports of other machines' services below 1024).
        /// </summary>
        public static bool IsAllowedBridgeUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            if (uri.Port < 1024 || uri.Port > 65535) return false;
            if (!IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // Tailscale (CGNAT range)
        }

        /// <summary>"host:port" of the device's bridge, for Caddy's reverse_proxy.</summary>
        public static string? Upstream(Device device) =>
            Uri.TryCreate(device.BridgeUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? $"{uri.Host}:{uri.Port}"
                : null;

        /// <summary>
        /// A game's path on the server as seen from a gaming PC (longest matching library path),
        /// or null when no library path covers it.
        /// </summary>
        public static string? ToPcPath(Settings settings, string serverPath)
        {
            var match = settings.LibraryPaths
                .Where(p => !string.IsNullOrEmpty(p.Server) && !string.IsNullOrEmpty(p.Pc))
                .Select(p => (Server: p.Server.EndsWith('/') ? p.Server : p.Server + "/", p.Pc))
                .Where(p => serverPath.StartsWith(p.Server, StringComparison.Ordinal))
                .OrderByDescending(p => p.Server.Length)
                .FirstOrDefault();
            if (match.Server == null) return null;
            var pc = match.Pc.EndsWith('\\') ? match.Pc : match.Pc + "\\";
            var rest = serverPath.Substring(match.Server.Length);
            if (rest.Split('/').Any(part => part == "..")) return null;
            return pc + rest.Replace('/', '\\');
        }

        public static bool IsValidDeviceId(string? id) =>
            !string.IsNullOrEmpty(id) && id.Length <= 32 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        /// <summary>A URL-safe id from a PC's name, unique among <paramref name="taken"/>.</summary>
        public static string NewDeviceId(string name, IEnumerable<string> taken)
        {
            var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
            while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
            slug = slug.Trim('-');
            if (slug.Length > 24) slug = slug[..24].TrimEnd('-');
            if (slug.Length == 0) slug = "pc";
            var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            var id = slug;
            for (var n = 2; set.Contains(id); n++) id = $"{slug}-{n}";
            return id;
        }
    }
}

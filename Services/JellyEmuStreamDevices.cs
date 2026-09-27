namespace JellyEmu.Services
{
    /// <summary>
    /// EXPERIMENT (game streaming): gaming PCs games can be streamed from, and whether one can
    /// be used right now. Settings live in {DataPath}/jellyemu-stream.json.
    /// </summary>
    public static class JellyEmuStreamDevices
    {
        public class Settings
        {
            public List<Device> Devices { get; set; } = new();
        }

        public class Device
        {
            /// <summary>Short id used in URLs, e.g. "laptop".</summary>
            public string Id { get; set; } = string.Empty;
            /// <summary>Shown in the "Play on" picker, e.g. "Gaming laptop".</summary>
            public string Name { get; set; } = string.Empty;
            /// <summary>Public HTTPS origin of its bridge through Caddy, e.g. https://stream.example.com.</summary>
            public string StreamOrigin { get; set; } = string.Empty;
            /// <summary>Its bridge on the LAN, for status checks and quitting games.</summary>
            public string BridgeUrl { get; set; } = string.Empty;
            public long HostId { get; set; }
            public long AppId { get; set; }
            /// <summary>Platforms it has emulators for, e.g. "PlayStation 2".</summary>
            public List<string> Platforms { get; set; } = new();

            public bool Supports(string platform) => Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
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
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Settings = JellyEmu.Services.JellyEmuStreamDevices.Settings;

namespace JellyEmu.Services
{
    /// <summary>
    /// EXPERIMENT (game streaming): reads and writes the gaming PC settings
    /// ({DataPath}/jellyemu-stream.json), and hands out one-time setup codes for new PCs.
    /// </summary>
    public static class JellyEmuStreamStore
    {
        private static readonly object FileLock = new();
        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static readonly TimeSpan SetupCodeLifetime = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, (string Name, DateTimeOffset Expires)> SetupCodes = new();

        private static string SettingsFile(IApplicationPaths paths) => Path.Combine(paths.DataPath, "jellyemu-stream.json");

        /// <summary>The settings, with unusable devices left out (null when there's no settings file or it's unreadable).</summary>
        public static Settings? Read(IApplicationPaths paths)
        {
            var s = ReadRaw(paths);
            if (s == null) return null;
            s.Devices = s.Devices.Where(d => JellyEmuStreamDevices.IsValidDeviceId(d.Id)
                                             && !string.IsNullOrEmpty(d.OriginIn(s))
                                             && !string.IsNullOrEmpty(d.BridgeUrl)).ToList();
            return s;
        }

        /// <summary>The settings as stored, including PCs still being set up.</summary>
        public static Settings? ReadRaw(IApplicationPaths paths)
        {
            lock (FileLock)
            {
                var file = SettingsFile(paths);
                if (!File.Exists(file)) return null;
                try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), ReadOptions); }
                catch (JsonException) { return null; }
            }
        }

        /// <summary>Changes the settings under a lock and saves them (written to a new file, then swapped in).</summary>
        public static T Update<T>(IApplicationPaths paths, Func<Settings, T> change)
        {
            lock (FileLock)
            {
                var file = SettingsFile(paths);
                Settings settings;
                if (File.Exists(file))
                {
                    // A settings file that can't be read must not be replaced by an empty one.
                    settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), ReadOptions)
                               ?? throw new InvalidOperationException("The streaming settings file is empty.");
                }
                else
                {
                    settings = new Settings();
                }
                var result = change(settings);
                var temp = file + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, WriteOptions));
                File.Move(temp, file, overwrite: true);
                return result;
            }
        }

        // ---- Setup codes ------------------------------------------------------------------------

        // No 0/O, 1/I/L: the code may be read out or typed.
        private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        /// <summary>A new one-time code for setting up a PC named <paramref name="name"/>, e.g. "K7QM-4TXP".</summary>
        public static (string Code, DateTimeOffset Expires) NewSetupCode(string name, DateTimeOffset now)
        {
            foreach (var old in SetupCodes.Where(c => c.Value.Expires < now).Select(c => c.Key).ToList())
                SetupCodes.TryRemove(old, out _);
            var chars = Enumerable.Range(0, 8).Select(_ => CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)]).ToArray();
            var code = new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
            var expires = now.Add(SetupCodeLifetime);
            SetupCodes[code] = (name, expires);
            return (code, expires);
        }

        /// <summary>Uses up a setup code. Returns the PC name it was made for, or null if it's unknown or expired.</summary>
        public static string? ConsumeSetupCode(string? code, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var normalised = code.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
            if (normalised.Length == 8) normalised = normalised[..4] + "-" + normalised[4..];
            if (!SetupCodes.TryRemove(normalised, out var entry)) return null;
            return entry.Expires >= now ? entry.Name : null;
        }

        /// <summary>A new random key for a PC (only its hash is stored).</summary>
        public static string NewPcKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

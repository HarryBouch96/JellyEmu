using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyEmu.Services
{
    /// <summary>
    /// Keeps saves attached to a game when its file moves.
    ///
    /// Jellyfin derives an item's ID from its path, so moving or renaming a ROM
    /// (e.g. into its own folder) gives it a new ID, and saves, which are stored
    /// by item ID, are left behind. This service records a fingerprint of each
    /// game's ROM file contents (so renames don't matter) in jellyemu-save-index.json. When a
    /// game is launched, saves belonging to a recorded game that no longer exists
    /// in the library but has the same fingerprint are moved across, along with
    /// its playtime.
    /// </summary>
    public class JellyEmuSaveLinkService
    {
        private static readonly object IndexLock = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly ILibraryManager? _libraryManager;
        private readonly IApplicationPaths _appPaths;
        private readonly JellyEmuFileService? _fileService;
        private readonly IJellyEmuCacheService? _cacheService;
        private readonly ILogger<JellyEmuSaveLinkService> _logger;

        public JellyEmuSaveLinkService(
            ILibraryManager? libraryManager,
            IApplicationPaths appPaths,
            JellyEmuFileService? fileService,
            JellyEmuCacheService? cacheService,
            ILogger<JellyEmuSaveLinkService> logger)
        {
            _libraryManager = libraryManager;
            _appPaths = appPaths;
            _fileService = fileService;
            _cacheService = cacheService;
            _logger = logger;
        }

        /// <summary>One recorded game in the index.</summary>
        public class IndexEntry
        {
            public string Fingerprint { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public DateTime Updated { get; set; }
        }

        private string SavesDir => Path.Combine(_appPaths.DataPath, "jellyemu-saves");
        private string IndexPath => Path.Combine(_appPaths.DataPath, "jellyemu-save-index.json");
        private string PlaytimeDbPath => Path.Combine(_appPaths.DataPath, "jellyemu-playtime.db");

        // ---- Library-facing entry points ------------------------------------------------

        /// <summary>
        /// Called when a game is launched: records its fingerprint and moves any saves
        /// left behind by a previous library item for the same ROM. Never throws.
        /// </summary>
        public void OnGameLaunch(BaseItem item)
        {
            try
            {
                var itemId = item.Id.ToString("N");
                var fingerprint = ComputeFingerprint(item.Path);
                if (fingerprint == null) return;

                foreach (var orphanId in FindOrphans(itemId, fingerprint, ItemExists))
                {
                    MoveSaves(orphanId, itemId);
                    Forget(orphanId);
                }
                Remember(itemId, fingerprint, item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JellyEmu] Save link check failed for {Name}", item.Name);
            }
        }

        /// <summary>
        /// Records (or refreshes) fingerprints for every game in the library that has
        /// saves, so saves made before a game was ever launched with this feature can
        /// follow a later move. Never throws.
        /// </summary>
        public void BackfillFromSaves()
        {
            try
            {
                var added = 0;
                foreach (var itemId in ItemIdsWithSaves())
                {
                    if (_libraryManager == null) break;
                    if (!Guid.TryParseExact(itemId, "N", out var guid)) continue;
                    var item = _libraryManager.GetItemById(guid);
                    if (item == null) continue;
                    var fingerprint = ComputeFingerprint(item.Path);
                    if (fingerprint == null) continue;
                    if (Remember(itemId, fingerprint, item.Name)) added++;
                }
                PruneOutdatedEntries();
                if (added > 0)
                    _logger.LogInformation("[JellyEmu] Recorded ROM fingerprints for {Count} games with saves", added);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[JellyEmu] Save link backfill failed");
            }
        }

        private bool ItemExists(string itemId)
        {
            if (_libraryManager == null) return true; // cannot tell, so never treat as orphaned
            return Guid.TryParseExact(itemId, "N", out var guid) && _libraryManager.GetItemById(guid) != null;
        }

        /// <summary>
        /// Fingerprint of the ROM files behind an item path (file, folder or .j3u
        /// playlist): their names and sizes, ignoring artwork and metadata.
        /// Null when no ROM files are found.
        /// </summary>
        public string? ComputeFingerprint(string? itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath) || _fileService == null) return null;
            return ComputeFingerprint(_fileService.ResolveAllRomFiles(itemPath));
        }

        // ---- Core logic (independent of the Jellyfin library, unit tested) --------------

        // Text sheets name the files they point to, so they change when those are renamed.
        private static readonly HashSet<string> SheetExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cue", ".m3u", ".gdi", ".ccd", ".j3u"
        };

        private const int SampleCount = 8;
        private const int SampleSize = 16 * 1024;
        private const string FingerprintVersion = "v2:";

        /// <summary>
        /// Content fingerprint of a game's files, independent of their names and
        /// location: each file's size plus a hash of evenly spaced samples of its
        /// contents (the whole file when small). Cheap even for large disc images.
        /// </summary>
        public static string? ComputeFingerprint(IEnumerable<string> files)
        {
            var parts = files
                .Where(JellyEmuFileService.IsRomLikeFile)
                .Where(f => !SheetExtensions.Contains(Path.GetExtension(f)))
                .Select(FileFingerprint)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (parts.Count == 0) return null;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
            return FingerprintVersion + Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string FileFingerprint(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[SampleSize];

            if (length <= (long)SampleCount * SampleSize)
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, read);
            }
            else
            {
                var lastStart = length - SampleSize;
                for (var i = 0; i < SampleCount; i++)
                {
                    stream.Position = lastStart * i / (SampleCount - 1);
                    stream.ReadExactly(buffer, 0, SampleSize);
                    sha.AppendData(buffer, 0, SampleSize);
                }
            }
            return length + ":" + Convert.ToHexString(sha.GetHashAndReset());
        }

        /// <summary>
        /// Recorded games with the same fingerprint that no longer exist in the library.
        /// </summary>
        public List<string> FindOrphans(string itemId, string fingerprint, Func<string, bool> itemExists)
        {
            return LoadIndex()
                .Where(e => e.Key != itemId && e.Value.Fingerprint == fingerprint)
                .Select(e => e.Key)
                .Where(id => !itemExists(id))
                .ToList();
        }

        /// <summary>Records a game's fingerprint. Returns false when it was already up to date.</summary>
        public bool Remember(string itemId, string fingerprint, string? name)
        {
            lock (IndexLock)
            {
                var index = LoadIndex();
                if (index.TryGetValue(itemId, out var existing) && existing.Fingerprint == fingerprint && existing.Name == (name ?? string.Empty))
                    return false;
                index[itemId] = new IndexEntry { Fingerprint = fingerprint, Name = name ?? string.Empty, Updated = DateTime.UtcNow };
                SaveIndex(index);
                return true;
            }
        }

        /// <summary>
        /// Drops entries from an older fingerprint format for games no longer in the
        /// library: they can never be matched again (existing games are refreshed).
        /// </summary>
        private void PruneOutdatedEntries()
        {
            lock (IndexLock)
            {
                var index = LoadIndex();
                var outdated = index
                    .Where(e => !e.Value.Fingerprint.StartsWith(FingerprintVersion, StringComparison.Ordinal) && !ItemExists(e.Key))
                    .Select(e => e.Key)
                    .ToList();
                if (outdated.Count == 0) return;
                outdated.ForEach(id => index.Remove(id));
                SaveIndex(index);
            }
        }

        public void Forget(string itemId)
        {
            lock (IndexLock)
            {
                var index = LoadIndex();
                if (index.Remove(itemId)) SaveIndex(index);
            }
        }

        /// <summary>
        /// Moves every save file of <paramref name="fromId"/> (all users, all slots:
        /// states, in-game saves, screenshots) to <paramref name="toId"/>, and merges its
        /// playtime. A file the new ID already has is never overwritten; the old one is
        /// left in place and logged. Returns the number of files moved.
        /// </summary>
        public int MoveSaves(string fromId, string toId)
        {
            var moved = 0;
            var skipped = 0;
            if (Directory.Exists(SavesDir))
            {
                foreach (var userDir in Directory.GetDirectories(SavesDir))
                {
                    foreach (var slotDir in Directory.GetDirectories(userDir, "slot*"))
                    {
                        foreach (var file in Directory.GetFiles(slotDir, fromId + ".*"))
                        {
                            var suffix = Path.GetFileName(file).Substring(fromId.Length); // e.g. ".sav"
                            var dest = Path.Combine(slotDir, toId + suffix);
                            if (File.Exists(dest))
                            {
                                skipped++;
                                _logger.LogWarning("[JellyEmu] Not moving {File}: the moved game already has {Dest}", file, dest);
                                continue;
                            }
                            File.Move(file, dest);
                            moved++;
                        }
                    }
                    var userId = Path.GetFileName(userDir);
                    _cacheService?.Evict(JellyEmuCacheKeys.UserSaves(userId));
                }
            }

            MergePlaytime(fromId, toId);

            foreach (var id in new[] { fromId, toId })
            {
                _cacheService?.EvictByPrefix($"save:{id}:");
                _cacheService?.EvictByPrefix($"sram:{id}:");
                _cacheService?.EvictByPrefix($"saveslots:{id}:");
            }

            _logger.LogInformation("[JellyEmu] Moved {Moved} save files from item {From} to {To} (game file moved); {Skipped} left in place",
                moved, fromId, toId, skipped);
            return moved;
        }

        private void MergePlaytime(string fromId, string toId)
        {
            if (!File.Exists(PlaytimeDbPath)) return;
            try
            {
                using var connection = new SqliteConnection($"Data Source={PlaytimeDbPath}");
                connection.Open();
                using var transaction = connection.BeginTransaction();

                using (var merge = connection.CreateCommand())
                {
                    merge.Transaction = transaction;
                    merge.CommandText =
                        @"INSERT INTO Playtime (UserId, ItemId, Seconds)
                          SELECT UserId, $to, Seconds FROM Playtime WHERE ItemId = $from
                          ON CONFLICT(UserId, ItemId) DO UPDATE SET Seconds = Seconds + excluded.Seconds;";
                    merge.Parameters.AddWithValue("$from", fromId);
                    merge.Parameters.AddWithValue("$to", toId);
                    merge.ExecuteNonQuery();
                }
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM Playtime WHERE ItemId = $from;";
                    delete.Parameters.AddWithValue("$from", fromId);
                    delete.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch (SqliteException ex)
            {
                _logger.LogWarning(ex, "[JellyEmu] Could not merge playtime from item {From} to {To}", fromId, toId);
            }
        }

        /// <summary>Item IDs that have at least one save file for any user.</summary>
        public HashSet<string> ItemIdsWithSaves()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(SavesDir)) return ids;
            foreach (var userDir in Directory.GetDirectories(SavesDir))
            {
                foreach (var slotDir in Directory.GetDirectories(userDir, "slot*"))
                {
                    foreach (var file in Directory.GetFiles(slotDir))
                    {
                        var name = Path.GetFileName(file);
                        var dot = name.IndexOf('.');
                        if (dot > 0) ids.Add(name.Substring(0, dot));
                    }
                }
            }
            return ids;
        }

        private Dictionary<string, IndexEntry> LoadIndex()
        {
            lock (IndexLock)
            {
                try
                {
                    if (!File.Exists(IndexPath)) return new();
                    return JsonSerializer.Deserialize<Dictionary<string, IndexEntry>>(File.ReadAllText(IndexPath)) ?? new();
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException)
                {
                    _logger.LogWarning(ex, "[JellyEmu] Could not read {Path}; starting a new save index", IndexPath);
                    return new();
                }
            }
        }

        private void SaveIndex(Dictionary<string, IndexEntry> index)
        {
            var tempPath = IndexPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(index, JsonOptions));
            File.Move(tempPath, IndexPath, overwrite: true);
        }
    }

    /// <summary>
    /// Records ROM fingerprints for games that already have saves, shortly after startup.
    /// </summary>
    public class JellyEmuSaveLinkStartup : IHostedService
    {
        private readonly JellyEmuSaveLinkService _saveLinks;

        public JellyEmuSaveLinkStartup(JellyEmuSaveLinkService saveLinks)
        {
            _saveLinks = saveLinks;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Give the library a moment to finish loading before looking items up.
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }
                _saveLinks.BackfillFromSaves();
            }, cancellationToken);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

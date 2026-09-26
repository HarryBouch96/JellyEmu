using System;
using System.IO;
using JellyEmu.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyEmu.Tests
{
    public class SaveLinkServiceTests : IDisposable
    {
        private const string OldId = "76c7e4628a9c991c9d001274207ad051";
        private const string NewId = "1231424c598ff304a72067d87f067c09";
        private const string UserA = "c069e3aceb5e4290a08913215870004c";
        private const string UserB = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private readonly string _root;
        private readonly string _data;
        private readonly JellyEmuSaveLinkService _service;

        public SaveLinkServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "jellyemu-savelink-" + Guid.NewGuid().ToString("N"));
            _data = Path.Combine(_root, "data");
            Directory.CreateDirectory(_data);
            var appPaths = new MockAppPaths(_data);
            var fileService = new JellyEmuFileService(null!, appPaths, NullLogger<JellyEmuFileService>.Instance);
            _service = new JellyEmuSaveLinkService(null, appPaths, fileService, null, NullLogger<JellyEmuSaveLinkService>.Instance);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string WriteFile(string relativePath, int size, byte fill = 1)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new byte[size];
            Array.Fill(bytes, fill);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private string WriteSave(string userId, int slot, string fileName, byte fill)
        {
            return WriteFile(Path.Combine("data", "jellyemu-saves", userId, "slot" + slot, fileName), 64, fill);
        }

        [Fact]
        public void Fingerprint_SameRom_MovedIntoOwnFolderWithArtwork_Matches()
        {
            var inRoot = WriteFile(Path.Combine("lib-before", "Harry Potter.chd"), 1000);
            var folder = Path.Combine(_root, "lib-after", "Harry Potter");
            WriteFile(Path.Combine("lib-after", "Harry Potter", "Harry Potter.chd"), 1000);
            WriteFile(Path.Combine("lib-after", "Harry Potter", "cover.jpg"), 50);
            WriteFile(Path.Combine("lib-after", "Harry Potter", ".DS_Store"), 10);

            var before = _service.ComputeFingerprint(inRoot);
            var after = _service.ComputeFingerprint(folder);

            Assert.NotNull(before);
            Assert.Equal(before, after);
        }

        [Fact]
        public void Fingerprint_MovedAndRenamed_Matches()
        {
            // The Pokemon Sapphire case: moved into its own folder *and* renamed.
            var before = WriteFile(Path.Combine("GBA", "Pokemon - Sapphire Version (USA, Europe).gba"), 16 * 1024 * 1024, 7);
            WriteFile(Path.Combine("GBA-after", "Pokemon Sapphire Version [igdb-1515]", "Pokemon Sapphire Version.gba"), 16 * 1024 * 1024, 7);

            Assert.Equal(
                _service.ComputeFingerprint(before),
                _service.ComputeFingerprint(Path.Combine(_root, "GBA-after", "Pokemon Sapphire Version [igdb-1515]")));
        }

        [Fact]
        public void Fingerprint_DifferentSizeOrContents_DoesNotMatch()
        {
            var a = WriteFile(Path.Combine("a", "game.gba"), 1000, 1);
            var b = WriteFile(Path.Combine("b", "game.gba"), 1001, 1);
            var c = WriteFile(Path.Combine("c", "game.gba"), 1000, 2);

            Assert.NotEqual(_service.ComputeFingerprint(a), _service.ComputeFingerprint(b));
            Assert.NotEqual(_service.ComputeFingerprint(a), _service.ComputeFingerprint(c));
        }

        [Fact]
        public void Fingerprint_LargeFile_DetectsChangeInsideASample()
        {
            var size = 4 * 1024 * 1024;
            var a = WriteFile(Path.Combine("a", "disc.bin"), size, 3);
            var b = WriteFile(Path.Combine("b", "disc.bin"), size, 3);
            var before = _service.ComputeFingerprint(a);
            Assert.Equal(before, _service.ComputeFingerprint(b));

            using (var stream = new FileStream(b, FileMode.Open, FileAccess.Write))
            {
                stream.Position = size - 1; // inside the last sample
                stream.WriteByte(4);
            }
            Assert.NotEqual(before, _service.ComputeFingerprint(b));
        }

        [Fact]
        public void Fingerprint_IgnoresCueSheets_SoRenamedDiscImagesMatch()
        {
            var before = Path.Combine(_root, "PS1", "Theme Park World (Europe) (En,Fr,De,Es,It,Nl,Sv)");
            WriteFile(Path.Combine(before, "Theme Park World (Europe) (En,Fr,De,Es,It,Nl,Sv).bin"), 300000, 5);
            File.WriteAllText(Path.Combine(before, "Theme Park World (Europe) (En,Fr,De,Es,It,Nl,Sv).cue"), "FILE \"Theme Park World (Europe) (En,Fr,De,Es,It,Nl,Sv).bin\" BINARY");
            var after = Path.Combine(_root, "PS1", "Theme Park World [igdb-12484]");
            WriteFile(Path.Combine(after, "Theme Park World.bin"), 300000, 5);
            File.WriteAllText(Path.Combine(after, "Theme Park World.cue"), "FILE \"Theme Park World.bin\" BINARY");

            Assert.Equal(_service.ComputeFingerprint(before), _service.ComputeFingerprint(after));
        }

        [Fact]
        public void Fingerprint_NoRomFiles_IsNull()
        {
            WriteFile(Path.Combine("art-only", "cover.png"), 10);
            Assert.Null(_service.ComputeFingerprint(Path.Combine(_root, "art-only")));
            Assert.Null(_service.ComputeFingerprint(Path.Combine(_root, "missing.gba")));
        }

        [Fact]
        public void FindOrphans_OnlyReturnsMissingItemsWithSameFingerprint()
        {
            _service.Remember(OldId, "fp1", "Harry Potter");
            _service.Remember("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "fp1", "Harry Potter (copy still in library)");
            _service.Remember("cccccccccccccccccccccccccccccccc", "fp2", "Other game");

            var orphans = _service.FindOrphans(NewId, "fp1", id => id == "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

            Assert.Equal(new[] { OldId }, orphans);
        }

        [Fact]
        public void MoveSaves_MovesAllFilesForAllUsers_AndNeverOverwrites()
        {
            WriteSave(UserA, 100, OldId + ".sav", 1);
            WriteSave(UserA, 1, OldId + ".state", 2);
            WriteSave(UserA, 1, OldId + ".screenshot.json", 3);
            WriteSave(UserB, 100, OldId + ".sav", 4);
            WriteSave(UserB, 100, NewId + ".sav", 9);            // already has one: must be kept
            WriteSave(UserA, 100, "unrelatedunrelatedunrelatedunrel.sav", 5);

            var moved = _service.MoveSaves(OldId, NewId);

            var saves = Path.Combine(_data, "jellyemu-saves");
            Assert.Equal(3, moved);
            Assert.Equal(1, File.ReadAllBytes(Path.Combine(saves, UserA, "slot100", NewId + ".sav"))[0]);
            Assert.True(File.Exists(Path.Combine(saves, UserA, "slot1", NewId + ".state")));
            Assert.True(File.Exists(Path.Combine(saves, UserA, "slot1", NewId + ".screenshot.json")));
            Assert.False(File.Exists(Path.Combine(saves, UserA, "slot100", OldId + ".sav")));
            // Conflict: the new ID's save is untouched and the old one stays where it was.
            Assert.Equal(9, File.ReadAllBytes(Path.Combine(saves, UserB, "slot100", NewId + ".sav"))[0]);
            Assert.True(File.Exists(Path.Combine(saves, UserB, "slot100", OldId + ".sav")));
            Assert.True(File.Exists(Path.Combine(saves, UserA, "slot100", "unrelatedunrelatedunrelatedunrel.sav")));
        }

        [Fact]
        public void MoveSaves_IdenticalDuplicate_IsRemoved()
        {
            WriteSave(UserA, 100, OldId + ".sav", 6);
            WriteSave(UserA, 100, NewId + ".sav", 6);

            var moved = _service.MoveSaves(OldId, NewId);

            var slot = Path.Combine(_data, "jellyemu-saves", UserA, "slot100");
            Assert.Equal(1, moved);
            Assert.False(File.Exists(Path.Combine(slot, OldId + ".sav")));
            Assert.Equal(6, File.ReadAllBytes(Path.Combine(slot, NewId + ".sav"))[0]);
        }

        [Fact]
        public void MoveSaves_MergesPlaytime()
        {
            var dbPath = Path.Combine(_data, "jellyemu-playtime.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    @"CREATE TABLE Playtime (UserId TEXT NOT NULL, ItemId TEXT NOT NULL, Seconds INTEGER NOT NULL, PRIMARY KEY (UserId, ItemId));
                      INSERT INTO Playtime VALUES ('" + UserA + "', '" + OldId + "', 600);" +
                    "INSERT INTO Playtime VALUES ('" + UserA + "', '" + NewId + "', 60);" +
                    "INSERT INTO Playtime VALUES ('" + UserB + "', '" + OldId + "', 30);";
                cmd.ExecuteNonQuery();
            }

            _service.MoveSaves(OldId, NewId);

            using var check = new SqliteConnection($"Data Source={dbPath}");
            check.Open();
            long Seconds(string user, string item)
            {
                using var q = check.CreateCommand();
                q.CommandText = "SELECT COALESCE(SUM(Seconds), -1) FROM Playtime WHERE UserId = $u AND ItemId = $i";
                q.Parameters.AddWithValue("$u", user);
                q.Parameters.AddWithValue("$i", item);
                var result = q.ExecuteScalar();
                return result is long l ? l : -1;
            }
            Assert.Equal(660, Seconds(UserA, NewId));
            Assert.Equal(30, Seconds(UserB, NewId));
            Assert.Equal(-1, Seconds(UserA, OldId));
        }

        [Fact]
        public void ItemIdsWithSaves_ListsItemsAcrossUsersAndSlots()
        {
            WriteSave(UserA, 100, OldId + ".sav", 1);
            WriteSave(UserB, 2, NewId + ".screenshot.json", 1);

            var ids = _service.ItemIdsWithSaves();

            Assert.Contains(OldId, ids);
            Assert.Contains(NewId, ids);
            Assert.Equal(2, ids.Count);
        }

        [Fact]
        public void Remember_PersistsAcrossInstances()
        {
            _service.Remember(OldId, "fp1", "Harry Potter");

            var appPaths = new MockAppPaths(_data);
            var reloaded = new JellyEmuSaveLinkService(null, appPaths, null, null, NullLogger<JellyEmuSaveLinkService>.Instance);

            Assert.Equal(new[] { OldId }, reloaded.FindOrphans(NewId, "fp1", _ => false));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellyEmu.Controllers;
using JellyEmu.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyEmu.Tests
{
    /// <summary>
    /// ROMs stored in per-game folders (e.g. Games/PS1/Harry Potter/Harry Potter.chd) are
    /// served as a ZIP of the folder. See upstream issue #229.
    /// </summary>
    public class RomZipTests : IDisposable
    {
        private readonly string _root;
        private readonly string _tempDir;

        public RomZipTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "jellyemu-romzip-" + Guid.NewGuid().ToString("N"));
            _tempDir = Path.Combine(_root, "tmp");
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private string CreateFile(string relativePath, byte[] content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        private static byte[] Bytes(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static Dictionary<string, byte[]> ReadZip(byte[] zipBytes)
        {
            using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
            return archive.Entries.ToDictionary(e => e.FullName, e =>
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            });
        }

        /// <summary>Mimics Kestrel's response body: synchronous writes are rejected.</summary>
        private sealed class AsyncOnlyStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new InvalidOperationException("Synchronous operations are disallowed.");
            public override void Write(ReadOnlySpan<byte> buffer) =>
                throw new InvalidOperationException("Synchronous operations are disallowed.");
            public override void WriteByte(byte value) =>
                throw new InvalidOperationException("Synchronous operations are disallowed.");
            public override void Flush() =>
                throw new InvalidOperationException("Synchronous operations are disallowed.");
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                base.Write(buffer, offset, count);
                return Task.CompletedTask;
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var array = buffer.ToArray();
                base.Write(array, 0, array.Length);
                return ValueTask.CompletedTask;
            }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        [Fact]
        public async Task ZipArchiveDirectlyOnAsyncOnlyStream_Throws_ReproducingTheBug()
        {
            var body = new AsyncOnlyStream();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                using (var archive = new ZipArchive(body, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("Harry Potter.chd", CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(Bytes(1024, 1));
                }
            });
        }

        [Fact]
        public async Task BuildRomZip_RomInSubfolder_CopiesToAsyncOnlyResponse()
        {
            var gameDir = Path.Combine(_root, "Games", "PS1", "Harry Potter");
            var chd = Bytes(256 * 1024, 2);
            var chdPath = CreateFile(Path.Combine("Games", "PS1", "Harry Potter", "Harry Potter.chd"), chd);

            var body = new AsyncOnlyStream();
            await using (var zip = await JellyEmuRomController.BuildRomZipAsync(
                new[] { chdPath }, gameDir, _tempDir, CancellationToken.None))
            {
                Assert.Equal(0, zip.Position);
                Assert.True(zip.Length > 0);
                await zip.CopyToAsync(body);
            }

            var entries = ReadZip(body.ToArray());
            Assert.Single(entries);
            Assert.Equal(chd, entries["Harry Potter.chd"]);
        }

        [Fact]
        public async Task BuildRomZip_PreservesNestedRelativePathsAndFilenames()
        {
            var gameDir = Path.Combine(_root, "Games", "PS1", "Final Fantasy VII (USA)");
            var cue = CreateFile(Path.Combine("Games", "PS1", "Final Fantasy VII (USA)", "Final Fantasy VII (USA).m3u"), Bytes(64, 3));
            var disc1 = CreateFile(Path.Combine("Games", "PS1", "Final Fantasy VII (USA)", "Discs", "Disc 1.chd"), Bytes(4096, 4));
            var disc2 = CreateFile(Path.Combine("Games", "PS1", "Final Fantasy VII (USA)", "Discs", "Disc 2 & Extras.chd"), Bytes(4096, 5));

            var service = new JellyEmuFileService(null!, new MockAppPaths(_root), NullLogger<JellyEmuFileService>.Instance);
            var files = service.ResolveAllRomFiles(gameDir);
            Assert.Equal(3, files.Count);

            byte[] zipBytes;
            await using (var zip = await JellyEmuRomController.BuildRomZipAsync(files, gameDir + Path.DirectorySeparatorChar, _tempDir, CancellationToken.None))
            using (var ms = new MemoryStream())
            {
                await zip.CopyToAsync(ms);
                zipBytes = ms.ToArray();
            }

            var entries = ReadZip(zipBytes);
            Assert.Equal(
                new[] { "Discs/Disc 1.chd", "Discs/Disc 2 & Extras.chd", "Final Fantasy VII (USA).m3u" },
                entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(File.ReadAllBytes(cue), entries["Final Fantasy VII (USA).m3u"]);
            Assert.Equal(File.ReadAllBytes(disc1), entries["Discs/Disc 1.chd"]);
            Assert.Equal(File.ReadAllBytes(disc2), entries["Discs/Disc 2 & Extras.chd"]);
        }

        [Fact]
        public void GetRomZipEntryName_SingleFileItem_UsesFileNameOnly()
        {
            var rom = CreateFile(Path.Combine("Games", "GBA", "Pokemon - Emerald Version.gba"), Bytes(16, 6));
            Assert.Equal("Pokemon - Emerald Version.gba", JellyEmuRomController.GetRomZipEntryName(rom, rom));
        }

        [Fact]
        public async Task BuildRomZip_DeletesTempFileWhenDisposed()
        {
            var gameDir = Path.Combine(_root, "Game");
            var rom = CreateFile(Path.Combine("Game", "game.bin"), Bytes(1024, 7));

            var zip = await JellyEmuRomController.BuildRomZipAsync(new[] { rom }, gameDir, _tempDir, CancellationToken.None);
            Assert.Single(Directory.GetFiles(_tempDir));
            await zip.DisposeAsync();
            Assert.Empty(Directory.GetFiles(_tempDir));
        }

        [Fact]
        public async Task BuildRomZip_Cancelled_ThrowsAndLeavesNoTempFile()
        {
            var gameDir = Path.Combine(_root, "Game");
            var rom = CreateFile(Path.Combine("Game", "game.bin"), Bytes(1024, 8));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                JellyEmuRomController.BuildRomZipAsync(new[] { rom }, gameDir, _tempDir, cts.Token));
            Assert.Empty(Directory.GetFiles(_tempDir));
        }

        [Fact]
        public async Task BuildRomZip_MissingFile_ThrowsAndLeavesNoTempFile()
        {
            var gameDir = Path.Combine(_root, "Game");
            Directory.CreateDirectory(gameDir);

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                JellyEmuRomController.BuildRomZipAsync(new[] { Path.Combine(gameDir, "gone.bin") }, gameDir, _tempDir, CancellationToken.None));
            Assert.Empty(Directory.GetFiles(_tempDir));
        }
    }
}

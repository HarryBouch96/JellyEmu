using System.Net.Mime;
using System.Text.Encodings.Web;
using JellyEmu.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace JellyEmu.Controllers
{
    /// <summary>
    /// Serves Roms and core information
    /// </summary>
    public class JellyEmuRomController : JellyEmuBaseController
    {
        private readonly PlatformResolver _platformResolver;
        private readonly JellyEmuFileService _fileService;

        public JellyEmuRomController(
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            ILogger<JellyEmuRomController> logger,
            JellyEmuEjsManager ejsManager,
            JellyEmuSessionService sessionService,
            IHttpClientFactory httpClientFactory,
            PlatformResolver platformResolver,
            JellyEmuFileService fileService)
            : base(libraryManager, appPaths, logger, ejsManager, sessionService, httpClientFactory)
        {
            _platformResolver = platformResolver;
            _fileService = fileService;
        }

        [HttpGet("/jellyemu/rom/{itemId}/{filename?}")]
        [HttpHead("/jellyemu/rom/{itemId}/{filename?}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Rom(string itemId, string? filename = null, [FromQuery] string? userId = null)
        {
            var item = LibraryManager.GetItemById(itemId);
            if (item == null || string.IsNullOrEmpty(item.Path))
            {
                Logger.LogWarning("[JellyEmu] Rom: item {ItemId} not found or path missing", itemId);
                return NotFound();
            }

            if (Directory.Exists(item.Path))
            {
                await DownloadZip(itemId).ConfigureAwait(false);
                return new EmptyResult();
            }

            var romPath = _fileService.ResolveActiveRomPath(item.Path, userId, itemId);
            if (string.IsNullOrEmpty(romPath) || !System.IO.File.Exists(romPath))
            {
                Logger.LogWarning("[JellyEmu] Rom: resolved path {Path} not found", romPath);
                return NotFound();
            }

            Logger.LogInformation("[JellyEmu] Serving ROM: {Path}", romPath);

            var fileInfo = new FileInfo(romPath);
            Response.Headers["X-Rom-Hash"] = GetFileHash(romPath);
            Response.Headers["X-Rom-Size"] = fileInfo.Length.ToString();
            Response.Headers["X-Rom-Extension"] = fileInfo.Extension;
            Response.Headers["X-Rom-Name"] = Path.GetFileNameWithoutExtension(romPath);

            var stream = System.IO.File.OpenRead(romPath);
            var finalFileName = Path.GetFileName(romPath);
            Response.Headers["Content-Disposition"] = $"attachment; filename=\"{finalFileName}\"";
            return File(stream, "application/octet-stream", enableRangeProcessing: true);
        }

        [HttpGet("/jellyemu/rom/download-zip/{itemId}")]
        [Authorize]
        public async Task DownloadZip(string itemId)
        {
            var item = LibraryManager.GetItemById(itemId);
            if (item == null || string.IsNullOrEmpty(item.Path))
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var filesToZip = _fileService.ResolveAllRomFiles(item.Path);
            if (filesToZip.Count == 0)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var isDir = Directory.Exists(item.Path);
            var baseDir = item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var zipName = isDir
                ? $"{Path.GetFileName(baseDir)}.zip"
                : $"{Path.GetFileNameWithoutExtension(item.Path)}.zip";

            var cancellationToken = HttpContext.RequestAborted;

            // HEAD only needs the headers; don't build a potentially multi-GB archive for it.
            if (HttpMethods.IsHead(Request.Method))
            {
                Response.ContentType = "application/zip";
                Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(zipName)}\"";
                return;
            }

            // ZipArchive finalises the archive with synchronous writes, which Kestrel
            // rejects on Response.Body. Build the ZIP in a temp file (deleted on close),
            // then stream it to the client asynchronously.
            FileStream zipStream;
            try
            {
                zipStream = await BuildRomZipAsync(filesToZip, item.Path, AppPaths.TempDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Logger.LogDebug("[JellyEmu] ROM zip for {ItemId} cancelled by client", SanitizeForLog(itemId));
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[JellyEmu] Failed to build ROM zip for {ItemId}", SanitizeForLog(itemId));
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            await using (zipStream.ConfigureAwait(false))
            {
                Response.ContentType = "application/zip";
                Response.ContentLength = zipStream.Length;
                Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(zipName)}\"";

                try
                {
                    await zipStream.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Logger.LogDebug("[JellyEmu] ROM zip download for {ItemId} aborted by client", SanitizeForLog(itemId));
                }
            }
        }

        /// <summary>
        /// Writes the given ROM files into a ZIP in a temporary file and returns it
        /// opened and rewound. The file is deleted automatically when the stream is
        /// disposed, and on failure or cancellation before it is returned.
        /// Entries for directory items keep their path relative to the item folder;
        /// single-file items use just the file name.
        /// </summary>
        internal static async Task<FileStream> BuildRomZipAsync(
            IReadOnlyList<string> files,
            string itemPath,
            string tempDirectory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(tempDirectory);
            var tempPath = Path.Combine(tempDirectory, $"jellyemu-rom-{Guid.NewGuid():N}.zip");
            var zipStream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            try
            {
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var filePath in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var entry = archive.CreateEntry(GetRomZipEntryName(itemPath, filePath), CompressionLevel.Fastest);
                        using (var entryStream = entry.Open())
                        using (var fileStream = new FileStream(
                            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                await zipStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                zipStream.Position = 0;
                return zipStream;
            }
            catch
            {
                await zipStream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// ZIP entry name for a ROM file: the '/'-separated path relative to the item
        /// folder for directory items, otherwise just the file name.
        /// </summary>
        internal static string GetRomZipEntryName(string itemPath, string filePath)
        {
            var baseDir = itemPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(baseDir)
                ? Path.GetRelativePath(baseDir, filePath).Replace('\\', '/')
                : Path.GetFileName(filePath);
        }

        /// <summary>
        /// Returns the resolved core name, whether it requires threads (SharedArrayBuffer),
        /// and which launcher to use for the given item.
        /// Used by the UI to decide iframe vs new tab launch, and which play page to load.
        /// 
        /// Path: GET /jellyemu/core/{itemId}
        /// Parameters:
        ///   - itemId (string, path): The unique ID of the item.
        /// Returns Example: { "core": "gba", "needsThreads": false, "launcher": "ejs" }
        ///          Example: { "core": "pico8", "needsThreads": false, "launcher": "pico8" }
        /// </summary>
        [HttpGet("/jellyemu/core/{itemId}")]
        [Produces(MediaTypeNames.Application.Json)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetCore(string itemId, [FromQuery] string? userId)
        {
            var item = LibraryManager.GetItemById(itemId);
            if (item == null)
                return NotFound();

            var info = ResolveCoreInfo(item, userId);
            return Ok(new { core = info.Core, needsThreads = info.NeedsThreads, launcher = info.Launcher });
        }

        /// <summary>
        /// Returns the total number of scanned ROMs in the library (items with the tag JellyEmu).
        /// Path: GET /jellyemu/roms/count/{userId}
        /// </summary>
        [HttpGet("/jellyemu/roms/count/{userId}")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult GetRomCount(string userId)
        {
            if (!VerifyUser(userId)) return Forbid();

            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Book },
                Recursive = true,
            };

            var count = LibraryManager.GetItemList(query)
                .Count(i => i.Tags != null && i.Tags.Contains("JellyEmu", StringComparer.OrdinalIgnoreCase));

            return Ok(new { total = count, count = count, total_roms = count });
        }

        /// <summary>
        /// Returns the list of system tags of all scanned ROMs, merged, with 1 entry per system.
        /// Dynamically returns the total number of systems currently uploaded.
        /// Path: GET /jellyemu/roms/systems/{userId}
        /// </summary>
        [HttpGet("/jellyemu/roms/systems/{userId}")]
        [Authorize]
        [Produces(MediaTypeNames.Application.Json)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult GetRomSystems(string userId)
        {
            if (!VerifyUser(userId)) return Forbid();

            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Book },
                Recursive = true,
            };

            var items = LibraryManager.GetItemList(query)
                .Where(i => i.Tags != null && i.Tags.Contains("JellyEmu", StringComparer.OrdinalIgnoreCase))
                .ToList();

            var knownSystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var val in PlatformResolver.Aliases.Values)
                knownSystems.Add(val);
            foreach (var val in PlatformResolver.LibraryOnlyAliases.Values)
                knownSystems.Add(val);
            foreach (var key in CoreMap.Keys)
                knownSystems.Add(key);

            var systemsList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var resolved = _platformResolver.Resolve(item.Path);
                if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    systemsList.Add(resolved);
                }

                if (item.Tags != null)
                {
                    foreach (var tag in item.Tags)
                    {
                        if (knownSystems.Contains(tag))
                        {
                            systemsList.Add(tag);
                        }
                    }
                }
            }

            var sortedSystems = systemsList.OrderBy(s => s).ToList();

            return Ok(new
            {
                systems = sortedSystems,
                totalSystems = sortedSystems.Count,
                count = sortedSystems.Count
            });
        }
    }
}
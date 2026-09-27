using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using JellyEmu.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Device = JellyEmu.Services.JellyEmuStreamDevices.Device;
using LibraryPath = JellyEmu.Services.JellyEmuStreamDevices.LibraryPath;

namespace JellyEmu.Controllers
{
    /// <summary>
    /// EXPERIMENT (experiment/game-streaming): adding and managing gaming PCs.
    ///
    /// An administrator creates a one-time setup code in the Gaming PCs tab and runs the setup
    /// script it shows on the new PC. The script registers with the code (getting the PC's own
    /// key), installs everything, and checks in with its bridge address and what it can play. The
    /// PC checks in again at every start-up, so a changed IP address is picked up.
    ///
    /// Administrators:
    ///   GET    /jellyemu/stream/admin                  - settings and gaming PCs (with status)
    ///   PUT    /jellyemu/stream/admin/settings         - stream address, library paths
    ///   POST   /jellyemu/stream/admin/setup-code       - one-time code for a new PC
    ///   PATCH  /jellyemu/stream/admin/pcs/{id}         - rename
    ///   DELETE /jellyemu/stream/admin/pcs/{id}         - remove (its key stops working)
    /// Gaming PCs:
    ///   GET    /jellyemu/stream/pc/setup.ps1           - the setup script (no secrets in it)
    ///   GET    /jellyemu/stream/pc/file/{name}         - files the script installs (launcher, manifest)
    ///   POST   /jellyemu/stream/pc/register            - setup code -> PC id and key
    ///   POST   /jellyemu/stream/pc/checkin             - bridge address, host/app ids, platforms
    ///   GET    /jellyemu/stream/pc/bios                - BIOS files the PC needs, from JellyEmu's BIOS folder
    ///   GET    /jellyemu/stream/pc/bios/file?path=     - one of them
    /// </summary>
    public class JellyEmuStreamPcController : JellyEmuBaseController
    {
        private const string AdminPolicy = "RequiresElevation";
        private const string KeyHeader = "X-JellyEmu-Launcher-Key";
        private const string DeviceHeader = "X-JellyEmu-Device";

        /// <summary>Platforms the setup script can set a PC up for.</summary>
        internal static readonly string[] SetupPlatforms =
            { "PlayStation 2", "GameCube", "PlayStation", "Game Boy", "Game Boy Color", "Game Boy Advance" };

        /// <summary>BIOS files a gaming PC needs, by the system JellyEmu's BIOS check recognises them as.</summary>
        private static readonly string[] PcBiosSystems = { "PlayStation", "PlayStation 2" };

        /// <summary>Files served to PCs: resource name -> content type.</summary>
        private static readonly Dictionary<string, string> PcFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["setup.ps1"] = "text/plain; charset=utf-8",
            ["jellyemu-launch.ps1"] = "text/plain; charset=utf-8",
            ["VirtualDisplay.cs"] = "text/plain; charset=utf-8",
            ["DeviceSetup.cs"] = "text/plain; charset=utf-8",
            ["manifest.json"] = "application/json; charset=utf-8"
        };

        private readonly JellyEmuBiosService _bios;

        public JellyEmuStreamPcController(
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            ILogger<JellyEmuStreamPcController> logger,
            JellyEmuEjsManager ejsManager,
            JellyEmuSessionService sessionService,
            IHttpClientFactory httpClientFactory,
            JellyEmuBiosService bios)
            : base(libraryManager, appPaths, logger, ejsManager, sessionService, httpClientFactory)
        {
            _bios = bios;
        }

        // ---- Administrators -----------------------------------------------------------------------

        [HttpGet("/jellyemu/stream/admin")]
        [Authorize(Policy = AdminPolicy)]
        public async Task<IActionResult> Overview()
        {
            var settings = JellyEmuStreamStore.ReadRaw(AppPaths) ?? new JellyEmuStreamDevices.Settings();
            var pcs = new List<object>();
            foreach (var d in settings.Devices)
            {
                var ready = !string.IsNullOrEmpty(d.BridgeUrl) && d.HostId != 0;
                var probe = ready ? await JellyEmuStreamController.ProbeBridge(HttpClientFactory, d).ConfigureAwait(false) : (JellyEmuStreamDevices.Probe?)null;
                pcs.Add(new
                {
                    id = d.Id,
                    name = d.Name,
                    platforms = d.Platforms,
                    managed = d.Managed,
                    lastCheckIn = d.LastCheckIn,
                    version = d.Version,
                    address = d.BridgeUrl,
                    status = probe switch
                    {
                        null => "Setting up",
                        JellyEmuStreamDevices.Probe.Offline => "Offline",
                        JellyEmuStreamDevices.Probe.HostUnavailable => "Not ready",
                        JellyEmuStreamDevices.Probe.Busy => "Playing",
                        _ => "Ready"
                    }
                });
            }
            return Ok(new
            {
                streamOrigin = settings.StreamOrigin,
                libraryPaths = settings.LibraryPaths,
                libraryFolders = LibraryFolders(),
                proxyAddresses = ServerAddresses(),
                pcs
            });
        }

        public class SettingsRequest
        {
            public string? StreamOrigin { get; set; }
            public List<LibraryPath>? LibraryPaths { get; set; }
        }

        [HttpPut("/jellyemu/stream/admin/settings")]
        [Authorize(Policy = AdminPolicy)]
        public IActionResult SaveSettings([FromBody] SettingsRequest request)
        {
            var origin = (request.StreamOrigin ?? string.Empty).Trim().TrimEnd('/');
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                                      || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)))
                return BadRequest(new { message = "The stream address must be an https:// address with nothing after the host name." });

            var paths = new List<LibraryPath>();
            foreach (var p in request.LibraryPaths ?? new List<LibraryPath>())
            {
                var server = (p.Server ?? string.Empty).Trim();
                var pc = (p.Pc ?? string.Empty).Trim();
                if (server.Length == 0 && pc.Length == 0) continue;
                if (!server.StartsWith('/') || server.Contains("..", StringComparison.Ordinal))
                    return BadRequest(new { message = $"\"{server}\" isn't a folder on the server (it should start with /)." });
                if (!(pc.StartsWith(@"\\", StringComparison.Ordinal) || (pc.Length >= 3 && char.IsAsciiLetter(pc[0]) && pc[1] == ':' && pc[2] == '\\'))
                    || pc.Contains("..", StringComparison.Ordinal))
                    return BadRequest(new { message = $"\"{pc}\" isn't a Windows folder (like \\\\nas\\games or D:\\Games)." });
                paths.Add(new LibraryPath { Server = server.EndsWith('/') ? server : server + "/", Pc = pc.EndsWith('\\') ? pc : pc + "\\" });
            }

            JellyEmuStreamStore.Update(AppPaths, s =>
            {
                s.StreamOrigin = origin;
                s.LibraryPaths = paths;
                return true;
            });
            Logger.LogInformation("[JellyEmu] Streaming settings changed: stream address {Origin}, {Count} library path(s)", SanitizeForLog(origin), paths.Count);
            return NoContent();
        }

        public class SetupCodeRequest
        {
            public string? Name { get; set; }
        }

        [HttpPost("/jellyemu/stream/admin/setup-code")]
        [Authorize(Policy = AdminPolicy)]
        public IActionResult SetupCode([FromBody] SetupCodeRequest request)
        {
            var name = CleanName(request.Name);
            if (name == null) return BadRequest(new { message = "Give the PC a name (up to 40 characters)." });
            var settings = JellyEmuStreamStore.ReadRaw(AppPaths);
            if (string.IsNullOrEmpty(settings?.StreamOrigin))
                return BadRequest(new { message = "Set the stream address first." });
            var (code, expires) = JellyEmuStreamStore.NewSetupCode(name, DateTimeOffset.UtcNow);
            Logger.LogInformation("[JellyEmu] Setup code created for a gaming PC named {Name}", SanitizeForLog(name));
            return Ok(new { code, expires });
        }

        public class RenameRequest
        {
            public string? Name { get; set; }
        }

        [HttpPatch("/jellyemu/stream/admin/pcs/{id}")]
        [Authorize(Policy = AdminPolicy)]
        public IActionResult Rename(string id, [FromBody] RenameRequest request)
        {
            var name = CleanName(request.Name);
            if (name == null) return BadRequest(new { message = "Give the PC a name (up to 40 characters)." });
            var found = JellyEmuStreamStore.Update(AppPaths, s =>
            {
                var d = s.Devices.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (d == null) return false;
                d.Name = name;
                return true;
            });
            return found ? NoContent() : NotFound();
        }

        [HttpDelete("/jellyemu/stream/admin/pcs/{id}")]
        [Authorize(Policy = AdminPolicy)]
        public IActionResult Remove(string id)
        {
            var removed = JellyEmuStreamStore.Update(AppPaths, s =>
                s.Devices.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0);
            if (!removed) return NotFound();
            Logger.LogInformation("[JellyEmu] Gaming PC {Id} removed", SanitizeForLog(id));
            return NoContent();
        }

        // ---- Gaming PCs ---------------------------------------------------------------------------

        [HttpGet("/jellyemu/stream/pc/setup.ps1")]
        [AllowAnonymous]
        public IActionResult SetupScript() => PcFile("setup.ps1");

        [HttpGet("/jellyemu/stream/pc/file/{name}")]
        [AllowAnonymous]
        public IActionResult PcFile(string name)
        {
            if (!PcFiles.TryGetValue(name, out var contentType)) return NotFound();
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("JellyEmu.PcSetup." + name);
            if (stream == null) return NotFound();
            using var reader = new StreamReader(stream);
            Response.Headers["Cache-Control"] = "no-cache";
            return Content(reader.ReadToEnd(), contentType);
        }

        public class RegisterRequest
        {
            public string? Code { get; set; }
            public string? ComputerName { get; set; }
        }

        /// <summary>Swaps a setup code for the new PC's id and key. The key is shown only this once.</summary>
        [HttpPost("/jellyemu/stream/pc/register")]
        [AllowAnonymous]
        public IActionResult Register([FromBody] RegisterRequest request)
        {
            var name = JellyEmuStreamStore.ConsumeSetupCode(request.Code, DateTimeOffset.UtcNow);
            if (name == null)
            {
                Logger.LogWarning("[JellyEmu] Gaming PC registration refused: unknown, used or expired setup code");
                return Unauthorized(new { message = "That setup code is wrong, already used or expired. Create a new one in JellyEmu's Gaming PCs settings." });
            }
            var key = JellyEmuStreamStore.NewPcKey();
            var id = JellyEmuStreamStore.Update(AppPaths, s =>
            {
                var newId = JellyEmuStreamDevices.NewDeviceId(name, s.Devices.Select(d => d.Id));
                s.Devices.Add(new Device
                {
                    Id = newId,
                    Name = name,
                    KeyHash = JellyEmuStreamDevices.HashKey(key),
                    LastCheckIn = DateTimeOffset.UtcNow
                });
                return newId;
            });
            Logger.LogInformation("[JellyEmu] Gaming PC {Name} registered as {Id} (computer {Computer})",
                SanitizeForLog(name), SanitizeForLog(id), SanitizeForLog(request.ComputerName));
            return Ok(new { id, name, key });
        }

        public class CheckInRequest
        {
            public string? BridgeUrl { get; set; }
            public long HostId { get; set; }
            public long AppId { get; set; }
            public List<string>? Platforms { get; set; }
            public string? Version { get; set; }
        }

        /// <summary>
        /// A PC reports how to reach it and what it can play (at setup and every start-up). Answers
        /// with what the PC needs to know from the server.
        /// </summary>
        [HttpPost("/jellyemu/stream/pc/checkin")]
        [AllowAnonymous]
        public IActionResult CheckIn([FromBody] CheckInRequest request)
        {
            var pc = AuthenticatedPc();
            if (pc == null) return Unauthorized();
            if (request.BridgeUrl != null && !JellyEmuStreamDevices.IsAllowedBridgeUrl(request.BridgeUrl))
                return BadRequest(new { message = "The bridge address must be http://<private IP address>:<port>." });
            var platforms = (request.Platforms ?? new List<string>())
                .Select(p => SetupPlatforms.FirstOrDefault(s => string.Equals(s, p, StringComparison.OrdinalIgnoreCase)))
                .Where(p => p != null).Select(p => p!).Distinct().ToList();

            var name = JellyEmuStreamStore.Update(AppPaths, s =>
            {
                var d = s.Devices.First(x => x.Id == pc.Id);
                if (request.BridgeUrl != null) d.BridgeUrl = request.BridgeUrl.TrimEnd('/');
                if (request.HostId != 0) d.HostId = request.HostId;
                if (request.AppId != 0) d.AppId = request.AppId;
                if (request.Platforms != null) d.Platforms = platforms;
                if (!string.IsNullOrEmpty(request.Version) && request.Version.Length <= 20) d.Version = request.Version;
                d.LastCheckIn = DateTimeOffset.UtcNow;
                return d.Name;
            });
            var settings = JellyEmuStreamStore.ReadRaw(AppPaths)!;
            Logger.LogInformation("[JellyEmu] Gaming PC {Id} checked in from {Bridge} ({Platforms})",
                SanitizeForLog(pc.Id), SanitizeForLog(request.BridgeUrl), SanitizeForLog(string.Join(", ", platforms)));
            return Ok(new
            {
                name,
                libraryPaths = settings.LibraryPaths,
                // Only these may reach the PC's bridge (its firewall rule): Caddy runs here.
                proxyAddresses = ServerAddresses()
            });
        }

        [HttpGet("/jellyemu/stream/pc/bios")]
        [AllowAnonymous]
        public IActionResult Bios()
        {
            if (AuthenticatedPc() == null) return Unauthorized();
            return Ok(PcBios().Select(b => new { system = b.SystemOrCore, path = b.RelativePath, fileName = b.FileName, size = b.SizeBytes, sha1 = b.Sha1 }));
        }

        [HttpGet("/jellyemu/stream/pc/bios/file")]
        [AllowAnonymous]
        public IActionResult BiosFile([FromQuery] string? path)
        {
            if (AuthenticatedPc() == null) return Unauthorized();
            // Only files from the list above, so nothing else in the BIOS folder (or outside it) is served.
            var bios = PcBios().FirstOrDefault(b => string.Equals(b.RelativePath, path, StringComparison.Ordinal));
            if (bios == null) return NotFound();
            return PhysicalFile(Path.Combine(_bios.GetBiosDirectory(), bios.RelativePath), "application/octet-stream");
        }

        // ---- Helpers ------------------------------------------------------------------------------

        private List<BiosInfo> PcBios() =>
            _bios.ListInstalledBios()
                .Where(b => PcBiosSystems.Contains(b.SystemOrCore, StringComparer.OrdinalIgnoreCase))
                .ToList();

        /// <summary>The PC making this request (X-JellyEmu-Device) if its own key is right.</summary>
        private Device? AuthenticatedPc()
        {
            var id = Request.Headers[DeviceHeader].ToString();
            var pc = JellyEmuStreamStore.ReadRaw(AppPaths)?.Devices
                .FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (pc != null && JellyEmuStreamDevices.KeyMatches(pc, Request.Headers[KeyHeader].ToString())) return pc;
            Logger.LogWarning("[JellyEmu] Gaming PC request refused: unknown PC or bad key");
            return null;
        }

        private static string? CleanName(string? name)
        {
            var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray()).Trim();
            return clean.Length is > 0 and <= 40 ? clean : null;
        }

        /// <summary>This server's own IPv4 addresses (Caddy runs on it, so these reach the PCs' bridges).</summary>
        private static List<string> ServerAddresses() =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct()
                .ToList();

        /// <summary>Folders of the Jellyfin libraries, to suggest library paths.</summary>
        private List<string> LibraryFolders() =>
            LibraryManager.GetVirtualFolders()
                .SelectMany(f => f.Locations ?? Array.Empty<string>())
                .Distinct()
                .ToList();
    }
}

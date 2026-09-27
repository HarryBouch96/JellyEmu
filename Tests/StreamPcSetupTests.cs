using System;
using System.Collections.Generic;
using JellyEmu.Services;
using Xunit;

namespace JellyEmu.Tests
{
    /// <summary>Gaming PCs added with the setup script: addresses, keys, codes, library paths.</summary>
    public class StreamPcSetupTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        [Theory]
        [InlineData("http://192.168.0.218:8080", true)]
        [InlineData("http://10.1.2.3:9000/", true)]
        [InlineData("http://172.20.0.5:8080", true)]
        [InlineData("http://100.101.102.103:8080", true)]   // Tailscale
        [InlineData("https://192.168.0.218:8080", false)]   // the bridge is plain http on the LAN
        [InlineData("http://8.8.8.8:8080", false)]          // not a private address
        [InlineData("http://172.32.0.1:8080", false)]
        [InlineData("http://192.168.0.218:80", false)]      // privileged port
        [InlineData("http://192.168.0.218", false)]         // (port 80)
        [InlineData("http://example.com:8080", false)]      // names could be re-pointed anywhere
        [InlineData("http://user:pw@192.168.0.218:8080", false)]
        [InlineData("http://192.168.0.218:8080/api", false)]
        [InlineData("http://192.168.0.218:8080/?x=1", false)]
        [InlineData("http://[fd00::1]:8080", false)]
        [InlineData("file:///c:/windows", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void BridgeUrl_OnlyPrivateHttpAddresses(string? url, bool allowed) =>
            Assert.Equal(allowed, JellyEmuStreamDevices.IsAllowedBridgeUrl(url));

        [Fact]
        public void Upstream_IsHostAndPort() =>
            Assert.Equal("192.168.0.218:8080", JellyEmuStreamDevices.Upstream(new JellyEmuStreamDevices.Device { BridgeUrl = "http://192.168.0.218:8080" }));

        [Fact]
        public void Key_MatchesOnlyItsOwnPc()
        {
            var key = JellyEmuStreamStore.NewPcKey();
            var pc = new JellyEmuStreamDevices.Device { Id = "pc", KeyHash = JellyEmuStreamDevices.HashKey(key) };
            Assert.True(JellyEmuStreamDevices.KeyMatches(pc, key));
            Assert.False(JellyEmuStreamDevices.KeyMatches(pc, key + "x"));
            Assert.False(JellyEmuStreamDevices.KeyMatches(pc, ""));
            Assert.False(JellyEmuStreamDevices.KeyMatches(pc, null));
            // A PC set up by hand has no key of its own; it uses the shared launcher key instead.
            Assert.False(JellyEmuStreamDevices.KeyMatches(new JellyEmuStreamDevices.Device { Id = "laptop" }, key));
        }

        [Fact]
        public void SetupCode_WorksOnceAndIgnoresFormatting()
        {
            var (code, _) = JellyEmuStreamStore.NewSetupCode("Living room PC", Now);
            Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
            Assert.Equal("Living room PC", JellyEmuStreamStore.ConsumeSetupCode(" " + code.ToLowerInvariant().Replace("-", "") + " ", Now));
            Assert.Null(JellyEmuStreamStore.ConsumeSetupCode(code, Now)); // used up
        }

        [Fact]
        public void SetupCode_Expires()
        {
            var (code, expires) = JellyEmuStreamStore.NewSetupCode("PC", Now);
            Assert.Null(JellyEmuStreamStore.ConsumeSetupCode(code, expires.AddSeconds(1)));
            Assert.Null(JellyEmuStreamStore.ConsumeSetupCode("ABCD-EFGH", Now));
            Assert.Null(JellyEmuStreamStore.ConsumeSetupCode(null, Now));
        }

        [Fact]
        public void DeviceId_FromNameAndUnique()
        {
            Assert.Equal("living-room-pc", JellyEmuStreamDevices.NewDeviceId("Living room PC!", new[] { "laptop" }));
            Assert.Equal("laptop-2", JellyEmuStreamDevices.NewDeviceId("Laptop", new[] { "laptop" }));
            Assert.Equal("laptop-3", JellyEmuStreamDevices.NewDeviceId("laptop", new[] { "laptop", "LAPTOP-2" }));
            Assert.Equal("pc", JellyEmuStreamDevices.NewDeviceId("???", Array.Empty<string>()));
            Assert.True(JellyEmuStreamDevices.IsValidDeviceId(JellyEmuStreamDevices.NewDeviceId(new string('x', 60), Array.Empty<string>())));
        }

        private static readonly JellyEmuStreamDevices.Settings Paths = new()
        {
            LibraryPaths = new List<JellyEmuStreamDevices.LibraryPath>
            {
                new() { Server = "/media/wd12tb/", Pc = @"\\192.168.0.180\Jellyfin\" },
                new() { Server = "/media/wd12tb/Games/PlayStation 2", Pc = @"D:\PS2" }
            }
        };

        [Theory]
        [InlineData("/media/wd12tb/Games/Gameboy Advance/Pokemon", @"\\192.168.0.180\Jellyfin\Games\Gameboy Advance\Pokemon")]
        [InlineData("/media/wd12tb/Games/PlayStation 2/God of War", @"D:\PS2\God of War")] // longest match wins
        [InlineData("/media/other/game", null)]
        [InlineData("/media/wd12tb/../etc/passwd", null)]
        [InlineData("/media/wd12tbx/game", null)]
        public void PcPath_FromLibraryPaths(string serverPath, string? expected) =>
            Assert.Equal(expected, JellyEmuStreamDevices.ToPcPath(Paths, serverPath));

        [Fact]
        public void Origin_SharedUnlessThePcHasItsOwn()
        {
            var settings = new JellyEmuStreamDevices.Settings { StreamOrigin = "https://stream.example.com/" };
            Assert.Equal("https://stream.example.com", new JellyEmuStreamDevices.Device().OriginIn(settings));
            Assert.Equal("https://old.example.com", new JellyEmuStreamDevices.Device { StreamOrigin = "https://old.example.com" }.OriginIn(settings));
        }
    }
}

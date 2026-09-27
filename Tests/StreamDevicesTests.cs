using System;
using System.Collections.Generic;
using JellyEmu.Services;
using Xunit;
using Probe = JellyEmu.Services.JellyEmuStreamDevices.Probe;

namespace JellyEmu.Tests
{
    public class StreamDevicesTests
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        private const string Me = "me";
        private const string Friend = "friend";

        private static readonly JellyEmuStreamDevices.Device Laptop = new()
        {
            Id = "laptop",
            Name = "Gaming laptop",
            Platforms = new List<string> { "PlayStation 2", "GameCube" }
        };

        private static JellyEmuStreamDevices.Status Eval(string platform, Probe probe, string? currentUser = null, int secondsSinceSeen = 0) =>
            JellyEmuStreamDevices.Evaluate(Laptop, platform, probe, currentUser, Now.AddSeconds(-secondsSinceSeen), Me, Now);

        [Fact]
        public void Free_SupportedPlatform_IsAvailable()
        {
            Assert.True(Eval("PlayStation 2", Probe.Free).Available);
            Assert.True(Eval("gamecube", Probe.Free).Available); // platform names are case-insensitive
        }

        [Fact]
        public void UnsupportedPlatform_IsNotAvailable()
        {
            var status = Eval("Game Boy Advance", Probe.Free);
            Assert.False(status.Available);
            Assert.Equal("Can't play this system", status.Reason);
        }

        [Theory]
        [InlineData(Probe.Offline, "Offline")]
        [InlineData(Probe.HostUnavailable, "Not ready")]
        public void OfflineOrNotReady_IsNotAvailable(Probe probe, string reason)
        {
            var status = Eval("PlayStation 2", probe);
            Assert.False(status.Available);
            Assert.Equal(reason, status.Reason);
        }

        [Fact]
        public void SomeoneElsePlaying_IsBusy()
        {
            var status = Eval("PlayStation 2", Probe.Busy, currentUser: Friend, secondsSinceSeen: 10);
            Assert.False(status.Available);
            Assert.Equal("In use by someone else", status.Reason);
        }

        [Fact]
        public void SomeoneElsesAbandonedGame_CanBeReplaced()
        {
            // Their stream page hasn't checked in for longer than the busy window.
            var stale = (int)JellyEmuStreamDevices.BusyWindow.TotalSeconds + 5;
            Assert.True(Eval("PlayStation 2", Probe.Busy, currentUser: Friend, secondsSinceSeen: stale).Available);
        }

        [Fact]
        public void MyOwnRunningGame_IsAvailable()
        {
            Assert.True(Eval("PlayStation 2", Probe.Busy, currentUser: Me, secondsSinceSeen: 5).Available);
        }
    }
}

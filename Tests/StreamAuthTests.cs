using System;
using System.Text;
using JellyEmu.Services;
using Xunit;

namespace JellyEmu.Tests
{
    public class StreamAuthTests
    {
        private static readonly byte[] Key = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef");
        private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        private static JellyEmuStreamAuth.Claims Pass(long expiresIn = 60, string? nonce = null) =>
            new("pass", "c069e3aceb5e4290a08913215870004c", "ef951bacdf1c1ef2b78383cda85d73ef", 3667480155, 226546161,
                Now.ToUnixTimeSeconds() + expiresIn, nonce ?? JellyEmuStreamAuth.NewNonce());

        [Fact]
        public void Verify_ValidToken_ReturnsClaims()
        {
            var claims = Pass();
            var result = JellyEmuStreamAuth.Verify(JellyEmuStreamAuth.Sign(claims, Key), "pass", Key, Now);
            Assert.Equal(claims, result);
        }

        [Fact]
        public void Verify_RejectsExpiredWrongKindWrongKeyAndTampered()
        {
            var token = JellyEmuStreamAuth.Sign(Pass(), Key);

            Assert.Null(JellyEmuStreamAuth.Verify(JellyEmuStreamAuth.Sign(Pass(expiresIn: -1), Key), "pass", Key, Now));
            Assert.Null(JellyEmuStreamAuth.Verify(token, "session", Key, Now));
            Assert.Null(JellyEmuStreamAuth.Verify(token, "pass", Encoding.ASCII.GetBytes("another-key-another-key-another-k"), Now));

            // Change a character of the payload: the signature no longer matches.
            var tampered = (token[0] == 'A' ? 'B' : 'A') + token[1..];
            Assert.Null(JellyEmuStreamAuth.Verify(tampered, "pass", Key, Now));
            Assert.Null(JellyEmuStreamAuth.Verify("garbage", "pass", Key, Now));
            Assert.Null(JellyEmuStreamAuth.Verify(null, "pass", Key, Now));
        }

        [Fact]
        public void ConsumePass_OnlyOnce()
        {
            var pass = Pass(nonce: "nonce-" + Guid.NewGuid());
            Assert.True(JellyEmuStreamAuth.ConsumePass(pass, Now));
            Assert.False(JellyEmuStreamAuth.ConsumePass(pass, Now));
        }

        [Theory]
        [InlineData("GET", "/stream.html?hostId=1&appId=2")]
        [InlineData("GET", "/stream.js")]
        [InlineData("GET", "/stream/transport/webrtc.js")]
        [InlineData("GET", "/styles/main.css")]
        [InlineData("GET", "/api/authenticate")]
        [InlineData("GET", "/api/role?id=")]
        [InlineData("GET", "/api/host/stream")]
        [InlineData("POST", "/api/host/cancel")]
        public void IsAllowedRequest_StreamPageNeeds(string method, string uri)
        {
            Assert.True(JellyEmuStreamAuth.IsAllowedRequest(method, uri));
        }

        [Theory]
        [InlineData("GET", "/")]
        [InlineData("GET", "/index.html")]
        [InlineData("GET", "/admin.html")]
        [InlineData("POST", "/api/login")]
        [InlineData("GET", "/api/users")]
        [InlineData("POST", "/api/user")]
        [InlineData("GET", "/api/hosts")]
        [InlineData("POST", "/api/host")]
        [InlineData("DELETE", "/api/host?id=1")]
        [InlineData("POST", "/api/pair")]
        [InlineData("GET", "/api/apps?host_id=1")]
        [InlineData("GET", "/api/app/image")]
        [InlineData("POST", "/api/host/wake")]
        [InlineData("PATCH", "/api/role")]
        [InlineData("POST", "/stream.html")]
        [InlineData("GET", "/stream/../admin.html")]
        public void IsAllowedRequest_BlocksManagement(string method, string uri)
        {
            Assert.False(JellyEmuStreamAuth.IsAllowedRequest(method, uri));
        }
    }
}

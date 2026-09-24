using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The session-version half of password recovery: the credential generation a token was minted for travels inside
    /// the token, and a reset moves the player's generation on.
    ///
    /// Without this, a stolen JWT would keep working for its full seven days after the victim changed their password —
    /// which is the one thing a reset is supposed to prevent. The comparison itself lives in <c>Program.cs</c> (REST)
    /// and <c>GameHub</c> (SignalR); what is testable in isolation is that the token carries the version and that it
    /// survives the round trip.
    /// </summary>
    public class TokenSessionVersionTests
    {
        [Fact]
        public void Validate_CarriesTheSessionVersionTheTokenWasIssuedFor()
        {
            var player = new Player
            {
                Login = "argel",
                Email = "argel@example.com",
                SessionVersion = 3,
            };

            var service = new TokenService();
            var payload = service.Validate(service.IssueToken(player));

            Assert.NotNull(payload);
            Assert.Equal("argel", payload!.Login);
            Assert.Equal(3, payload.SessionVersion);
        }

        [Fact]
        public void AResetMakesEverySessionIssuedBeforeItDisagreeWithThePlayer()
        {
            var player = new Player { Login = "argel", Email = "argel@example.com" };
            var service = new TokenService();

            // A session opened before the reset, carrying the generation the player was on at the time.
            var tokenFromBeforeTheReset = service.IssueToken(player);
            var carriedVersion = service.Validate(tokenFromBeforeTheReset)!.SessionVersion;

            // What PasswordResetService.ResetPasswordAsync does to the row.
            player.SessionVersion++;

            // The old token is still structurally valid — signature fine, nowhere near expiry — so the *only* thing
            // that retires it is the mismatch the middleware and the hub compare for.
            Assert.NotNull(service.Validate(tokenFromBeforeTheReset));
            Assert.NotEqual(player.SessionVersion, carriedVersion);
        }

        [Fact]
        public void ATokenWithoutASessionVersionIsReadAsGenerationZero()
        {
            var player = new Player { Login = "argel", Email = "argel@example.com" };

            // A player who has never reset anything is at generation 0, which is the version a token minted before
            // password recovery existed is treated as carrying — so existing sessions survive the upgrade.
            Assert.Equal(0, player.SessionVersion);

            var service = new TokenService();
            Assert.Equal(0, service.Validate(service.IssueToken(player))!.SessionVersion);
        }
    }
}

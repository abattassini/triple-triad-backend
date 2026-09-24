using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Controllers;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;
using TripleTriadApi.Tests.Services;
using TripleTriadApi.Validators;

namespace TripleTriadApi.Tests.Controllers
{
    /// <summary>
    /// The two recovery endpoints as the client sees them: `forgot-password` answers identically whatever happens, so
    /// it cannot be used to find out which emails have accounts; `reset-password` holds a new password to the same
    /// policy as registration and rejects a bad code without giving anything away.
    ///
    /// The controller is driven directly over an EF InMemory database, as <c>PlayerControllerTests</c> does. The
    /// rate-limiting attribute on the endpoints is inert here because it is middleware — the limits themselves are
    /// covered in <c>PasswordResetServiceTests</c>.
    /// </summary>
    public class PasswordRecoveryTests
    {
        private const string PlayerLogin = "argel";
        private const string PlayerEmail = "argel@example.com";

        [Fact]
        public async Task ForgotPassword_AnswersIdentically_ForKnownAndUnknownAddresses()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var mail = new RecordingEmailSender();
            var controller = CreateController(context, mail);

            var known = await controller.ForgotPassword(new ForgotPasswordRequest { Email = PlayerEmail });
            var unknown = await controller.ForgotPassword(
                new ForgotPasswordRequest { Email = "nobody@example.com" }
            );

            var knownAccepted = Assert.IsType<AcceptedResult>(known.Result);
            var unknownAccepted = Assert.IsType<AcceptedResult>(unknown.Result);

            // Same status and, crucially, the same body: the endpoint must never answer "does this address have an
            // account here?".
            Assert.Equal(202, knownAccepted.StatusCode);
            Assert.Equal(202, unknownAccepted.StatusCode);
            Assert.Equal(
                JsonSerializer.Serialize(knownAccepted.Value),
                JsonSerializer.Serialize(unknownAccepted.Value)
            );

            // Only the account that really exists received anything.
            var message = Assert.Single(mail.Sent);
            Assert.Equal(PlayerEmail, message.To);
        }

        [Fact]
        public async Task ForgotPassword_ForABlankAddress_StillAnswersTheSame()
        {
            using var context = CreateContext();
            var mail = new RecordingEmailSender();
            var controller = CreateController(context, mail);

            var result = await controller.ForgotPassword(new ForgotPasswordRequest { Email = "   " });

            // An empty address is answered like an unknown one, for exactly the same reason.
            Assert.IsType<AcceptedResult>(result.Result);
            Assert.Empty(mail.Sent);
        }

        [Theory]
        [InlineData("short1")]
        [InlineData("allletters")]
        [InlineData("123456789")]
        public async Task ResetPassword_AppliesTheRegistrationPasswordPolicy(string weakPassword)
        {
            using var context = CreateContext();
            var controller = CreateController(context, new RecordingEmailSender());

            var result = await controller.ResetPassword(
                new ResetPasswordRequest { Token = "23456789AB", NewPassword = weakPassword }
            );

            // Recovering an account can never leave it with a weaker password than creating one would have allowed.
            var rejected = Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(400, rejected.StatusCode);
        }

        [Fact]
        public async Task ResetPassword_WithAValidCode_ChangesThePassword()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var mail = new RecordingEmailSender();
            var controller = CreateController(context, mail);

            await controller.ForgotPassword(new ForgotPasswordRequest { Email = PlayerEmail });
            var code = CodeFromLink(Assert.Single(mail.Sent));

            var result = await controller.ResetPassword(
                new ResetPasswordRequest { Token = code, NewPassword = "newpassword9" }
            );

            Assert.IsType<OkObjectResult>(result.Result);

            var player = await context.Players.SingleAsync();
            Assert.True(new PasswordHasherService().Verify("newpassword9", player.PasswordHash));

            // Bumped here, which is what retires the tokens the player was already holding.
            Assert.Equal(1, player.SessionVersion);
        }

        [Fact]
        public async Task ResetPassword_WithABadCode_IsRejected()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var controller = CreateController(context, new RecordingEmailSender());

            var result = await controller.ResetPassword(
                new ResetPasswordRequest { Token = "23456789AB", NewPassword = "newpassword9" }
            );

            var rejected = Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(400, rejected.StatusCode);
        }

        [Fact]
        public async Task ResetPassword_WithAMissingCode_IsRejected()
        {
            using var context = CreateContext();
            var controller = CreateController(context, new RecordingEmailSender());

            var result = await controller.ResetPassword(
                new ResetPasswordRequest { Token = "  ", NewPassword = "newpassword9" }
            );

            var rejected = Assert.IsType<BadRequestObjectResult>(result.Result);
            Assert.Equal(400, rejected.StatusCode);
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        private static async Task SeedPlayerAsync(TripleTriadContext context)
        {
            context.Players.Add(
                new Player
                {
                    Login = PlayerLogin,
                    Email = PlayerEmail,
                    PasswordHash = new PasswordHasherService().Hash("password1"),
                    CreatedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }

        /// <summary>The controller under test, with a source address so the per-address limit path is exercised.</summary>
        private static PlayerController CreateController(TripleTriadContext context, IEmailSender mail) =>
            new(
                new PlayerRepository(context),
                new PlayerCardRepository(context),
                new PlayerPackRepository(context),
                new GameRepository(context),
                new PasswordHasherService(),
                new RegisterPlayerRequestValidator(),
                new ResetPasswordRequestValidator(),
                new TokenService(),
                PasswordRecoveryTestHarness.CreateService(context, mail)
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.7") },
                    },
                },
            };

        private static string CodeFromLink(EmailMessage message) =>
            Regex.Match(message.TextBody, @"reset-password\?token=([0-9A-Z]+)").Groups[1].Value;
    }
}

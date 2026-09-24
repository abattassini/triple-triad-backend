using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Tests for password recovery's rules: a code is issued only for a real, non-CPU account, what is stored is a
    /// hash and never the code, the layered limits refuse in the right order, and spending a code changes the password
    /// exactly once while retiring every session the player already had.
    ///
    /// The clock is supplied to every call (there is no timer), mail is captured by <see cref="RecordingEmailSender"/>
    /// rather than sent, and codes are read back out of the message body — so these tests also pin the message itself:
    /// the link, the typed code, and that both carry the same token.
    /// </summary>
    public class PasswordResetServiceTests
    {
        private const string PlayerLogin = "argel";
        private const string PlayerEmail = "argel@example.com";
        private const string OldPassword = "password1";
        private const string NewPassword = "password2";

        private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task RequestReset_SendsACode_AndStoresOnlyItsHash()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, mail) = CreateService(context);

            var outcome = await service.RequestResetAsync(PlayerEmail, "203.0.113.7", Now);

            Assert.Equal(RecoveryRequestOutcome.Sent, outcome);

            var message = Assert.Single(mail.Sent);
            Assert.Equal(PlayerEmail, message.To);

            // The code is delivered twice — as a link and as something to type — and both are the same token.
            var code = CodeFromLink(message);
            var grouped = GroupedCodeFromBody(message);
            Assert.NotEmpty(code);
            Assert.Equal(code, grouped.Replace("-", string.Empty));

            // Only the hash is persisted, so a database dump cannot be replayed to reset an account.
            var stored = Assert.Single(context.PasswordResetTokens);
            Assert.Equal(PlayerLogin, stored.PlayerLogin);
            Assert.NotEqual(code, stored.TokenHash);
            Assert.Equal(64, stored.TokenHash.Length);
            Assert.Equal("203.0.113.7", stored.RequestedFromIp);
            Assert.Equal(Now.AddMinutes(15), stored.ExpiresAt);
            Assert.Null(stored.ConsumedAt);
        }

        [Fact]
        public async Task RequestReset_ForAnUnknownAddress_IssuesNothing()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, mail) = CreateService(context);

            var outcome = await service.RequestResetAsync("nobody@example.com", "203.0.113.7", Now);

            // Same shape as the real case, minus the mail — which is what keeps the endpoint from answering
            // "does this address have an account?".
            Assert.Equal(RecoveryRequestOutcome.NoSuchAccount, outcome);
            Assert.Empty(mail.Sent);
            Assert.Empty(context.PasswordResetTokens);
        }

        [Fact]
        public async Task RequestReset_ForTheCpuSentinel_IssuesNothing()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, login: CpuOpponent.Login, email: "cpu@example.com");
            var (service, mail) = CreateService(context);

            var outcome = await service.RequestResetAsync("cpu@example.com", "203.0.113.7", Now);

            // The CPU is a real row with a real login, and no human may take it over.
            Assert.Equal(RecoveryRequestOutcome.NoSuchAccount, outcome);
            Assert.Empty(mail.Sent);
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        private static async Task SeedPlayerAsync(
            TripleTriadContext context,
            string login = PlayerLogin,
            string email = PlayerEmail
        )
        {
            context.Players.Add(
                new Player
                {
                    Login = login,
                    Email = email,
                    PasswordHash = new PasswordHasherService().Hash(OldPassword),
                    CreatedAt = Now,
                }
            );

            await context.SaveChangesAsync();
        }

        private static (PasswordResetService Service, RecordingEmailSender Mail) CreateService(
            TripleTriadContext context,
            PasswordResetOptions? options = null
        )
        {
            var mail = new RecordingEmailSender();

            return (PasswordRecoveryTestHarness.CreateService(context, mail, options), mail);
        }

        /// <summary>The code exactly as the emailed link carries it.</summary>
        private static string CodeFromLink(EmailMessage message) =>
            Regex.Match(message.TextBody, @"reset-password\?token=([0-9A-Z]+)").Groups[1].Value;

        /// <summary>The same code as it is printed for typing — grouped in fours, which verification strips.</summary>
        private static string GroupedCodeFromBody(EmailMessage message) =>
            Regex.Match(message.TextBody, "[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{2}").Value;

        [Fact]
        public async Task RequestReset_RefusesPastThePerAccountHourlyLimit()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, mail) = CreateService(
                context,
                new PasswordResetOptions { PerAccountPerHour = 2, PerAccountPerDay = 100 }
            );

            Assert.Equal(RecoveryRequestOutcome.Sent, await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now));
            Assert.Equal(
                RecoveryRequestOutcome.Sent,
                await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now.AddMinutes(5))
            );

            // The third request inside the same hour never reaches the player's inbox.
            Assert.Equal(
                RecoveryRequestOutcome.RateLimited,
                await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now.AddMinutes(10))
            );
            Assert.Equal(2, mail.Sent.Count);

            // Once the window has moved past the first two sends, the player can ask again.
            Assert.Equal(
                RecoveryRequestOutcome.Sent,
                await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now.AddHours(2))
            );
        }

        [Fact]
        public async Task RequestReset_RefusesPastThePerAddressHourlyLimit()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            await SeedPlayerAsync(context, login: "other", email: "other@example.com");
            var (service, mail) = CreateService(
                context,
                new PasswordResetOptions
                {
                    PerIpPerHour = 1,
                    PerAccountPerHour = 100,
                    PerAccountPerDay = 100,
                }
            );

            Assert.Equal(RecoveryRequestOutcome.Sent, await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now));

            // A different account from the same source is refused, so one script cannot farm the endpoint by walking
            // through addresses.
            Assert.Equal(
                RecoveryRequestOutcome.RateLimited,
                await service.RequestResetAsync("other@example.com", "1.1.1.1", Now.AddMinutes(1))
            );

            // A genuinely different source is unaffected — which is the whole point of partitioning by address.
            Assert.Equal(
                RecoveryRequestOutcome.Sent,
                await service.RequestResetAsync("other@example.com", "2.2.2.2", Now.AddMinutes(2))
            );
            Assert.Equal(2, mail.Sent.Count);
        }

        [Fact]
        public async Task RequestReset_RefusesOnceTheGlobalDailyCapIsSpent()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            await SeedPlayerAsync(context, login: "other", email: "other@example.com");
            var (service, _) = CreateService(
                context,
                new PasswordResetOptions
                {
                    GlobalPerDay = 1,
                    PerAccountPerHour = 100,
                    PerAccountPerDay = 100,
                }
            );

            Assert.Equal(RecoveryRequestOutcome.Sent, await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now));

            // The global cap is the backstop that protects the sending mailbox, so when it goes the whole feature
            // goes with it — including accounts nowhere near their own limits. That is why it must be generous and
            // why tripping it is logged as an error.
            Assert.Equal(
                RecoveryRequestOutcome.RateLimited,
                await service.RequestResetAsync("other@example.com", "9.9.9.9", Now.AddMinutes(1))
            );
        }

        [Fact]
        public async Task RequestReset_WhenDeliveryFails_IssuesTheCodeAnyway()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var mail = new RecordingEmailSender
            {
                Failure = new InvalidOperationException("Email is not configured."),
            };
            var service = PasswordRecoveryTestHarness.CreateService(context, mail);

            var outcome = await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now);

            // The failure is reported for the logs, but not thrown: the endpoint has to answer exactly as it does for
            // an unknown address, or a broken mailbox becomes an account-existence oracle.
            Assert.Equal(RecoveryRequestOutcome.SendFailed, outcome);

            // The token was still issued, so the attempt counts against the limits — it was made.
            Assert.Single(context.PasswordResetTokens);
        }

        [Fact]
        public async Task RequestReset_IssuingAgain_RetiresThePreviousCode()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, mail) = CreateService(context);

            await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now);
            var firstCode = CodeFromLink(mail.Sent[0]);

            await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now.AddMinutes(1));
            var secondCode = CodeFromLink(mail.Sent[1]);

            Assert.NotEqual(firstCode, secondCode);

            // The earlier email is dead the moment a new one is issued, so a code read out of an old message cannot be
            // replayed after the player has asked again.
            Assert.False(
                (await service.ResetPasswordAsync(firstCode, NewPassword, Now.AddMinutes(2))).Succeeded
            );
            Assert.True(
                (await service.ResetPasswordAsync(secondCode, NewPassword, Now.AddMinutes(2))).Succeeded
            );
        }

        [Fact]
        public async Task ResetPassword_ChangesThePassword_AndRetiresExistingSessions()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, mail) = CreateService(context);

            await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now);

            var result = await service.ResetPasswordAsync(
                CodeFromLink(mail.Sent[0]),
                NewPassword,
                Now.AddMinutes(1)
            );

            Assert.True(result.Succeeded);
            Assert.Null(result.Error);

            var hasher = new PasswordHasherService();
            var player = await context.Players.SingleAsync();
            Assert.True(hasher.Verify(NewPassword, player.PasswordHash));
            Assert.False(hasher.Verify(OldPassword, player.PasswordHash));

            // The generation moves, which is what makes every JWT already issued stale — so an attacker who was
            // signed in is actually evicted rather than left holding a valid 7-day token.
            Assert.Equal(1, player.SessionVersion);
        }

        [Fact]
        public async Task ResetPassword_RejectsAnExpiredCode()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, mail) = CreateService(context);

            await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now);

            // The default lifetime is 15 minutes.
            var result = await service.ResetPasswordAsync(
                CodeFromLink(mail.Sent[0]),
                NewPassword,
                Now.AddMinutes(16)
            );

            Assert.False(result.Succeeded);

            // And the password is untouched, so an expired code is inert rather than merely reported as failed.
            var player = await context.Players.SingleAsync();
            Assert.True(new PasswordHasherService().Verify(OldPassword, player.PasswordHash));
            Assert.Equal(0, player.SessionVersion);
        }

        [Fact]
        public async Task ResetPassword_RejectsACodeThatWasAlreadyUsed()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, mail) = CreateService(context);

            await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now);
            var code = CodeFromLink(mail.Sent[0]);

            Assert.True((await service.ResetPasswordAsync(code, NewPassword, Now)).Succeeded);

            // Spending it once is all it gets: the second attempt cannot set a third password.
            Assert.False((await service.ResetPasswordAsync(code, "password3", Now)).Succeeded);

            var player = await context.Players.SingleAsync();
            Assert.True(new PasswordHasherService().Verify(NewPassword, player.PasswordHash));
            Assert.Equal(1, player.SessionVersion);
        }

        [Fact]
        public async Task ResetPassword_AcceptsTheCodeInAnyCaseAndWithItsDashes()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, mail) = CreateService(context);

            await service.RequestResetAsync(PlayerEmail, "1.1.1.1", Now);

            // What a player actually produces: the grouped code, lower-cased by an autocorrecting keyboard, pasted
            // with surrounding whitespace. Normalisation is what stops any of that becoming a failed recovery.
            var asTyped = $"  {GroupedCodeFromBody(mail.Sent[0]).ToLowerInvariant()}  ";

            Assert.True((await service.ResetPasswordAsync(asTyped, NewPassword, Now)).Succeeded);
        }

        [Fact]
        public async Task ResetPassword_RejectsAWrongCode()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, _) = CreateService(context);

            var result = await service.ResetPasswordAsync("23456789AB", NewPassword, Now);

            Assert.False(result.Succeeded);
            Assert.NotNull(result.Error);

            // A guess must not have touched anything.
            var player = await context.Players.SingleAsync();
            Assert.True(new PasswordHasherService().Verify(OldPassword, player.PasswordHash));
            Assert.Equal(0, player.SessionVersion);
        }

        [Fact]
        public async Task ResetPassword_RejectsACodeOfTheWrongShape()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context);
            var (service, _) = CreateService(context);

            // Too short, too long and empty all fail before any lookup happens.
            foreach (var code in new[] { string.Empty, "23456789A", "23456789ABC" })
            {
                Assert.False((await service.ResetPasswordAsync(code, NewPassword, Now)).Succeeded);
            }
        }
    }
}

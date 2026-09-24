using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TripleTriadApi.Data;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// A mail transport that records instead of sending — the same idea as the recording match notifier the CPU and
    /// timeout tests use. Mail becomes a value that can be asserted on, and no SMTP server is ever involved.
    ///
    /// It doubles as the development transport's stand-in for anything that only needs *a* sender: the controller
    /// tests build one of these even though they never send.
    /// </summary>
    public sealed class RecordingEmailSender : IEmailSender
    {
        /// <summary>Everything handed to the sender, in order.</summary>
        public List<EmailMessage> Sent { get; } = [];

        /// <summary>
        /// When set, sending throws. Used to prove that a mailbox failure cannot change the response: a broken mailbox
        /// must not become another way to tell which addresses have accounts.
        /// </summary>
        public Exception? Failure { get; set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);

            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    /// <summary>
    /// Builds a <see cref="PasswordResetService"/> over an InMemory database, with the clock supplied per call — the
    /// style the other service tests use (no host, no timer, no network).
    /// </summary>
    internal static class PasswordRecoveryTestHarness
    {
        public static PasswordResetService CreateService(
            TripleTriadContext context,
            IEmailSender emailSender,
            PasswordResetOptions? options = null,
            AppOptions? appOptions = null,
            IPlayerRepository? playerRepository = null
        ) =>
            new(
                playerRepository ?? new PlayerRepository(context),
                new PasswordResetRepository(context),
                new PasswordHasherService(),
                emailSender,
                Options.Create(options ?? new PasswordResetOptions()),
                Options.Create(appOptions ?? new AppOptions()),
                NullLogger<PasswordResetService>.Instance
            );
    }
}

using Microsoft.Extensions.Logging;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The development transport: it "sends" mail by writing it to the log.
    ///
    /// In Development this is not a fallback, it <em>is</em> the delivery mechanism — so the whole password-recovery
    /// flow can be built, run and demonstrated with no mailbox, no app password and no configuration at all. A
    /// developer reads the code out of the console instead of an inbox. That is the entire reason this class exists,
    /// and it is why the message body (which contains a live recovery code) is logged in full here while
    /// <see cref="SmtpEmailSender"/> logs only the subject.
    ///
    /// It is also the shape tests lean on: because mail arrives as a value rather than as a network side effect, a
    /// test double is a two-line subclass and no SMTP server is ever needed.
    ///
    /// The log level is <c>Warning</c> on purpose: this must be impossible to mistake for a real send, and it stands
    /// out in a scroll of <c>Information</c> lines.
    /// </summary>
    public class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            var block =
                $"{Environment.NewLine}--- development email: NOT actually sent ---{Environment.NewLine}"
                + $"To:      {message.To}{Environment.NewLine}"
                + $"Subject: {message.Subject}{Environment.NewLine}{Environment.NewLine}"
                + message.TextBody
                + $"{Environment.NewLine}--- end of development email ---{Environment.NewLine}";

            logger.LogWarning("{EmailBlock}", block);

            return Task.CompletedTask;
        }
    }
}

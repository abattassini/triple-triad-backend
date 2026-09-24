using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Sends mail through the configured SMTP server — today the dedicated Gmail account, tomorrow whatever relay the
    /// <c>Email:*</c> settings point at. See <see cref="IEmailSender"/> for why this is the only provider-aware class
    /// in the API, and why SMTP from a free mailbox is the chosen stopgap.
    ///
    /// Two decisions here are deliberate and worth reading before changing the file.
    ///
    /// <para><strong>1. It uses the base class library's <see cref="SmtpClient"/>, so the feature adds no NuGet
    /// dependency.</strong> That matches how this backend is otherwise built — <see cref="TokenService"/> hand-rolls
    /// its JWTs rather than taking a library. Microsoft's own documentation does note that <c>SmtpClient</c> "is not
    /// recommended for new development", and that is the trade-off being accepted: it is a client protocol with no
    /// quirk we depend on beyond STARTTLS and a credential. If it ever becomes a problem, drop in a MailKit or
    /// provider-HTTP implementation of <see cref="IEmailSender"/> and nothing outside this file changes.</para>
    ///
    /// <para><strong>2. It deliberately does NOT fail at startup when unconfigured.</strong> Contrast
    /// <see cref="TokenService"/>, whose secret is read during static initialization and throws if it is missing.
    /// This API has to boot and serve on a machine with no mailbox at all — a fresh clone, a test host, a review
    /// environment — so a missing password must surface as a clear error at <em>send</em> time. Do not "tidy this up"
    /// by moving the check into a constructor: that turns "recovery is unavailable" into "the game is down".</para>
    /// </summary>
    public class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
    {
        private readonly EmailOptions _options = options.Value;

        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            if (!_options.IsConfigured)
            {
                // A runtime failure with an actionable message; see point 2 in the class comment.
                throw new InvalidOperationException(
                    "Email is not configured, so the message was not sent. Set Email__Smtp__Host, "
                        + "Email__Smtp__Port, Email__Smtp__User, Email__Smtp__Password and Email__FromAddress "
                        + "(see .env.example and plans/password-recovery-plan.md §5)."
                );
            }

            // A fresh client per send: SmtpClient is not safe to share, and one message per instance avoids the
            // pooled-connection lifecycle entirely. Send volume here is a handful of messages a day.
            using var client = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port)
            {
                EnableSsl = true,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(_options.Smtp.User, _options.Smtp.Password),
            };

            using var mail = new MailMessage
            {
                // The From address must be the authenticated mailbox or the message fails SPF/DKIM alignment and is
                // quarantined — see IEmailSender's closing note.
                From = new MailAddress(_options.FromAddress, _options.FromName),
                Subject = message.Subject,
                Body = message.TextBody,
                IsBodyHtml = false,
            };

            mail.To.Add(message.To);

            await client.SendMailAsync(mail, cancellationToken);

            // The subject only. The body holds a live recovery code, and the recipient address is the player's
            // personal data — neither belongs in a log line that outlives the request.
            logger.LogInformation("Recovery email sent: \"{Subject}\".", message.Subject);
        }
    }
}

namespace TripleTriadApi.Services
{
    /// <summary>
    /// One outgoing message. Text-only on purpose: a plain-text body is both simpler and markedly less
    /// phishing-shaped than an HTML "click here to reset your password" — and this is exactly the kind of mail that
    /// spam filters scrutinise hardest when it arrives from a free-mail sender.
    ///
    /// There is also no <c>Bcc</c>, no attachments and no tracking pixel: recovery mail should contain nothing the
    /// sender would not want a player to read.
    /// </summary>
    public sealed record EmailMessage(string To, string Subject, string TextBody);

    /// <summary>
    /// The one thing in this API that knows how mail leaves the process.
    ///
    /// <para><strong>This interface is the swap point.</strong> Everything above it — the token table, the hashing,
    /// the time-to-live, the attempt cap, the layered rate limits — is transport-agnostic. Changing how mail is
    /// delivered must therefore never require touching any of it: add an implementation, change one DI registration
    /// in <c>Program.cs</c>, change configuration. Nothing else.</para>
    ///
    /// <para><strong>Why SMTP from a Gmail account, and not a transactional provider?</strong> Because this project
    /// owns no domain. Resend, Brevo, SendGrid, Mailgun and SES all refuse to send to arbitrary recipients until you
    /// have proved control of a sending domain (SPF + DKIM); the frontend lives on <c>github.io</c> and the API on a
    /// <c>koyeb.app</c> subdomain, so there are no DNS records we can publish. Sending through the mailbox's own SMTP
    /// server is what makes the mail authentic at all: Gmail signs it as <c>gmail.com</c>, so SPF and DKIM genuinely
    /// pass. The alternative — relaying via a provider with a free-mail <c>From:</c> — fails DMARC alignment and is
    /// explicitly called out in Google's sender guidelines ("Don't impersonate Gmail From: headers").
    /// See <c>plans/password-recovery-plan.md</c> §1 for the full decision record.</para>
    ///
    /// <para><strong>This is a stopgap, not a destination.</strong> The end state is a real domain plus a
    /// transactional relay. The good news is that Resend, Brevo, SendGrid and Mailgun <em>all</em> expose SMTP
    /// relays, so <see cref="SmtpEmailSender"/> already covers that move: it is configuration, not code. If a
    /// provider ever drops SMTP, add a native HTTP implementation here instead.</para>
    ///
    /// <para><strong>One rule that breaks deliverability silently if ignored:</strong> the <c>From:</c> address must
    /// be the mailbox that authenticated (see <see cref="EmailOptions.FromAddress"/>). Gmail will happily *send* a
    /// mismatched <c>From:</c>, and the message will then be quarantined by the recipient.</para>
    /// </summary>
    public interface IEmailSender
    {
        Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
    }
}

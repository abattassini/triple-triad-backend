namespace TripleTriadApi.Services
{
    /// <summary>
    /// Everything <see cref="SmtpEmailSender"/> needs, bound from the <c>Email</c> configuration section.
    ///
    /// The settings are deliberately <em>generic SMTP</em> (host / port / user / password) rather than Gmail-specific.
    /// The current deployment sends through a dedicated Gmail account because we own no domain
    /// (see <c>plans/password-recovery-plan.md</c> §1), but every paid transactional provider we evaluated —
    /// Resend, Brevo, SendGrid, Mailgun — also exposes an SMTP relay. Moving to one of those is therefore a change to
    /// these values and nothing else: no code, no redeploy of logic, no new sender class.
    /// </summary>
    public class EmailOptions
    {
        /// <summary>The configuration section these values bind from.</summary>
        public const string SectionName = "Email";

        public SmtpOptions Smtp { get; set; } = new();

        /// <summary>
        /// The address the mail is sent as. For Gmail this <strong>must</strong> be the same mailbox that authenticates
        /// against <see cref="SmtpOptions.User"/>: Gmail signs outbound mail as its own domain, and a mismatched
        /// <c>From:</c> breaks SPF/DKIM alignment and gets the message quarantined. Most relays require the same.
        /// </summary>
        public string FromAddress { get; set; } = string.Empty;

        /// <summary>The display name players see in their inbox.</summary>
        public string FromName { get; set; } = "Triple Triad";

        /// <summary>
        /// True when there is enough here to actually send. Callers use this to fail with a clear message instead of
        /// letting an unconfigured sender throw a confusing socket error — the app has to keep working without mail
        /// (see <see cref="SmtpEmailSender"/>), so "not configured" is a state to report, not a reason to crash.
        /// </summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Smtp.Host)
            && Smtp.Port > 0
            && !string.IsNullOrWhiteSpace(Smtp.User)
            && !string.IsNullOrWhiteSpace(Smtp.Password)
            && !string.IsNullOrWhiteSpace(FromAddress);
    }

    /// <summary>The SMTP server the API authenticates against.</summary>
    public class SmtpOptions
    {
        /// <summary>e.g. <c>smtp.gmail.com</c>.</summary>
        public string Host { get; set; } = string.Empty;

        /// <summary>587 for STARTTLS, which is what every provider in the plan's comparison documents.</summary>
        public int Port { get; set; } = 587;

        /// <summary>The mailbox that authenticates. For Gmail, the same address as <see cref="EmailOptions.FromAddress"/>.</summary>
        public string User { get; set; } = string.Empty;

        /// <summary>
        /// The app password (Gmail) or API key (a paid relay's SMTP endpoint). Supplied by configuration only —
        /// <c>Email__Smtp__Password</c> locally in <c>.env</c>, and a Koyeb secret in production. Never committed,
        /// never logged.
        /// </summary>
        public string Password { get; set; } = string.Empty;
    }
}

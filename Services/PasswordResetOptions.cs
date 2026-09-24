namespace TripleTriadApi.Services
{
    /// <summary>
    /// The password-recovery tunables, bound from the <c>PasswordReset</c> configuration section. They live here for
    /// the same reason the CPU opponent's live in <see cref="CpuOpponent"/> and the match deadlines in
    /// <see cref="MatchTimeouts"/>: one place to look, one place to change, and every value overridable by
    /// configuration so the limits can be tightened after an incident without a code change.
    ///
    /// The limits are <strong>layered on purpose</strong> (see <c>plans/password-recovery-plan.md</c> §6). A single
    /// global daily cap — the obvious first design — is itself a denial-of-service: one attacker sending that many
    /// requests would lock every player out of recovery for the rest of the day. So the per-account caps are the
    /// primary control (they protect the *player's* inbox, which is the thing we must not spam), the per-IP cap stops
    /// one script, and <see cref="GlobalPerDay"/> is only a generous backstop protecting the sending mailbox.
    /// </summary>
    public class PasswordResetOptions
    {
        /// <summary>The configuration section these values bind from.</summary>
        public const string SectionName = "PasswordReset";

        /// <summary>
        /// The name of the rate-limiting policy applied to the recovery endpoints. It lives next to the limits
        /// themselves so the policy registered in <c>Program.cs</c> and the attribute on the endpoint cannot drift
        /// apart.
        /// </summary>
        public const string RateLimitPolicyName = "password-reset";

        /// <summary>How long an emailed code stays valid. Short, because the code is the whole secret.</summary>
        public int CodeTtlMinutes { get; set; } = 15;

        /// <summary>
        /// How many password-reset submissions one source address may make per hour. Enforced by the rate-limiting
        /// middleware on the reset endpoint rather than in the database.
        ///
        /// This — not a per-token counter — is what makes a 50-bit code unguessable: guessing is only possible
        /// through this endpoint, and at ten tries an hour the expected time to stumble on a valid code exceeds the
        /// age of the universe. See <see cref="PasswordResetService"/> for why a per-token attempt counter cannot
        /// work at all.
        /// </summary>
        public int ResetAttemptsPerIpPerHour { get; set; } = 10;

        /// <summary>How many recovery mails one account may be sent per hour. The primary protection for the player's inbox.</summary>
        public int PerAccountPerHour { get; set; } = 3;

        /// <summary>How many recovery mails one account may be sent per day.</summary>
        public int PerAccountPerDay { get; set; } = 5;

        /// <summary>How many recovery requests one source address may make per hour. Stops a single script farming the endpoint.</summary>
        public int PerIpPerHour { get; set; } = 10;

        /// <summary>
        /// The backstop: total recovery mails per calendar day (UTC), across every account and every source.
        /// Deliberately generous — this is an attackable shared resource, so it must never be the first thing that
        /// trips. It exists to protect the sending mailbox's reputation and provider quota, and tripping it is
        /// logged as an error because it is either abuse or a sign the mailbox has been outgrown.
        /// </summary>
        public int GlobalPerDay { get; set; } = 200;
    }
}

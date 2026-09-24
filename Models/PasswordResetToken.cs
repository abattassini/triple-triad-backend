namespace TripleTriadApi.Models
{
    /// <summary>
    /// One issued password-recovery token: proof that whoever clicked the emailed link (or typed the emailed code) is
    /// entitled to set a new password on <see cref="PlayerLogin"/>'s account.
    ///
    /// The row deliberately stores a <em>hash</em> of the token rather than the token itself (see
    /// <see cref="TokenHash"/>), and it is consumed exactly once, so a leaked database is not a set of keys to every
    /// account — the same reasoning that makes <see cref="Player.PasswordHash"/> a hash rather than a password.
    ///
    /// These rows double as the rate-limit ledger: the per-account, per-IP and global caps in
    /// <see cref="Services.PasswordResetOptions"/> are all counts over this table's <see cref="CreatedAt"/>, so there
    /// is no second counter to drift out of step with reality (see <c>plans/password-recovery-plan.md</c> §6).
    /// </summary>
    public class PasswordResetToken
    {
        public int Id { get; set; }

        /// <summary>
        /// The login the token was issued to. A plain login rather than a foreign key because every other
        /// player reference in this schema (<c>PlayerHand.PlayerId</c>, <c>CardPlacement.PlayerId</c>, ...) is the
        /// login string too, and the login is immutable for a player's lifetime.
        /// </summary>
        public string PlayerLogin { get; set; } = string.Empty;

        /// <summary>
        /// SHA-256 (hex) of the <em>normalised</em> token — normalisation being upper-casing and stripping the
        /// display dashes, so the same code typed as <c>abcd-efgh-ij</c> or <c>ABCD EFGH IJ</c> hashes identically.
        ///
        /// The plaintext token is never persisted, so a database dump cannot be replayed to reset an account.
        /// Contrast <see cref="Player.PasswordHash"/>, which is BCrypt precisely because a *password* must be slow to
        /// verify; a reset token is high-entropy and short-lived, so a fast hash is the right tool and keeps the
        /// lookup a single indexed equality test.
        /// </summary>
        public string TokenHash { get; set; } = string.Empty;

        /// <summary>When the token was issued. Also the timestamp every rate-limit window is measured from.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>After this instant the token is dead, consumed or not.</summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>Set when the token is spent. Non-null means the token can never be used again.</summary>
        public DateTime? ConsumedAt { get; set; }

        /// <summary>
        /// The address the request came from. Kept for the per-IP limit (a count over this column) and for
        /// answering "was this really that player?" after the fact; it is never shown to a player.
        /// </summary>
        public string? RequestedFromIp { get; set; }

        /// <summary>
        /// True when the token is still usable at <paramref name="now"/>: issued, not yet spent, and not yet expired.
        /// The controller and the service both ask this rather than re-deriving it, so there is one definition.
        /// </summary>
        public bool IsUsableAt(DateTime now) => ConsumedAt is null && ExpiresAt > now;
    }
}

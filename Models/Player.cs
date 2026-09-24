namespace TripleTriadApi.Models
{
    public class Player
    {
        public int Id { get; set; }
        public string Login { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Which generation of this player's credentials a token belongs to. Written into every issued JWT as
        /// <c>session_version</c>, and compared against this value on each authenticated request; a mismatch retires
        /// the token.
        ///
        /// It exists because a JWT is otherwise trusted for its whole lifetime — 7 days — so a password reset would
        /// leave an attacker's stolen token working *after* the victim had changed their password, which is the one
        /// thing the reset is supposed to prevent. Bumping this number on a reset invalidates every session the
        /// player has, everywhere, without storing a token blacklist.
        /// See <c>plans/password-recovery-plan.md</c> §7.
        /// </summary>
        public int SessionVersion { get; set; }

        // Progression & economy (see plans/player-stats-economy-plan.md)
        public int Coins { get; set; }
        public int Experience { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Ties { get; set; }

        // Null until an avatar is chosen; the frontend shows a placeholder.
        public string? AvatarUrl { get; set; }
    }
}
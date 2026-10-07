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
        /// See <c>plans/PLAN-013-password-recovery/plan.md</c> §7.
        /// </summary>
        public int SessionVersion { get; set; }

        // Progression & economy (see plans/PLAN-002-player-stats-economy/plan.md)
        public int Coins { get; set; }
        public int Experience { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Ties { get; set; }

        // Null until an avatar is chosen; the frontend shows a placeholder.
        public string? AvatarUrl { get; set; }

        /// <summary>
        /// True for a bot: an emulated player the backend plays on behalf of
        /// (plans/PLAN-025-bots/plan.md). A bot is an ordinary <see cref="Player"/> row — it has a profile, a record
        /// and a live online flag, and every screen that draws a player draws a bot too — but no desktop is behind it,
        /// so it never signs in and its moves are played by the turn engine. False for every human, which is also the
        /// column default, so an existing row can never be mistaken for a bot.
        /// </summary>
        public bool IsBot { get; set; }

        /// <summary>
        /// How active a bot is, 0–100: the share of the time it is online (plans/PLAN-025-bots/plan.md §3.2). The
        /// presence rule reads it and nothing else does; it is meaningless (0) for a human. Seeded low for now — no
        /// bot idles online all day — but the column itself allows the whole range, so the ceiling can be raised later
        /// without a migration.
        /// </summary>
        public int Activity { get; set; }
    }
}

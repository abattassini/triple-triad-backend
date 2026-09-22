namespace TripleTriadApi.Services
{
    /// <summary>
    /// How long each part of a match may stall before <see cref="MatchTimeoutService"/> settles it. The tunables live
    /// in one place, the same style as PackService's prices and MatchRewardService's reward table, and the cutoff
    /// helpers keep the sweep and the read-only checks working from exactly the same arithmetic.
    ///
    /// Agreed 2026-09-21: a waiting match nobody joins is abandoned after ten minutes; an active match whose five
    /// cards never arrive is abandoned two minutes after it went active; a match with no placement for three minutes
    /// ends as a forfeit in favour of the player who stayed.
    /// </summary>
    public static class MatchTimeouts
    {
        public static readonly TimeSpan WaitingForOpponent = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan HandPick = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan TurnIdle = TimeSpan.FromMinutes(3);

        /// <summary>How often the sweep looks for expired matches — it also runs once at startup.</summary>
        public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(20);

        public static DateTime WaitingCutoff(DateTime now) => now - WaitingForOpponent;

        public static DateTime HandPickCutoff(DateTime now) => now - HandPick;

        public static DateTime TurnIdleCutoff(DateTime now) => now - TurnIdle;
    }
}

using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Picks the <see cref="MoveScorer"/> a <see cref="CPUPlayingProfile"/> scores with
    /// (plans/PLAN-029-cpu-playing-profiles/plan.md). It is the single place a profile is added, and the default arm is
    /// <see cref="CPUPlayingProfile.Decent"/> — so an unknown value (or a cohort the seeder wrote before profiles
    /// existed) plays exactly as the bot always has instead of throwing.
    /// </summary>
    public static class CPUPlayingProfileScorers
    {
        private static readonly MoveScorer Decent = new DecentMoveScorer();

        private static readonly MoveScorer Formidable = new FormidableMoveScorer(
            new DecentMoveScorer()
        );

        /// <summary>The scorer for <paramref name="profile"/>.</summary>
        public static MoveScorer For(CPUPlayingProfile profile) =>
            profile switch
            {
                CPUPlayingProfile.Formidable => Formidable,
                _ => Decent,
            };
    }
}

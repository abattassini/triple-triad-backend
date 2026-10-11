namespace TripleTriadApi.Models
{
    /// <summary>
    /// How a bot plays — the personality the turn engine scores its moves with
    /// (plans/PLAN-029-cpu-playing-profiles/plan.md). It lives on <see cref="Player.CpuPlayingProfile"/> as an int and
    /// defaults to <see cref="Decent"/>, so every existing row (and every human) plays exactly the way the bot always
    /// has.
    /// </summary>
    public enum CPUPlayingProfile
    {
        /// <summary>
        /// The original (and default) bot: one ply, it takes the captures it can see and keeps its strong cards in the
        /// cells hardest to attack from. This member reproduces that scoring exactly — it is the guard the refactor
        /// had to pass.
        /// </summary>
        Decent = 0,

        /// <summary>
        /// Decent plus two considerations: it values the cards it renders permanently uncapturable, and the cards its
        /// own remaining hand could win back. See <see cref="Services.BotMoveSelector"/> for the weights.
        /// </summary>
        Formidable = 1,
    }
}

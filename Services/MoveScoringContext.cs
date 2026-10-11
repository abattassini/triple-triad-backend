using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Everything a profile's scorer needs to score one candidate move (plans/PLAN-029-cpu-playing-profiles/plan.md):
    /// the move the enumeration resolved, the board it was resolved against, the cards the actor could still play, and
    /// who is playing. It is deliberately a plain value — a scorer computes, it never writes — so the same context can
    /// be handed to any profile.
    ///
    /// <see cref="PlayableHand"/> still contains the card this candidate plays; a scorer that looks ahead at the hand
    /// excludes it itself (the played card cannot win itself back).
    /// </summary>
    public sealed record MoveScoringContext(
        int MatchId,
        GameLogicService.MoveOutcome Outcome,
        IReadOnlyCollection<CardPlacement> Board,
        IReadOnlyList<PlayerHand> PlayableHand,
        string Actor
    );
}

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The original bot (plans/PLAN-012-cpu-opponent/plan.md §5.2): it takes the captures it can see and keeps its
    /// strong cards where they are hardest to attack, scored
    /// <c>captures * CaptureWeight − cardStrength * exposedSides</c> (a corner is attacked from two sides, the centre
    /// from four). This expression is the profile's whole definition (plans/PLAN-029-cpu-playing-profiles/plan.md), so
    /// the Decent profile is exactly the bot that existed before profiles did.
    /// </summary>
    public sealed class DecentMoveScorer : MoveScorer
    {
        public override int Score(MoveScoringContext context) =>
            context.Outcome.Result.CapturedCards.Count * CaptureWeight
            - Strength(context.Outcome.Card) * ExposedSides(context.Outcome.X, context.Outcome.Y);
    }
}

namespace TripleTriadApi.Models
{
    /// <summary>
    /// Optional rules that can be enabled per match. Stored as a bitmask in
    /// <see cref="Match.Rules"/> so several rules can be combined without a schema change when
    /// more rules are added later.
    /// </summary>
    [Flags]
    public enum MatchRule
    {
        /// <summary>No special rules are enabled (the default for every existing match).</summary>
        None = 0,

        /// <summary>
        /// SAME: when the played card collides with two or more neighbours (see
        /// <c>GameLogicService.TryCaptureNeighbor</c>) and the touching values are equal
        /// (attack == defense) in at least two of those collisions, the tied neighbour cards are
        /// captured by the player who played the card.
        /// </summary>
        Same = 1 << 0,

        // Reserved for the rules that follow — the capture pipeline in GameLogicService is
        // already split into phases so each of these is an extra evaluation phase rather than a
        // rewrite:
        //   Combo    = 1 << 1,  // chains the cards captured by Same/Plus
        //   Plus     = 1 << 2,  // two collisions with the same attack + defense sum
        //   SameWall = 1 << 3,  // board edges count as A for the Same rule
    }
}

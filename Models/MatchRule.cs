namespace TripleTriadApi.Models
{
    /// <summary>
    /// Special rules that can be enabled on a match. A rule is enabled when it appears in the
    /// match's <see cref="Match.Rules"/> list, so there is no "none" member: an empty list means the
    /// match is played with the basic rules only.
    /// </summary>
    public enum MatchRule
    {
        /// <summary>
        /// SAME: when the played card collides with two or more neighbours — the neighbour notion
        /// used by <c>GameLogicService.GetCollisions</c> — and the touching values are equal
        /// (attack == defense) in at least two of those collisions, the tied neighbour cards are
        /// captured by the player who played the card.
        /// </summary>
        Same = 1,

        // Rules that will follow. The capture pipeline in GameLogicService is already split into
        // phases, so each of these is an extra evaluation phase rather than a rewrite:
        //   Combo,    // chains the cards captured by Same/Plus
        //   Plus,     // two collisions with the same attack + defense sum
        //   SameWall, // board edges count as A for the Same rule
    }
}

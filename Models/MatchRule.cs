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
        /// SAME (FF8): when the played card collides with two or more neighbours — the neighbour notion
        /// used by <c>GameLogicService.GetCollisions</c>, own cards included — and the touching values
        /// are equal (attack == defense) in at least two of those collisions, the tied cards flip to the
        /// player who played the card. A tie with one of the player's own cards counts toward the
        /// two-or-more but is already that player's, so at least one tied neighbour has to belong to the
        /// opponent.
        /// </summary>
        Same = 1,

        /// <summary>
        /// PLUS (FF8): when two or more collisions share the same touching-value sum (attack + defense),
        /// the cards involved in those matching sums flip to the player who played the card. The rank
        /// comparison is irrelevant, so PLUS can flip a neighbour that beats the played card on that
        /// side. A collision with the player's own card counts toward the sum match but is already that
        /// player's, so at least one involved neighbour has to belong to the opponent. SAME is evaluated
        /// first, so a card both rules would flip is reported as SAME's.
        /// </summary>
        Plus = 2,

        /// <summary>
        /// SAME WALL (FF8): the board's outer wall counts as a card with an A (see
        /// <c>GameLogicService.WallValue</c>) on the side facing the board, so a wall is a collision like any
        /// other neighbour for the SAME rule. A wall tie therefore happens when the played card's value on a
        /// side touching the wall is A, and it counts toward the two-or-more ties SAME needs. A wall has no
        /// owner, so it can never be captured and never counts as the opponent's card: at least one tied
        /// neighbour still has to be the opponent's. Independent of <see cref="Same"/> — this rule works with
        /// SAME disabled — and evaluated after it, so it can only add captures, never take them away.
        /// </summary>
        SameWall = 3,

        /// <summary>
        /// PLUS WALL (FF8): the board's outer wall counts as a card with an A (see
        /// <c>GameLogicService.WallValue</c>) on the side facing the board for the PLUS rule, so a wall
        /// contributes the sum <c>played value + A</c> for any played value and can be one of the two members
        /// of a matching sum. A wall has no owner, so it is never captured and never counts as the opponent's
        /// card. Independent of <see cref="Plus"/> — this rule works with PLUS disabled — and evaluated after
        /// it, so it can only add captures, never take them away.
        /// </summary>
        PlusWall = 4,

        // Rules that will follow. The capture pipeline in GameLogicService is already split into
        // phases, so each of these is an extra evaluation phase rather than a rewrite:
        //   Combo, // chains the cards captured by Same/Plus/SameWall/PlusWall
    }
}

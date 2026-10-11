using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The collection a player needs before they can be in a match at all
    /// (plans/PLAN-030-minimum-cards-and-board-menu/plan.md): enough <em>different</em> cards to field a hand. Every
    /// way into a match asks this — create, join, Quick Match, the bot fallback, sending a challenge and answering one
    /// — so the rule and its wording live in one place instead of in six guards.
    ///
    /// A player under the threshold is not stuck: a new account is created with no cards and
    /// <c>PackService.StartingPacks</c> unopened packs, which is why the refusal points at My Packs.
    /// </summary>
    public class MatchEligibilityService(IPlayerCardRepository playerCards)
    {
        private readonly IPlayerCardRepository _playerCards = playerCards;

        /// <summary>
        /// How many distinct cards a player must own to play: one hand's worth. It is
        /// <see cref="GameLogicService.HandSize"/> rather than a literal, so the threshold can never drift from the
        /// size of the hand a player is asked to pick.
        /// </summary>
        public const int RequiredCards = GameLogicService.HandSize;

        /// <summary>The one sentence every entry point refuses with.</summary>
        public const string NotEnoughCardsMessage =
            "You need at least 5 different cards to play. Open a pack to get more.";

        /// <summary>
        /// The refusal when it is somebody else — the friend being invited — who cannot play. It names them, because the
        /// sentence is read by whoever tried to start the match, not by them.
        /// </summary>
        public static string CannotPlayMessage(string login) =>
            $"{login} cannot play yet — they need at least {RequiredCards} different cards.";

        /// <summary>
        /// Whether <paramref name="login"/> may take part in a match. The count is the distinct-card count of the
        /// collection — extra copies of one card do not help — read straight from the owning table.
        /// </summary>
        public async Task<bool> CanPlayAsync(string login) =>
            await _playerCards.GetOwnedCardCountAsync(login) >= RequiredCards;
    }
}

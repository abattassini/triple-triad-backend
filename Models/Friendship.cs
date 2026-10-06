namespace TripleTriadApi.Models
{
    /// <summary>
    /// The two values <see cref="Friendship.Status"/> may hold, spelled once so the service that writes them, the
    /// repository that filters on them and the client's mapping all agree. A string in the database rather than an
    /// enum, the same shape <c>Match.Status</c> has.
    /// </summary>
    public static class FriendshipStatus
    {
        /// <summary>Asked, not yet answered: one side has requested and the other has not replied either way.</summary>
        public const string Pending = "pending";

        /// <summary>Both sides have agreed — the friendship the friend list will show.</summary>
        public const string Accepted = "accepted";
    }

    /// <summary>
    /// The four answers a panel can get about a pair, **from the caller's point of view** — the vocabulary the API
    /// speaks (a `friendship` on the profile, a `friendshipState` on a notification). Distinct from
    /// <see cref="FriendshipStatus"/>, which is what the row holds: `pending` is two different screens depending on
    /// who is asking.
    /// </summary>
    public static class FriendshipStates
    {
        /// <summary>No row at all: the two have never asked each other.</summary>
        public const string None = "none";

        /// <summary>The caller asked, and has not been answered.</summary>
        public const string Requested = "requested";

        /// <summary>The other player asked, and the caller has not answered.</summary>
        public const string Incoming = "incoming";

        /// <summary>Accepted — either way round, which is the point of the single row.</summary>
        public const string Friends = "friends";
    }

    /// <summary>
    /// One friendship between two players — **one row per unordered pair** (<see cref="PlayerA"/> &lt;
    /// <see cref="PlayerB"/>, ordinal compare), which is what makes the relationship symmetric by construction: a
    /// request in either direction lands on the same row, and no pair can ever hold two.
    ///
    /// The pair is held in that canonical order rather than in the order it was asked, so the row cannot say who asked
    /// through its layout — that is <see cref="RequestedBy"/>'s job, and it is what lets a panel tell "you asked" from
    /// "you were asked".
    ///
    /// Both columns hold a login, the identifier the game layer, the JWT subject and every other game table use, and
    /// there is deliberately no FK to <see cref="Player"/>, exactly like <see cref="PlayerCard"/>,
    /// <see cref="PlayerPack"/>, <see cref="PlayerHand"/> and <see cref="CardPlacement"/>
    /// (plans/PLAN-022-notifications-and-friends/plan.md §3.1).
    /// </summary>
    public class Friendship
    {
        public int Id { get; set; }

        /// <summary>The lexicographically smaller of the two logins (ordinal compare).</summary>
        public string PlayerA { get; set; } = string.Empty;

        /// <summary>The lexicographically larger of the two logins (ordinal compare).</summary>
        public string PlayerB { get; set; } = string.Empty;

        /// <summary><see cref="FriendshipStatus.Pending"/> or <see cref="FriendshipStatus.Accepted"/>.</summary>
        public string Status { get; set; } = FriendshipStatus.Pending;

        /// <summary>The login that asked — always one of the two above, never a third party.</summary>
        public string RequestedBy { get; set; } = string.Empty;

        /// <summary>When the request was made.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>When it was accepted; null while it is still pending.</summary>
        public DateTime? RespondedAt { get; set; }
    }
}

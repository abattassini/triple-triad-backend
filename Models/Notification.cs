namespace TripleTriadApi.Models
{
    /// <summary>
    /// The kinds of notification this application writes. A string in the database rather than an enum, so a new kind
    /// is a data change and an older client renders its generic line instead of failing to read the row
    /// (plans/PLAN-022-notifications-and-friends/plan.md §3.1).
    /// </summary>
    public static class NotificationTypes
    {
        /// <summary>Somebody asked the recipient to be friends. Carries the friendship in <see cref="Notification.SubjectId"/>.</summary>
        public const string FriendRequest = "friend_request";

        /// <summary>The recipient's own request was accepted. Carries the friendship in <see cref="Notification.SubjectId"/>.</summary>
        public const string FriendAccepted = "friend_accepted";

        /// <summary>
        /// Somebody challenged the recipient to a match. Carries the **match** in <see cref="Notification.SubjectId"/>,
        /// and is answerable from the panel until that match stops being `pending`
        /// (plans/PLAN-027-friend-challenge/plan.md §3.6).
        /// </summary>
        public const string MatchChallenge = "match_challenge";
    }

    /// <summary>
    /// One thing that happened to one player, in their inbox: who did it, what kind of thing it was, and which domain
    /// row it is about. <c>NotificationService</c> is the only producer; the bell and the notification panel are the
    /// readers.
    ///
    /// The table is deliberately generic — the two kinds above are all that exist today, and the point of the shape is
    /// that a third is an insert rather than a migration.
    ///
    /// <see cref="RecipientId"/> and <see cref="ActorId"/> hold logins, like every other game table here, and there is
    /// deliberately no FK to <see cref="Player"/> — nor to whatever row <see cref="SubjectId"/> names. A notification
    /// is a historical record: removing the friendship it is about cannot remove the fact that the request happened.
    /// </summary>
    public class Notification
    {
        public int Id { get; set; }

        /// <summary>Whose inbox this lands in — a login.</summary>
        public string RecipientId { get; set; } = string.Empty;

        /// <summary>Which kind of thing this is: one of <see cref="NotificationTypes"/>.</summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>Who caused it — a login, and never the recipient.</summary>
        public string ActorId { get; set; } = string.Empty;

        /// <summary>The domain row this is about (for the two friend kinds: the <see cref="Friendship"/>'s id).</summary>
        public int? SubjectId { get; set; }

        /// <summary>
        /// Kind-specific extras, stored as jsonb. **Reserved**: no kind writes one yet, and the column exists so that
        /// the first kind needing to carry more than "who did what" does not need a migration to do it.
        /// </summary>
        public string? Payload { get; set; }

        /// <summary>When it was written.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the recipient saw it; null while it is still asking for attention, which is also exactly what the
        /// badge counts.
        /// </summary>
        public DateTime? ReadAt { get; set; }
    }
}

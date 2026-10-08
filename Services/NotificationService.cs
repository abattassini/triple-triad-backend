using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// What a `match_challenge` notification means to its recipient **now** — the stamp the panel draws its buttons
    /// from, computed from the match's current status (plans/PLAN-027-friend-challenge/plan.md §3.6), exactly as
    /// <see cref="FriendshipStates"/> is computed for a friend kind.
    /// </summary>
    public static class ChallengeStates
    {
        /// <summary>The match is still `pending`: the invitation is the recipient's to accept or refuse.</summary>
        public const string Pending = "pending";

        /// <summary>The match went `active`: it is being played.</summary>
        public const string Accepted = "accepted";

        /// <summary>The recipient declined the invitation.</summary>
        public const string Refused = "refused";

        /// <summary>It was cancelled, superseded, or ran out its twenty minutes.</summary>
        public const string Expired = "expired";

        /// <summary>The match row is gone. Never expected — but a missing row is not an error.</summary>
        public const string Gone = "gone";
    }

    /// <summary>
    /// The one way a notification is written, and the only thing that knows an inbox has a badge
    /// (plans/PLAN-022-notifications-and-friends/plan.md §3.3). Every kind goes through <see cref="CreateAsync"/>, so a
    /// kind added later inherits the row, the read state and the push with no new plumbing.
    ///
    /// Two rules live here rather than in the schema: **a second identical notification is not written while the first
    /// is unread** (asking twice must not queue twice), and **the push carries only the new count** — the row is the
    /// message, and the client re-reads.
    /// </summary>
    public class NotificationService
    {
        /// <summary>How many rows the panel asks for when it does not say.</summary>
        public const int DefaultPageSize = 20;

        /// <summary>The most one page will ever hold, whatever the caller asks for.</summary>
        public const int MaxPageSize = 50;

        private readonly INotificationRepository _notifications;
        private readonly IFriendshipRepository _friendships;
        private readonly IPlayerRepository _players;
        private readonly IPlayerNotifier _notifier;
        private readonly IGameRepository _games;

        public NotificationService(
            INotificationRepository notifications,
            IFriendshipRepository friendships,
            IPlayerRepository players,
            IPlayerNotifier notifier,
            IGameRepository games
        )
        {
            _notifications = notifications;
            _friendships = friendships;
            _players = players;
            _notifier = notifier;
            _games = games;
        }

        /// <summary>
        /// Writes a notification for one recipient about one actor, unless the same
        /// (recipient, kind, actor, subject) is already sitting unread — in which case that row is returned and
        /// nothing is written. Idempotent by design: every caller may call this on every path without checking first.
        /// </summary>
        public async Task<Notification> CreateAsync(
            string recipientId,
            string type,
            string actorId,
            int? subjectId,
            string? payload = null
        )
        {
            var outstanding = await _notifications.FindUnreadAsync(
                recipientId,
                type,
                actorId,
                subjectId
            );

            if (outstanding is not null)
            {
                // Already in the inbox, unanswered — a second row would be the same sentence twice.
                return outstanding;
            }

            var notification = await _notifications.CreateAsync(
                recipientId,
                type,
                actorId,
                subjectId,
                payload
            );

            // The hint, sent once per row written. The duplicate above deliberately stays quiet: nothing changed.
            await PushAsync(recipientId);

            return notification;
        }

        /// <summary>
        /// One page of the inbox, newest first, plus the badge's number — one answer, because the bell wants both.
        ///
        /// Each row is stamped with the pair's **current** state, and a request that has since been answered or
        /// withdrawn is left out of the page entirely: what a player wants from this list is what is still theirs to
        /// deal with, not a line no button can act on (§3.4). The row itself survives — a notification is a record of
        /// something that happened — it is simply not listed (<see cref="IsStillListed"/>).
        /// <paramref name="limit"/> of 0 answers the count alone, which is what a badge refresh costs.
        /// </summary>
        public async Task<NotificationPage> GetPageAsync(
            string recipientId,
            int? limit,
            int? beforeId
        )
        {
            var pageSize = Math.Clamp(limit ?? DefaultPageSize, 0, MaxPageSize);
            var rows =
                pageSize == 0
                    ? []
                    : await _notifications.GetPageAsync(recipientId, pageSize, beforeId);

            var unreadCount = await _notifications.CountUnreadAsync(recipientId);

            // The friendships the page's friend-kind rows are about, in one query rather than one per row.
            var friendshipIds = rows.Where(row => row.SubjectId is not null)
                .Select(row => row.SubjectId!.Value)
                .Distinct()
                .ToList();
            var friendships = (await _friendships.FindByIdsAsync(friendshipIds)).ToDictionary(f =>
                f.Id
            );

            var avatars = new Dictionary<string, string?>(StringComparer.Ordinal);
            // The matches the page's challenge rows are about, cached across the page's rows.
            var challengeMatches = new Dictionary<int, Match?>();
            var entries = new List<NotificationEntry>(rows.Count);

            foreach (var row in rows)
            {
                if (!avatars.TryGetValue(row.ActorId, out var avatarUrl))
                {
                    // The actor's avatar, so a notification draws exactly what that player's own profile would. Read
                    // per actor and cached for the page: an inbox is a handful of rows and usually one or two names,
                    // which is what keeps this a couple of reads instead of one per row.
                    avatarUrl = (await _players.FindByLoginAsync(row.ActorId))?.AvatarUrl;
                    avatars[row.ActorId] = avatarUrl;
                }

                var isFriendKind =
                    row.Type == NotificationTypes.FriendRequest
                    || row.Type == NotificationTypes.FriendAccepted;

                var friendshipState = isFriendKind
                    ? FriendService.StateFor(
                        row.SubjectId is { } id && friendships.TryGetValue(id, out var friendship)
                            ? friendship
                            : null,
                        recipientId
                    )
                    : null;

                // A challenge row is stamped from the match it is about, read once per distinct id and cached the same
                // way the avatars are — a page is a handful of rows.
                string? challengeState = null;
                if (row.Type == NotificationTypes.MatchChallenge && row.SubjectId is { } matchId)
                {
                    if (!challengeMatches.TryGetValue(matchId, out var challengeMatch))
                    {
                        challengeMatch = await _games.GetMatchByIdAsync(matchId);
                        challengeMatches[matchId] = challengeMatch;
                    }

                    challengeState = ChallengeStateFor(challengeMatch);
                }

                if (!IsStillListed(row.Type, friendshipState, challengeState))
                {
                    // Answered, declined, withdrawn or expired since: there is nothing left to do about this one, so it
                    // leaves the inbox rather than sitting there as a line no button can act on.
                    continue;
                }

                entries.Add(new NotificationEntry(row, avatarUrl, friendshipState, challengeState));
            }

            return new NotificationPage(unreadCount, entries);
        }

        /// <summary>
        /// Whether a row still belongs in the inbox. Only a **request still waiting for this player** can fall out:
        /// once it has been accepted, declined or withdrawn it is no longer theirs to answer, so the list stops showing
        /// it. Nothing is deleted — the row is the record — and by then it is already stamped read by
        /// <see cref="MarkAnsweredAsync"/>, so the badge's number is unaffected either way.
        ///
        /// Everything else stays listed, `friend_accepted` included: that is news nobody acts on, and its row is how
        /// the requester learns the answer.
        /// </summary>
        private static bool IsStillListed(
            string type,
            string? friendshipState,
            string? challengeState
        ) =>
            type switch
            {
                NotificationTypes.FriendRequest => friendshipState == FriendshipStates.Incoming,
                // A challenge is listed only while it is still the recipient's to answer (§3.6).
                NotificationTypes.MatchChallenge => challengeState == ChallengeStates.Pending,
                _ => true,
            };

        /// <summary>
        /// What a challenge notification means right now, from the match's status
        /// (plans/PLAN-027-friend-challenge/plan.md §3.6). `waiting`/`completed`/`abandoned` all read as expired: none
        /// of them is an invitation anybody can still answer.
        /// </summary>
        private static string ChallengeStateFor(Match? match) =>
            match?.Status switch
            {
                "pending" => ChallengeStates.Pending,
                "active" => ChallengeStates.Accepted,
                "refused" => ChallengeStates.Refused,
                null => ChallengeStates.Gone,
                _ => ChallengeStates.Expired,
            };

        /// <summary>
        /// Marks one row read, for the recipient it belongs to. Returns the badge's new number, or null when that id
        /// is not this player's — a probed id then reads as "nothing here" rather than as "not yours" (§3.4).
        /// </summary>
        public async Task<int?> MarkReadAsync(string recipientId, int id)
        {
            var notification = await _notifications.FindForRecipientAsync(id, recipientId);

            if (notification is null)
            {
                return null;
            }

            if (notification.ReadAt is null)
            {
                await _notifications.MarkReadAsync([id], recipientId);
                await PushAsync(recipientId);
            }

            return await _notifications.CountUnreadAsync(recipientId);
        }

        /// <summary>
        /// Marks the whole inbox read — the panel's *Mark all read*. Answers how many rows it changed and the badge's
        /// new number, which is zero by construction but reported rather than assumed.
        /// </summary>
        public async Task<ReadAllResult> MarkAllReadAsync(string recipientId)
        {
            var marked = await _notifications.MarkAllReadAsync(recipientId);

            if (marked > 0)
            {
                await PushAsync(recipientId);
            }

            return new ReadAllResult(marked, 0);
        }

        /// <summary>
        /// Stamps the outstanding notification of one exact kind read. Used when an action answers it — accepting a
        /// request, or removing it — so a badge stops counting something already dealt with. The row itself stays: the
        /// panel still shows that the request happened, carrying a state that offers no action (§3.2).
        /// </summary>
        public async Task<int> MarkAnsweredAsync(
            string recipientId,
            string type,
            string actorId,
            int? subjectId
        )
        {
            var outstanding = await _notifications.FindUnreadAsync(
                recipientId,
                type,
                actorId,
                subjectId
            );

            if (outstanding is null)
            {
                return 0;
            }

            var marked = await _notifications.MarkReadAsync([outstanding.Id], recipientId);
            await PushAsync(recipientId);

            return marked;
        }

        /// <summary>The hint, always carrying the count as it stands now rather than a delta.</summary>
        private async Task PushAsync(string recipientId) =>
            await _notifier.NotificationsChangedAsync(
                recipientId,
                await _notifications.CountUnreadAsync(recipientId)
            );

        /// <summary>
        /// One row of the inbox, ready to draw: the notification itself, the actor's avatar and what the row means
        /// **now** (null for a kind that is not about a friendship).
        /// </summary>
        public sealed record NotificationEntry(
            Notification Notification,
            string? ActorAvatarUrl,
            string? FriendshipState,
            string? ChallengeState
        );

        /// <summary>A page of the inbox plus the badge's number.</summary>
        public sealed record NotificationPage(
            int UnreadCount,
            IReadOnlyList<NotificationEntry> Entries
        );

        /// <summary>What "mark all read" did.</summary>
        public sealed record ReadAllResult(int MarkedRead, int UnreadCount);
    }
}

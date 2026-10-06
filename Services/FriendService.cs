using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The rules of a friendship: who may ask whom, what a second ask means, and what an accept, a decline or an
    /// unfriending does to the row and to both inboxes
    /// (plans/PLAN-022-notifications-and-friends/plan.md §3.2). The controller is thin on purpose — it maps these
    /// results onto status codes and nothing else — and the state machine lives here so it can be tested without HTTP.
    /// </summary>
    public class FriendService
    {
        /// <summary>
        /// How many requests one player may have sent that nobody has answered. Not about politeness: the inbox is
        /// what is being protected, so one account cannot fill somebody's list with rows.
        /// </summary>
        public const int MaxPendingRequests = 50;

        /// <summary>The named rate-limit policy the friend actions carry (registered in <c>Program.cs</c>).</summary>
        public const string RateLimitPolicyName = "friend-requests";

        private readonly IFriendshipRepository _friendships;
        private readonly IPlayerRepository _players;
        private readonly NotificationService _notifications;

        public FriendService(
            IFriendshipRepository friendships,
            IPlayerRepository players,
            NotificationService notifications
        )
        {
            _friendships = friendships;
            _players = players;
            _notifications = notifications;
        }

        /// <summary>
        /// The caller-relative state of the pair, which is what a panel draws its button from — one indexed lookup.
        /// <see cref="StateFor"/> is the rule itself, kept pure so it can be tested on its own.
        /// </summary>
        public async Task<string> StateAsync(string caller, string other)
        {
            var friendship = await _friendships.FindPairAsync(caller, other);

            return StateFor(friendship, caller);
        }

        /// <summary>
        /// What a row means to the caller. The stored status is deliberately **not** the answer: a pending row is
        /// *requested* for the player who asked and *incoming* for the player who was asked, and those are two
        /// different screens.
        /// </summary>
        public static string StateFor(Friendship? friendship, string caller)
        {
            if (friendship is null)
            {
                return FriendshipStates.None;
            }

            if (friendship.Status == FriendshipStatus.Accepted)
            {
                return FriendshipStates.Friends;
            }

            return friendship.RequestedBy == caller
                ? FriendshipStates.Requested
                : FriendshipStates.Incoming;
        }

        /// <summary>
        /// Asks <paramref name="target"/> to be friends. Idempotent for the caller, and — the one generosity in the
        /// design — an ask that answers an outstanding request from the other side simply accepts it, so two players
        /// who each press *Add friend* end up friends rather than locked in two pending rows forever.
        /// </summary>
        public async Task<FriendResult> RequestAsync(string caller, string target)
        {
            if (string.Equals(caller, target, StringComparison.Ordinal))
            {
                return FriendResult.Fail(FriendFailure.Yourself, "You cannot add yourself as a friend.");
            }

            var player = await _players.FindByLoginAsync(target);
            if (player is null)
            {
                // Which is also how the CPU is refused rather than special-cased: `"AI"` is a sentinel with no row
                // behind it, so it is simply a login that is not a player.
                return FriendResult.Fail(FriendFailure.UnknownPlayer, $"No player called {target}.");
            }

            var friendship = await _friendships.FindPairAsync(caller, target);

            if (friendship is null)
            {
                if (await _friendships.CountPendingSentByAsync(caller) >= MaxPendingRequests)
                {
                    return FriendResult.Fail(
                        FriendFailure.TooManyRequests,
                        $"You already have {MaxPendingRequests} friend requests waiting for an answer."
                    );
                }

                friendship = await _friendships.CreateRequestAsync(caller, target);

                await _notifications.CreateAsync(
                    target,
                    NotificationTypes.FriendRequest,
                    caller,
                    friendship.Id
                );

                return FriendResult.Ok(player.Login, FriendshipStates.Requested);
            }

            if (friendship.Status == FriendshipStatus.Accepted)
            {
                return FriendResult.Ok(player.Login, FriendshipStates.Friends);
            }

            if (friendship.RequestedBy == caller)
            {
                // Asked twice: the row already says so, and the inbox is not told again (that is the dedupe in
                // NotificationService, asked through the same kind/actor/subject).
                return FriendResult.Ok(player.Login, FriendshipStates.Requested);
            }

            await AcceptAndAnnounceAsync(friendship, caller, player.Login);

            return FriendResult.Ok(player.Login, FriendshipStates.Friends);
        }

        /// <summary>
        /// Accepts an outstanding request from <paramref name="other"/>. Only that: an accept with nothing waiting is
        /// a conflict rather than a silent friendship, because a caller who presses *Accept* on a screen that was
        /// stale should be told, not quietly joined to somebody.
        /// </summary>
        public async Task<FriendResult> AcceptAsync(string caller, string other)
        {
            var player = await _players.FindByLoginAsync(other);
            if (player is null)
            {
                return FriendResult.Fail(FriendFailure.UnknownPlayer, $"No player called {other}.");
            }

            var friendship = await _friendships.FindPairAsync(caller, other);

            if (friendship is null || friendship.RequestedBy == caller)
            {
                // Nothing is waiting for this caller: either they have never been asked, or the row is their own ask
                // (which the ask endpoint answers, not this one).
                return FriendResult.Fail(
                    FriendFailure.Conflict,
                    $"{player.Login} has not asked to be your friend."
                );
            }

            if (friendship.Status == FriendshipStatus.Accepted)
            {
                return FriendResult.Fail(
                    FriendFailure.Conflict,
                    $"You and {player.Login} are already friends."
                );
            }

            await AcceptAndAnnounceAsync(friendship, caller, player.Login);

            return FriendResult.Ok(player.Login, FriendshipStates.Friends);
        }

        /// <summary>
        /// Removes whatever the two have — a decline, a cancellation and an unfriending are the same write, and the
        /// caller has already decided which one it is. Idempotent, so a stale panel's *Decline* is not an error, and
        /// it stamps both inboxes' outstanding request read so neither keeps offering an action on a row that is gone.
        /// </summary>
        public async Task<FriendResult> RemoveAsync(string caller, string other)
        {
            var player = await _players.FindByLoginAsync(other);
            if (player is null)
            {
                return FriendResult.Fail(FriendFailure.UnknownPlayer, $"No player called {other}.");
            }

            var friendship = await _friendships.FindPairAsync(caller, other);
            if (friendship is null)
            {
                return FriendResult.Ok(player.Login, FriendshipStates.None);
            }

            await _friendships.DeleteAsync(friendship);

            // The requester's copy of the request, and the answerer's — the row's id outlives it here on purpose: a
            // notification is a historical record, which is why nothing points at the friendship with a foreign key.
            await _notifications.MarkAnsweredAsync(
                other,
                NotificationTypes.FriendRequest,
                caller,
                friendship.Id
            );
            await _notifications.MarkAnsweredAsync(
                caller,
                NotificationTypes.FriendRequest,
                other,
                friendship.Id
            );

            return FriendResult.Ok(player.Login, FriendshipStates.None);
        }

        /// <summary>
        /// Accepts the row and tells the two inboxes what happened: the requester gets a `friend_accepted`, and the
        /// request the answering player was looking at is stamped read — a badge must not keep asking for something
        /// that is already answered.
        /// </summary>
        private async Task AcceptAndAnnounceAsync(Friendship friendship, string answerer, string requester)
        {
            await _friendships.AcceptAsync(friendship);

            await _notifications.CreateAsync(
                requester,
                NotificationTypes.FriendAccepted,
                answerer,
                friendship.Id
            );

            await _notifications.MarkAnsweredAsync(
                answerer,
                NotificationTypes.FriendRequest,
                requester,
                friendship.Id
            );
        }

        /// <summary>Why a friend action was refused — the controller turns this into a status code (§3.4).</summary>
        public enum FriendFailure
        {
            None,

            /// <summary>No such player — which is how the CPU sentinel is refused, without a special case.</summary>
            UnknownPlayer,

            /// <summary>The caller named themselves.</summary>
            Yourself,

            /// <summary>Nothing to accept, or the two are already friends.</summary>
            Conflict,

            /// <summary>Too many requests are already waiting for an answer.</summary>
            TooManyRequests,
        }

        /// <summary>
        /// What came of a friend action: the pair's state from the caller's point of view, the **stored** login of the
        /// other player (so a caller who guessed the casing is corrected by the answer), and — on a refusal — the
        /// sentence plus the kind of refusal. Shaped like <c>PackService.PurchaseResult</c>, with the code the
        /// controller needs to tell a 400 from a 404 from a 409.
        /// </summary>
        public sealed record FriendResult
        {
            public bool Succeeded { get; init; }

            /// <summary>The other player's login, as stored.</summary>
            public string Login { get; init; } = string.Empty;

            /// <summary>One of <see cref="FriendshipStates"/>, as it stands after the action.</summary>
            public string Friendship { get; init; } = FriendshipStates.None;

            public string? ErrorMessage { get; init; }

            public FriendFailure Failure { get; init; } = FriendFailure.None;

            public static FriendResult Ok(string login, string friendship) =>
                new()
                {
                    Succeeded = true,
                    Login = login,
                    Friendship = friendship,
                };

            public static FriendResult Fail(FriendFailure failure, string message) =>
                new() { Failure = failure, ErrorMessage = message };
        }
    }
}

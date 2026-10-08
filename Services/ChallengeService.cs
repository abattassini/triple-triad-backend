using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The lifecycle writes other services need from the challenge feature, without taking its whole graph
    /// (plans/PLAN-027-friend-challenge/plan.md §3.5). <see cref="ChallengeService"/> is the only implementation, and
    /// the seam exists so `MatchmakingService` and the timeout sweep can expire an invitation without a service cycle.
    /// </summary>
    public interface IPendingChallenges
    {
        /// <summary>
        /// Ends every `pending` challenge this player is in — **either** seat (plans/PLAN-027-friend-challenge/plan.md
        /// §3.2 #5 for a sign-out, §13 for starting any match at all). One match per player is the rule both callers
        /// are enforcing.
        /// </summary>
        Task<int> ExpireInvolvingAsync(string login);

        /// <summary>Ends every `pending` challenge past the window (§3.2 #4).</summary>
        Task<int> ExpireTimedOutAsync(DateTime now);
    }

    /// <summary>
    /// Challenging a friend to a match (plans/PLAN-027-friend-challenge/plan.md §3.4): the rules for sending one, for
    /// answering it, and for the ways it ends on its own. The controller is thin on purpose — it maps these results
    /// onto status codes and nothing else — and the state machine lives here so it can be tested without HTTP, exactly
    /// as <see cref="FriendService"/> is.
    ///
    /// A challenge **is** a `Matches` row (§1.2): `pending` while it waits for an answer, `active` once accepted,
    /// `refused` when declined, `abandoned` when cancelled or expired. The invited player's inbox row carries that
    /// match in its `SubjectId`, which is what lets the panel answer it later (§3.6).
    /// </summary>
    public class ChallengeService(
        IGameRepository games,
        IPlayerRepository players,
        IPlayerPresence presence,
        FriendService friends,
        NotificationService notifications,
        IPlayerNotifier notifier,
        GameLogicService gameLogic,
        IRandomSource random
    ) : IPendingChallenges
    {
        private readonly IGameRepository _games = games;
        private readonly IPlayerRepository _players = players;
        private readonly IPlayerPresence _presence = presence;
        private readonly FriendService _friends = friends;
        private readonly NotificationService _notifications = notifications;
        private readonly IPlayerNotifier _notifier = notifier;
        private readonly GameLogicService _gameLogic = gameLogic;
        private readonly IRandomSource _random = random;

        /// <summary>
        /// Sends <paramref name="challengedLogin"/> an invitation: only a **friend who is online**, never yourself, and
        /// only while you are free yourself (§3.3). The row is written before anything is pushed, so an invitation that
        /// nobody answers still exists to be read later.
        /// </summary>
        public async Task<ChallengeResult> ChallengeAsync(
            string challenger,
            string challengedLogin,
            DateTime now
        )
        {
            var challenged = challengedLogin.Trim();

            if (string.Equals(challenged, challenger, StringComparison.Ordinal))
            {
                return ChallengeResult.Fail(
                    ChallengeFailure.Yourself,
                    "You cannot challenge yourself."
                );
            }

            var challengedPlayer = await _players.FindByLoginAsync(challenged);
            if (challengedPlayer is null)
            {
                return ChallengeResult.Fail(ChallengeFailure.UnknownPlayer, "No such player.");
            }

            if (
                await _friends.StateAsync(challenger, challengedPlayer.Login)
                != FriendshipStates.Friends
            )
            {
                return ChallengeResult.Fail(
                    ChallengeFailure.NotFriends,
                    "You can only challenge your friends."
                );
            }

            if (!_presence.IsOnline(challengedPlayer.Login))
            {
                return ChallengeResult.Fail(
                    ChallengeFailure.Offline,
                    $"{challengedPlayer.Login} is not online."
                );
            }

            // Sending an invitation is starting something (§13): everything else still holding the challenger goes —
            // their own outstanding invitations, so that sending again supersedes them (§3.2 #6), **and** any
            // invitation they had received. This runs *before* the busy check, so an invitation already outstanding is
            // not itself the reason a new one is refused.
            await ExpireInvolvingAsync(challenger);

            if (await IsBusyAsync(challenger))
            {
                return ChallengeResult.Fail(
                    ChallengeFailure.Busy,
                    "Finish your current match first."
                );
            }

            var match = await _games.CreateChallengeMatchAsync(
                challenger,
                challengedPlayer.Login,
                now
            );

            // The row is durable: it is what the bell counts, and what the invited player can still answer later.
            await _notifications.CreateAsync(
                challengedPlayer.Login,
                NotificationTypes.MatchChallenge,
                challenger,
                match.Id
            );

            // The push is the extra: it opens the dialog straight away, and only for a player free to take it (§1.4).
            // Someone in a match reads the invitation in their inbox instead.
            if (!await IsBusyAsync(challengedPlayer.Login))
            {
                await _notifier.ChallengeReceivedAsync(
                    challengedPlayer.Login,
                    match.Id,
                    challenger
                );
            }

            return ChallengeResult.Ok(match);
        }

        /// <summary>
        /// Accepts the invitation: the match turns `active`, the opener is drawn now that both seats are playing, and
        /// the challenger is told so that their own picker opens (§3.4 #1).
        /// </summary>
        public async Task<ChallengeResult> AcceptAsync(int matchId, string challenged, DateTime now)
        {
            var match = await _games.GetMatchByIdAsync(matchId);

            var guard = GuardAnswer(match, challenged);
            if (guard is not null)
            {
                return guard;
            }

            // The window is enforced here as well as by the sweep, so a stale dialog cannot revive an old invitation.
            if (match!.CreatedAt <= MatchTimeouts.PendingChallengeCutoff(now))
            {
                await EndAsync(match);
                return ChallengeResult.Fail(
                    ChallengeFailure.NotPending,
                    "That challenge has expired."
                );
            }

            if (await IsBusyAsync(challenged))
            {
                return ChallengeResult.Fail(
                    ChallengeFailure.Busy,
                    "Finish your current match first."
                );
            }

            match.CurrentPlayerTurn = _gameLogic.GetStartingPlayer(
                match.Player1Id,
                match.Player2Id,
                _random
            );
            match.Status = "active";
            match.ActivatedAt = now;
            await _games.UpdateMatchAsync(match);

            await _notifications.MarkAnsweredAsync(
                match.Player2Id,
                NotificationTypes.MatchChallenge,
                match.Player1Id,
                match.Id
            );

            // Accepting is joining a match, so everything else they held goes (§13) — their own outstanding
            // invitations and any other invitation they had received. The one being accepted is `active` by now, so it
            // is not among them.
            await ExpireInvolvingAsync(challenged);

            await _notifier.ChallengeAcceptedAsync(match.Player1Id, match.Id);

            return ChallengeResult.Ok(match);
        }

        /// <summary>Declines the invitation — the match's status becomes `refused` (§1.6).</summary>
        public async Task<ChallengeResult> RefuseAsync(int matchId, string challenged)
        {
            var match = await _games.GetMatchByIdAsync(matchId);

            var guard = GuardAnswer(match, challenged);
            if (guard is not null)
            {
                return guard;
            }

            match!.Status = "refused";
            await _games.UpdateMatchAsync(match);

            await _notifications.MarkAnsweredAsync(
                match.Player2Id,
                NotificationTypes.MatchChallenge,
                match.Player1Id,
                match.Id
            );

            await _notifier.ChallengeRefusedAsync(match.Player1Id, match.Id);

            return ChallengeResult.Ok(match);
        }

        /// <summary>Withdraws the invitation before it is answered (§3.4 #3).</summary>
        public async Task<ChallengeResult> CancelAsync(int matchId, string challenger)
        {
            var match = await _games.GetMatchByIdAsync(matchId);

            if (match is null)
            {
                return ChallengeResult.Fail(ChallengeFailure.UnknownMatch, "No such match.");
            }

            if (!string.Equals(match.Player1Id, challenger, StringComparison.Ordinal))
            {
                return ChallengeResult.Fail(
                    ChallengeFailure.NotYours,
                    "That challenge is not yours to withdraw."
                );
            }

            if (match.Status != "pending")
            {
                return ChallengeResult.Fail(
                    ChallengeFailure.NotPending,
                    "That challenge is no longer waiting for an answer."
                );
            }

            match.Status = "abandoned";
            await _games.UpdateMatchAsync(match);

            await _notifications.MarkAnsweredAsync(
                match.Player2Id,
                NotificationTypes.MatchChallenge,
                match.Player1Id,
                match.Id
            );

            await _notifier.ChallengeCancelledAsync(match.Player2Id, match.Id);

            return ChallengeResult.Ok(match);
        }

        public async Task<int> ExpireInvolvingAsync(string login)
        {
            var pending = await _games.GetPendingChallengesForAsync(login);

            foreach (var match in pending)
            {
                await EndAsync(match);
            }

            return pending.Count;
        }

        public async Task<int> ExpireTimedOutAsync(DateTime now)
        {
            var expired = await _games.GetExpiredPendingChallengesAsync(
                MatchTimeouts.PendingChallengeCutoff(now)
            );

            foreach (var match in expired)
            {
                await EndAsync(match);
            }

            return expired.Count;
        }

        /// <summary>
        /// The guards an answer shares: the match must be a challenge that is **this** player's to answer, and it must
        /// still be `pending`. Returns the refusal, or null when the answer may proceed.
        /// </summary>
        private static ChallengeResult? GuardAnswer(Match? match, string challenged)
        {
            if (match is null)
            {
                return ChallengeResult.Fail(ChallengeFailure.UnknownMatch, "No such match.");
            }

            if (!string.Equals(match.Player2Id, challenged, StringComparison.Ordinal))
            {
                return ChallengeResult.Fail(
                    ChallengeFailure.NotYours,
                    "That challenge is not yours to answer."
                );
            }

            return match.Status != "pending"
                ? ChallengeResult.Fail(
                    ChallengeFailure.NotPending,
                    "That challenge is no longer waiting for an answer."
                )
                : null;
        }

        /// <summary>Whether this player is already in a match — `waiting` or `active`, either seat (§3.3).</summary>
        private async Task<bool> IsBusyAsync(string login) =>
            (await _games.GetUnfinishedMatchesForPlayerAsync(login)).Count > 0;

        /// <summary>
        /// The one way a pending challenge stops being pending **without being answered**: the row is abandoned, the
        /// invited player's inbox row is stamped answered — so it leaves the bell and stops counting — and both seats
        /// are told, because either of them may be looking at a dialog about it.
        /// </summary>
        private async Task EndAsync(Match match)
        {
            match.Status = "abandoned";
            await _games.UpdateMatchAsync(match);

            await _notifications.MarkAnsweredAsync(
                match.Player2Id,
                NotificationTypes.MatchChallenge,
                match.Player1Id,
                match.Id
            );

            await _notifier.ChallengeExpiredAsync(match.Player1Id, match.Id);
            await _notifier.ChallengeExpiredAsync(match.Player2Id, match.Id);
        }

        /// <summary>Why a challenge action was refused — the controller turns this into a status code (§3.4).</summary>
        public enum ChallengeFailure
        {
            None,

            /// <summary>No such player.</summary>
            UnknownPlayer,

            /// <summary>The caller named themselves.</summary>
            Yourself,

            /// <summary>The two are not friends.</summary>
            NotFriends,

            /// <summary>The challenged player is not online.</summary>
            Offline,

            /// <summary>The acting player is already in a match.</summary>
            Busy,

            /// <summary>No such match.</summary>
            UnknownMatch,

            /// <summary>Not a challenge still awaiting an answer.</summary>
            NotPending,

            /// <summary>The caller is not the seat this action belongs to.</summary>
            NotYours,
        }

        /// <summary>
        /// What came of a challenge action: the match and its status, or — on a refusal — the sentence and the kind of
        /// refusal. Shaped like <see cref="FriendService.FriendResult"/>, with the codes the controller needs to tell a
        /// 400 from a 403 from a 404 from a 409.
        /// </summary>
        public sealed record ChallengeResult
        {
            public bool Succeeded { get; init; }

            /// <summary>The challenge's `Matches` row.</summary>
            public int MatchId { get; init; }

            /// <summary>That row's status as it stands after the action.</summary>
            public string Status { get; init; } = string.Empty;

            public string? ErrorMessage { get; init; }

            public ChallengeFailure Failure { get; init; } = ChallengeFailure.None;

            public static ChallengeResult Ok(Match match) =>
                new()
                {
                    Succeeded = true,
                    MatchId = match.Id,
                    Status = match.Status,
                };

            public static ChallengeResult Fail(ChallengeFailure failure, string message) =>
                new() { Failure = failure, ErrorMessage = message };
        }
    }
}

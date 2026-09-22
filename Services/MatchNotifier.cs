using Microsoft.AspNetCore.SignalR;
using TripleTriadApi.Hubs;
using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>One move the server played on a client's behalf — everything the pushes need to describe it.</summary>
    public sealed record MovePush(
        Match Match,
        GameLogicService.PlayCardResult Result,
        MatchRewardService.MatchRewardResult? Rewards,
        string PlayerId,
        int CardId,
        int X,
        int Y
    );

    /// <summary>
    /// The pushes a match needs from outside the hub: the hub only broadcasts what happens inside a connection, while
    /// filing a hand, cancelling a match, the timeout sweep and the CPU's own moves all run in a REST request or a
    /// background job. It is an interface because tests have no web host — they record the calls instead.
    /// </summary>
    public interface IMatchNotifier
    {
        /// <summary>A player's five cards are filed, so the opponent can stop waiting for them.</summary>
        Task HandReadyAsync(int matchId);

        /// <summary>The match can no longer be played — a deadline passed and it was abandoned.</summary>
        Task AbandonedAsync(int matchId, string reason);

        /// <summary>The match ended early — a forfeit — in the same shape the hub's GameCompleted already uses.</summary>
        Task CompletedEarlyAsync(
            Match match,
            MatchRewardService.MatchRewardResult? rewards,
            string reason
        );

        /// <summary>
        /// A move the server played for a client (the CPU): the same `CardPlayed` the hub sends for a human move, plus
        /// `GameCompleted` when that move filled the board.
        /// </summary>
        Task CardPlayedAsync(MovePush move);
    }

    public class SignalRMatchNotifier(IHubContext<GameHub> hub) : IMatchNotifier
    {
        private readonly IHubContext<GameHub> _hub = hub;

        public Task HandReadyAsync(int matchId) =>
            _hub.Clients.Group(GroupOf(matchId)).SendAsync("MatchReady", new { matchId });

        public Task AbandonedAsync(int matchId, string reason) =>
            _hub.Clients.Group(GroupOf(matchId)).SendAsync("MatchAbandoned", new { matchId, reason });

        public Task CompletedEarlyAsync(
            Match match,
            MatchRewardService.MatchRewardResult? rewards,
            string reason
        ) =>
            _hub
                .Clients.Group(GroupOf(match.Id))
                .SendAsync("GameCompleted", MatchPushes.Completed(match, rewards, reason));

        public async Task CardPlayedAsync(MovePush move)
        {
            var group = _hub.Clients.Group(GroupOf(move.Match.Id));

            await group.SendAsync(
                "CardPlayed",
                MatchPushes.CardPlayed(
                    move.Match,
                    move.Result,
                    move.PlayerId,
                    move.CardId,
                    move.X,
                    move.Y
                )
            );

            if (move.Result.IsGameComplete)
            {
                await group.SendAsync("GameCompleted", MatchPushes.Completed(move.Match, move.Rewards));
            }
        }

        private static string GroupOf(int matchId) => $"match-{matchId}";
    }
}

using Microsoft.AspNetCore.SignalR;
using TripleTriadApi.Hubs;
using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The pushes a match needs from outside the hub: the hub only broadcasts what happens inside a connection, while
    /// filing a hand, cancelling a match and the timeout sweep all run in a REST request or a background job. It is an
    /// interface because tests have no web host — they record the calls instead.
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
                .SendAsync(
                    "GameCompleted",
                    new
                    {
                        winnerId = match.WinnerId,
                        player1Score = match.Player1Score,
                        player2Score = match.Player2Score,
                        completedAt = match.CompletedAt,
                        // Absent on a played-out match, so clients can tell a forfeit from a finished game.
                        reason,
                        rewards =
                            rewards is null
                                ? null
                                : new
                                {
                                    player1Coins = rewards.Player1Coins,
                                    player1Experience = rewards.Player1Experience,
                                    player2Coins = rewards.Player2Coins,
                                    player2Experience = rewards.Player2Experience,
                                },
                    }
                );

        private static string GroupOf(int matchId) => $"match-{matchId}";
    }
}

using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Settles the matches whose deadlines have passed — the rules and the values live in <see cref="MatchTimeouts"/>:
    /// a waiting match nobody joined, an active match whose five cards never arrived, and a match nobody has moved in.
    ///
    /// It is the only writer of these outcomes (the read paths merely report <c>timedOut</c>), and it only ever
    /// touches a match that is still <c>waiting</c>/<c>active</c>, so a slow tick, a redeploy or two sweeps in a row
    /// can never abandon or pay a forfeit twice.
    /// </summary>
    public class MatchTimeoutService(IServiceScopeFactory scopeFactory) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // One pass straight away: a redeploy can leave matches that expired while the app was down.
            await SweepOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(MatchTimeouts.SweepInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepOnceAsync(stoppingToken);
            }
        }

        private async Task SweepOnceAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();

            await SweepAsync(
                scope.ServiceProvider.GetRequiredService<IGameRepository>(),
                scope.ServiceProvider.GetRequiredService<MatchRewardService>(),
                scope.ServiceProvider.GetRequiredService<IMatchNotifier>(),
                DateTime.UtcNow,
                cancellationToken
            );
        }

        /// <summary>
        /// One pass over both candidate sets. Dependency-explicit and <paramref name="now"/>-driven so the tests can
        /// drive it over an InMemory database with a scripted clock, no host and no timer.
        /// </summary>
        public static async Task SweepAsync(
            IGameRepository gameRepository,
            MatchRewardService rewardService,
            IMatchNotifier notifier,
            DateTime now,
            CancellationToken cancellationToken = default
        )
        {
            foreach (var match in await gameRepository.GetWaitingMatchesAsync())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!MatchStateService.IsTimedOut(match, false, null, now))
                {
                    continue;
                }

                await AbandonAsync(gameRepository, notifier, match, "the search timed out");
            }

            foreach (var match in await gameRepository.GetActiveMatchesAsync())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var handsReady =
                    MatchStateService.HasFiledHand(match, match.Player1Id)
                    && MatchStateService.HasFiledHand(match, match.Player2Id);
                var lastActivity =
                    match.CardPlacements.Count == 0
                        ? (DateTime?)null
                        : match.CardPlacements.Max(placement => placement.PlacedAt);

                if (!MatchStateService.IsTimedOut(match, handsReady, lastActivity, now))
                {
                    continue;
                }

                if (!handsReady)
                {
                    // S2, and only ever within `MatchTimeouts.HandPick` of activation: a hand that was never filed.
                    // (An in-progress match is *ready* — playing a card marks its row used, it does not un-file the
                    // hand — so a half-played game can never land here.)
                    await AbandonAsync(gameRepository, notifier, match, "the hand was never picked");
                    continue;
                }

                await ForfeitAsync(gameRepository, rewardService, notifier, match);
            }
        }

        /// <summary>S1/S2: nobody can play this match any more, and no game was really started.</summary>
        private static async Task AbandonAsync(
            IGameRepository gameRepository,
            IMatchNotifier notifier,
            Match match,
            string reason
        )
        {
            match.Status = "abandoned";
            await gameRepository.UpdateMatchAsync(match);
            await notifier.AbandonedAsync(match.Id, reason);
        }

        /// <summary>S3/S4: cards were ready, so the player who stayed wins it — the one who is not on the clock.</summary>
        private static async Task ForfeitAsync(
            IGameRepository gameRepository,
            MatchRewardService rewardService,
            IMatchNotifier notifier,
            Match match
        )
        {
            var loser = match.CurrentPlayerTurn;
            var winner = loser == match.Player1Id ? match.Player2Id : match.Player1Id;

            match.WinnerId = winner;
            match.Status = "completed";
            match.CompletedAt = DateTime.UtcNow;
            await gameRepository.UpdateMatchAsync(match);

            var rewards = await rewardService.AwardForMatchAsync(match);
            await notifier.CompletedEarlyAsync(match, rewards, "timeout");
        }
    }
}

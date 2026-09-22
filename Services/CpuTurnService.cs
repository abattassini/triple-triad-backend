using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Plays the CPU's turns. Every active match whose turn belongs to <see cref="CpuOpponent.Login"/> gets one move
    /// as soon as its thinking time has passed, played through <see cref="GamePlayService"/> — the same pipeline REST
    /// and SignalR use — and then pushed into the match group, so the human's board follows it exactly as it follows
    /// another person's.
    ///
    /// A sweep rather than a task scheduled per move, for the same reasons as <see cref="MatchTimeoutService"/>: an
    /// overdue move is simply due again after a restart or a redeploy, one pass can never overlap another, and the
    /// whole thing is a static method taking <c>now</c> so the tests drive it with no host and no timer.
    /// </summary>
    public class CpuTurnService(IServiceScopeFactory scopeFactory) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(CpuOpponent.PollInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using var scope = scopeFactory.CreateScope();

                await AdvanceAsync(
                    scope.ServiceProvider.GetRequiredService<IGameRepository>(),
                    scope.ServiceProvider.GetRequiredService<GamePlayService>(),
                    scope.ServiceProvider.GetRequiredService<CpuMoveSelector>(),
                    scope.ServiceProvider.GetRequiredService<IMatchNotifier>(),
                    scope.ServiceProvider.GetRequiredService<IRandomSource>(),
                    DateTime.UtcNow,
                    stoppingToken
                );
            }
        }

        /// <summary>
        /// One pass: every move that is due, at most one per match. Dependency-explicit and <paramref name="now"/>-driven
        /// so a test can drive it over an InMemory database with a scripted clock and a recording notifier.
        /// </summary>
        public static async Task AdvanceAsync(
            IGameRepository gameRepository,
            GamePlayService gamePlayService,
            CpuMoveSelector moveSelector,
            IMatchNotifier notifier,
            IRandomSource random,
            DateTime now,
            CancellationToken cancellationToken = default
        )
        {
            foreach (var match in await gameRepository.GetMatchesAwaitingTurnAsync(CpuOpponent.Login))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var board = match.CardPlacements.ToList();
                if (!CpuOpponent.IsMoveDue(match, board, now))
                {
                    continue;
                }

                var hand = match.PlayerHands.Where(row => row.PlayerId == CpuOpponent.Login).ToList();
                var move = moveSelector.Select(match, board, hand, random);
                if (move is null)
                {
                    continue;
                }

                // The shared pipeline, which re-reads the match: if a human move landed in between, this comes back as
                // a failure ("not your turn") and the next tick decides again from fresh data.
                var result = await gamePlayService.PlayCardAsync(
                    match.Id,
                    move.CardId,
                    move.X,
                    move.Y,
                    CpuOpponent.Login
                );

                if (!result.IsSuccess || result.GameResult is null || result.UpdatedMatch is null)
                {
                    continue;
                }

                await notifier.CardPlayedAsync(
                    new MovePush(
                        result.UpdatedMatch,
                        result.GameResult,
                        result.Rewards,
                        CpuOpponent.Login,
                        move.CardId,
                        move.X,
                        move.Y
                    )
                );
            }
        }
    }
}

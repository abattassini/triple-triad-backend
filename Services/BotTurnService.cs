using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Plays the bots' turns. Every active match whose turn belongs to a bot (plans/PLAN-025-bots/plan.md §3.4) gets
    /// one move as soon as its thinking time has passed, played through <see cref="GamePlayService"/> — the same
    /// pipeline REST and SignalR use — and then pushed into the match group, so the human's board follows it exactly
    /// as it follows another person's.
    ///
    /// A sweep rather than a task scheduled per move, for the same reasons as <see cref="MatchTimeoutService"/>: an
    /// overdue move is simply due again after a restart or a redeploy, one pass can never overlap another, and the
    /// whole thing is a static method taking <c>now</c> so the tests drive it with no host and no timer.
    /// </summary>
    public class BotTurnService(IServiceScopeFactory scopeFactory) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(BotOpponent.PollInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using var scope = scopeFactory.CreateScope();

                await AdvanceAsync(
                    scope.ServiceProvider.GetRequiredService<IGameRepository>(),
                    scope.ServiceProvider.GetRequiredService<GamePlayService>(),
                    scope.ServiceProvider.GetRequiredService<BotMoveSelector>(),
                    scope.ServiceProvider.GetRequiredService<IMatchNotifier>(),
                    scope.ServiceProvider.GetRequiredService<IRandomSource>(),
                    scope.ServiceProvider.GetRequiredService<BotRegistry>(),
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
            BotMoveSelector moveSelector,
            IMatchNotifier notifier,
            IRandomSource random,
            BotRegistry bots,
            DateTime now,
            CancellationToken cancellationToken = default
        )
        {
            var botLogins = bots.Bots.Select(bot => bot.Login).ToList();

            foreach (var match in await gameRepository.GetMatchesAwaitingBotTurnAsync(botLogins))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // The bot on turn is the match's current player — captured now, because playing flips the turn.
                var actor = match.CurrentPlayerTurn;
                if (string.IsNullOrEmpty(actor))
                {
                    continue;
                }

                var board = match.CardPlacements.ToList();
                if (!BotOpponent.IsMoveDue(match, board, now))
                {
                    continue;
                }

                var hand = match.PlayerHands.Where(row => row.PlayerId == actor).ToList();

                // The bot's own personality decides how its moves are scored (plans/PLAN-029-cpu-playing-profiles).
                var move = moveSelector.Select(
                    match,
                    board,
                    hand,
                    actor,
                    random,
                    bots.ProfileOf(actor)
                );
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
                    actor
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
                        actor,
                        move.CardId,
                        move.X,
                        move.Y
                    )
                );
            }
        }
    }
}

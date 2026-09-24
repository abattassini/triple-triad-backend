using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Finds a game for a player who asked for one, or starts one for them to be found in.
    ///
    /// <para><strong>Why this is its own type.</strong> Matchmaking used to be a conversation: the client asked
    /// <c>GET matches/waiting</c>, decided whether anyone compatible was waiting, and then either joined that match or
    /// created one of its own. The decision and the write therefore happened in two separate requests, and nothing
    /// stopped two players clicking Quick Match at the same moment from both deciding "nobody is waiting" before
    /// either had written anything. Both then created a match and neither ever looked again, so the two were left
    /// waiting for each other until the timeout sweep gave both up. A narrower window produced something worse: both
    /// players finding each other's waiting match and each joining the other's, leaving two <em>active</em> matches
    /// holding the same two players, each waiting for a hand from someone who was picking in the other one.</para>
    ///
    /// <para>The fix is to make the whole find-or-create a single server-side operation and to run it under a gate, so
    /// the second request cannot make its decision until the first has finished writing. The second player then always
    /// sees the first player's waiting match and joins it — the one match they both wanted.</para>
    ///
    /// <para><strong>Two gates, for two different reasons.</strong> The in-process semaphore is what makes this correct
    /// on a single instance, and it is also what makes the behaviour testable, since the suite runs on the InMemory
    /// provider where the database lock does not exist. The Postgres advisory lock is what makes it correct when more
    /// than one instance is serving traffic: a semaphore inside one process cannot hold off a request being handled by
    /// another. Neither is redundant — removing either one silently reintroduces the race it covers.</para>
    /// </summary>
    public class MatchmakingService(
        IGameRepository gameRepository,
        IMatchNotifier notifier,
        TripleTriadContext context,
        ILogger<MatchmakingService> logger
    )
    {
        /// <summary>
        /// Why a match is abandoned when its player starts another one — the wording the other side reads in the
        /// `MatchAbandoned` reason (the client renders it as "Match abandoned — ...").
        /// </summary>
        public const string AbandonedByNewSearchReason = "the other player started another match";

        /// <summary>
        /// The in-process half of the gate. Static on purpose: it has to hold off every request the process is
        /// handling, not merely every call that happens to share one instance of this service.
        /// </summary>
        private static readonly SemaphoreSlim InProcessGate = new(1, 1);

        /// <summary>
        /// The database half's key. The value only has to be fixed (every quick-match call contends on the same one)
        /// and distinct from any other advisory lock the app might take.
        /// </summary>
        private const long AdvisoryLockKey = 7_427_001;

        /// <summary>
        /// Gives up on every match the player is still in (`waiting` or `active`, in either seat) so that looking for
        /// a game is never refused because of an earlier one. The rows are abandoned rather than deleted — the terminal
        /// status is a status write — and whoever was waiting on them is told through the same `MatchAbandoned` push
        /// the timeout sweep sends, which is what stops the other side from waiting for a match that will never be
        /// played.
        ///
        /// It belongs to matchmaking rather than to any one endpoint because all three ways of starting a game
        /// (create, join by id, and quick match) have to do it, and a player may only ever be in one match.
        /// </summary>
        public async Task AbandonUnfinishedAsync(string playerId)
        {
            var unfinished = await gameRepository.GetUnfinishedMatchesForPlayerAsync(playerId);

            foreach (var match in unfinished)
            {
                match.Status = "abandoned";
                await gameRepository.UpdateMatchAsync(match);
                await notifier.AbandonedAsync(match.Id, AbandonedByNewSearchReason);
            }
        }

        /// <summary>
        /// The player's seat: the match they were put into — whether they were seated in one that was already waiting
        /// or became the one waiting to be found.
        ///
        /// Everything here runs inside the gate (see the class comment), so no other request can invalidate the answer
        /// between choosing and writing.
        /// </summary>
        public async Task<Match> FindOrCreateWaitingMatchAsync(
            string playerId,
            List<MatchRule> rules,
            DateTime now
        )
        {
            return await RunExclusiveAsync(async () =>
            {
                // Give up whatever they were still in first, so an earlier match cannot shadow this search.
                await AbandonUnfinishedAsync(playerId);

                // Each attempt either claims a match or finds none left to claim, so this cannot spin.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var candidate = await FindJoinableAsync(playerId, rules);

                    if (candidate is null)
                    {
                        break;
                    }

                    if (await gameRepository.TryClaimWaitingMatchAsync(candidate.Id, playerId, now))
                    {
                        return candidate;
                    }

                    // Losing the claim means two searches reached it at once, which the gate exists to prevent. Retry,
                    // but leave a trace: a weakened gate should show up in the logs rather than only in odd behaviour.
                    logger.LogWarning(
                        "Quick match lost the race for match {MatchId}; looking again.",
                        candidate.Id
                    );
                }

                // Nobody compatible is waiting, so start the match and be the one who is found.
                return await gameRepository.CreateMatchAsync(playerId, null, rules);
            });
        }

        /// <summary>
        /// The oldest waiting match that plays by exactly <paramref name="rules"/> and is not the caller's own.
        ///
        /// Filtered in memory rather than in SQL because a match's rules live in a converted string column that the
        /// schema notes cannot be used in a `WHERE`. The waiting set is tiny, and it is only ever read while holding
        /// the gate.
        /// </summary>
        private async Task<Match?> FindJoinableAsync(string playerId, List<MatchRule> rules)
        {
            var waiting = await gameRepository.GetWaitingMatchesAsync();

            return waiting.FirstOrDefault(match =>
                match.Player1Id != playerId && MatchRuleExtensions.SameSet(match.Rules, rules)
            );
        }

        /// <summary>
        /// Runs <paramref name="work"/> with every other matchmaking call held off — see the class comment for why
        /// both a process gate and a database gate are needed, and for what goes wrong without them.
        ///
        /// The advisory lock is transaction-scoped, so it is released when the transaction ends whether that is a
        /// commit or a rollback: a request that throws cannot leave it held.
        /// </summary>
        private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> work)
        {
            await InProcessGate.WaitAsync();

            try
            {
                if (!context.Database.IsRelational())
                {
                    // The in-memory provider has no advisory locks; the process gate above is the whole gate here, and
                    // it is what the concurrency tests exercise.
                    return await work();
                }

                await using var transaction = await context.Database.BeginTransactionAsync();

                await context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock({0})",
                    AdvisoryLockKey
                );

                var result = await work();

                await transaction.CommitAsync();

                return result;
            }
            finally
            {
                InProcessGate.Release();
            }
        }
    }
}

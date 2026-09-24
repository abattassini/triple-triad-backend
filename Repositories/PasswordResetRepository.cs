using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    /// <summary>
    /// Storage for password-recovery tokens.
    ///
    /// This table doubles as the rate-limit ledger (see <see cref="PasswordResetToken"/>), so the counting methods
    /// here are part of the security design rather than reporting — and nothing purges a row until it is far older
    /// than the longest limit window, or the caps would quietly loosen as the table was cleaned.
    /// </summary>
    public interface IPasswordResetRepository
    {
        Task<PasswordResetToken> CreateAsync(PasswordResetToken token);

        Task<PasswordResetToken?> FindByHashAsync(string tokenHash);

        /// <summary>
        /// Marks the token spent, but only if it has not been spent already. Returns false when it was — which is
        /// what makes the code single-use even if two submissions arrive at the same instant.
        /// </summary>
        Task<bool> ConsumeAsync(int tokenId, DateTime consumedAt);

        /// <summary>
        /// Spends every still-usable token for a player. Called when a new one is issued, so a player always has at
        /// most one live recovery code and an earlier email cannot be replayed after they request another.
        /// </summary>
        Task<int> RetireOutstandingAsync(string playerLogin, DateTime now);

        /// <summary>
        /// How many tokens have been issued since <paramref name="since"/>, optionally narrowed to one account or one
        /// source address. This is how every layered limit in <see cref="Services.PasswordResetOptions"/> is measured.
        /// </summary>
        Task<int> CountIssuedSinceAsync(DateTime since, string? playerLogin = null, string? requestedFromIp = null);

        /// <summary>Deletes rows past <see cref="PasswordResetRepository.LedgerRetention"/>. Returns how many went.</summary>
        Task<int> PurgeStaleAsync(DateTime now);
    }

    public class PasswordResetRepository(TripleTriadContext context) : IPasswordResetRepository
    {
        /// <summary>
        /// How long an expired or spent token is kept before it is purged.
        ///
        /// It must stay longer than the longest rate-limit window (a day, plus slack), because those limits are
        /// counts over this table. Purging on the token's own expiry — the obvious choice, since the row is useless
        /// by then — would delete rows still inside the windows and quietly raise the effective limits. Two days is
        /// comfortably past every window and still keeps the table small.
        /// </summary>
        public static readonly TimeSpan LedgerRetention = TimeSpan.FromHours(48);

        public async Task<PasswordResetToken> CreateAsync(PasswordResetToken token)
        {
            context.PasswordResetTokens.Add(token);
            await context.SaveChangesAsync();

            return token;
        }

        public async Task<PasswordResetToken?> FindByHashAsync(string tokenHash)
        {
            return await context.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash);
        }

        public async Task<bool> ConsumeAsync(int tokenId, DateTime consumedAt)
        {
            if (context.Database.IsInMemory())
            {
                // The in-memory provider has no bulk update, so the guarded read-modify-write below is the only
                // option there — the same accommodation PlayerRepository.TrySpendCoinsAsync makes for coins.
                var tracked = await context.PasswordResetTokens.FirstOrDefaultAsync(t => t.Id == tokenId);
                if (tracked is null || tracked.ConsumedAt is not null)
                {
                    return false;
                }

                tracked.ConsumedAt = consumedAt;
                await context.SaveChangesAsync();

                return true;
            }

            // One guarded statement: the row only changes while it is still unspent, so two requests racing on the
            // same code can never both win.
            var consumed = await context
                .PasswordResetTokens.Where(t => t.Id == tokenId && t.ConsumedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.ConsumedAt, consumedAt));

            return consumed > 0;
        }

        public async Task<int> RetireOutstandingAsync(string playerLogin, DateTime now)
        {
            var outstanding = await context
                .PasswordResetTokens.Where(t => t.PlayerLogin == playerLogin && t.ConsumedAt == null)
                .ToListAsync();

            if (outstanding.Count == 0)
            {
                return 0;
            }

            foreach (var token in outstanding)
            {
                token.ConsumedAt = now;
            }

            await context.SaveChangesAsync();

            return outstanding.Count;
        }

        public Task<int> CountIssuedSinceAsync(
            DateTime since,
            string? playerLogin = null,
            string? requestedFromIp = null
        )
        {
            var query = context.PasswordResetTokens.Where(t => t.CreatedAt >= since);

            if (playerLogin is not null)
            {
                query = query.Where(t => t.PlayerLogin == playerLogin);
            }

            if (requestedFromIp is not null)
            {
                query = query.Where(t => t.RequestedFromIp == requestedFromIp);
            }

            return query.CountAsync();
        }

        public async Task<int> PurgeStaleAsync(DateTime now)
        {
            var cutoff = now - LedgerRetention;

            var stale = await context.PasswordResetTokens.Where(t => t.ExpiresAt < cutoff).ToListAsync();

            if (stale.Count == 0)
            {
                return 0;
            }

            context.PasswordResetTokens.RemoveRange(stale);
            await context.SaveChangesAsync();

            return stale.Count;
        }
    }
}

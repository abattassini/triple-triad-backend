using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    /// <summary>
    /// The unopened packs a player holds (the inventory half of the card shop). One row per pack code with a
    /// quantity, mirroring <see cref="IPlayerCardRepository"/> on the collection side.
    /// </summary>
    public interface IPlayerPackRepository
    {
        /// <summary>Every stack the player holds, ascending by pack code. Empty stacks are never stored.</summary>
        Task<List<PlayerPack>> GetForPlayerAsync(string playerId);

        /// <summary>How many packs of <paramref name="packCode"/> the player holds (0 when they hold none).</summary>
        Task<int> GetCountAsync(string playerId, string packCode);

        /// <summary>Adds one pack of <paramref name="packCode"/>, creating the stack on the first purchase.</summary>
        Task<PlayerPack> GrantAsync(string playerId, string packCode);

        /// <summary>
        /// Opens one pack: a guarded decrement returning the packs left, or <c>null</c> when the player holds
        /// none — in which case nothing is written. The stack is removed once its last pack is opened and the
        /// quantity can never go negative, the same guarantee <c>TrySpendCoinsAsync</c> gives the wallet.
        /// </summary>
        Task<int?> TryConsumeAsync(string playerId, string packCode);
    }

    public class PlayerPackRepository(TripleTriadContext context) : IPlayerPackRepository
    {
        private readonly TripleTriadContext _context = context;

        public async Task<List<PlayerPack>> GetForPlayerAsync(string playerId)
        {
            return await _context
                .PlayerPacks.Where(pack => pack.PlayerId == playerId && pack.Quantity > 0)
                .OrderBy(pack => pack.PackCode)
                .ToListAsync();
        }

        public async Task<int> GetCountAsync(string playerId, string packCode)
        {
            // Projected to a scalar so this read is one cheap query rather than a tracked entity load.
            return await _context
                .PlayerPacks.Where(pack => pack.PlayerId == playerId && pack.PackCode == packCode)
                .Select(pack => pack.Quantity)
                .FirstOrDefaultAsync();
        }

        public async Task<PlayerPack> GrantAsync(string playerId, string packCode)
        {
            var now = DateTime.UtcNow;
            var stack = await _context.PlayerPacks.FirstOrDefaultAsync(pack =>
                pack.PlayerId == playerId && pack.PackCode == packCode
            );

            if (stack is null)
            {
                stack = new PlayerPack
                {
                    PlayerId = playerId,
                    PackCode = packCode,
                    Quantity = 1,
                    FirstAcquiredAt = now,
                    LastAcquiredAt = now,
                };

                _context.PlayerPacks.Add(stack);
            }
            else
            {
                stack.Quantity++;
                stack.LastAcquiredAt = now;
            }

            await _context.SaveChangesAsync();

            return stack;
        }

        public async Task<int?> TryConsumeAsync(string playerId, string packCode)
        {
            if (_context.Database.IsInMemory())
            {
                // The in-memory provider has no bulk update, so the guarded read-modify-write below is the only
                // option there — acceptable because every row lives inside one process.
                var tracked = await _context.PlayerPacks.FirstOrDefaultAsync(pack =>
                    pack.PlayerId == playerId && pack.PackCode == packCode
                );

                if (tracked is null || tracked.Quantity < 1)
                {
                    return null;
                }

                tracked.Quantity--;

                if (tracked.Quantity == 0)
                {
                    // An emptied stack is removed rather than left behind at zero.
                    _context.PlayerPacks.Remove(tracked);
                }

                await _context.SaveChangesAsync();

                return tracked.Quantity;
            }

            // One guarded statement: the row only changes when a pack is actually held, so two requests racing
            // on the same inventory can never take it below zero.
            var consumed = await _context
                .PlayerPacks.Where(pack =>
                    pack.PlayerId == playerId && pack.PackCode == packCode && pack.Quantity > 0
                )
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(pack => pack.Quantity, pack => pack.Quantity - 1)
                );

            if (consumed == 0)
            {
                return null;
            }

            // Clean-up for the bulk path: an emptied stack is dropped, so the inventory only ever lists packs the
            // player still holds (the InMemory branch above does the same by removing the tracked row).
            await _context
                .PlayerPacks.Where(pack =>
                    pack.PlayerId == playerId && pack.PackCode == packCode && pack.Quantity <= 0
                )
                .ExecuteDeleteAsync();

            // Read the quantity back as a scalar so the fresh value is reported even though the bulk update
            // bypassed the change tracker.
            return await GetCountAsync(playerId, packCode);
        }
    }
}

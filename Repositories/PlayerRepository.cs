using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    public interface IPlayerRepository
    {
        Task<Player?> FindByLoginAsync(string login);
        Task<Player?> FindByEmailAsync(string email);
        Task<bool> LoginExistsAsync(string login);
        Task<bool> EmailExistsAsync(string email);
        Task<Player> CreateAsync(Player player);
        Task<Player> UpdateAsync(Player player);

        /// <summary>
        /// Spends <paramref name="amount"/> coins for the player, but only when the balance covers it.
        /// Returns the balance after the spend, or <c>null</c> when the player does not exist or cannot
        /// afford it — in which case nothing is written.
        /// </summary>
        Task<int?> TrySpendCoinsAsync(string login, int amount);
    }

    public class PlayerRepository(TripleTriadContext context) : IPlayerRepository
    {
        private readonly TripleTriadContext _context = context;

        public async Task<Player?> FindByLoginAsync(string login)
        {
            return await _context.Players.FirstOrDefaultAsync(p => p.Login == login);
        }

        public async Task<Player?> FindByEmailAsync(string email)
        {
            return await _context.Players.FirstOrDefaultAsync(p => p.Email == email);
        }

        public async Task<bool> LoginExistsAsync(string login)
        {
            return await _context.Players.AnyAsync(p => p.Login == login);
        }

        public async Task<bool> EmailExistsAsync(string email)
        {
            return await _context.Players.AnyAsync(p => p.Email == email);
        }

        public async Task<Player> CreateAsync(Player player)
        {
            _context.Players.Add(player);
            await _context.SaveChangesAsync();

            return player;
        }

        public async Task<Player> UpdateAsync(Player player)
        {
            _context.Players.Update(player);
            await _context.SaveChangesAsync();

            return player;
        }

        public async Task<int?> TrySpendCoinsAsync(string login, int amount)
        {
            if (_context.Database.IsInMemory())
            {
                // The in-memory provider has no bulk update, so the guarded read-modify-write below is the only
                // option there — acceptable because every row lives inside one process.
                var tracked = await _context.Players.FirstOrDefaultAsync(p => p.Login == login);
                if (tracked is null || tracked.Coins < amount)
                {
                    return null;
                }

                tracked.Coins -= amount;
                await _context.SaveChangesAsync();

                return tracked.Coins;
            }

            // One guarded statement: the row only changes when the player can afford the spend, so two
            // requests racing on the same wallet can never take it below zero.
            var spent = await _context
                .Players.Where(p => p.Login == login && p.Coins >= amount)
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(p => p.Coins, p => p.Coins - amount)
                );

            if (spent == 0)
            {
                return null;
            }

            // Read the balance back as a scalar so the fresh value is reported even though the bulk update
            // bypassed the change tracker.
            return await _context
                .Players.Where(p => p.Login == login)
                .Select(p => p.Coins)
                .FirstOrDefaultAsync();
        }
    }
}

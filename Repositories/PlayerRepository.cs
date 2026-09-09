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
    }
}
using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    /// <summary>
    /// The cards a player owns outside of a match (the collection).
    /// </summary>
    public interface IPlayerCardRepository
    {
        /// <summary>Every card the player owns, with its quantity, ordered by card level then card id.</summary>
        Task<List<PlayerCard>> GetForPlayerAsync(string playerId);

        /// <summary>
        /// Files the drawn cards: +1 for a card the player already owns, a new row (quantity 1) otherwise.
        /// A card listed twice counts twice and still ends up in a single row.
        /// </summary>
        Task AddOrIncrementManyAsync(string playerId, IReadOnlyList<int> cardIds);
    }

    public class PlayerCardRepository(TripleTriadContext context) : IPlayerCardRepository
    {
        private readonly TripleTriadContext _context = context;

        public async Task<List<PlayerCard>> GetForPlayerAsync(string playerId)
        {
            return await _context
                .PlayerCards.Include(pc => pc.Card)
                .Where(pc => pc.PlayerId == playerId)
                .OrderBy(pc => pc.Card.Level)
                .ThenBy(pc => pc.CardId)
                .ToListAsync();
        }

        public async Task AddOrIncrementManyAsync(string playerId, IReadOnlyList<int> cardIds)
        {
            if (cardIds.Count == 0)
            {
                return;
            }

            var drawnCardIds = cardIds.Distinct().ToList();
            var owned = await _context
                .PlayerCards.Where(pc =>
                    pc.PlayerId == playerId && drawnCardIds.Contains(pc.CardId)
                )
                .ToDictionaryAsync(pc => pc.CardId);

            var now = DateTime.UtcNow;

            foreach (var cardId in cardIds)
            {
                // A card drawn twice in the same pack hits this branch on its second occurrence.
                if (owned.TryGetValue(cardId, out var existing))
                {
                    existing.Quantity++;
                    existing.LastAcquiredAt = now;
                    continue;
                }

                var created = new PlayerCard
                {
                    PlayerId = playerId,
                    CardId = cardId,
                    Quantity = 1,
                    FirstAcquiredAt = now,
                    LastAcquiredAt = now,
                };

                _context.PlayerCards.Add(created);

                // Track it immediately so a repeat draw in this pack increments the same row.
                owned[cardId] = created;
            }

            await _context.SaveChangesAsync();
        }
    }
}

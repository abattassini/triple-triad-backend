using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    /// <summary>How much of one card level a player owns: distinct cards held and the copies those represent.</summary>
    public sealed record LevelOwnership(int Level, int OwnedCount, int OwnedCopies);

    /// <summary>
    /// The cards a player owns outside of a match (the collection).
    /// </summary>
    public interface IPlayerCardRepository
    {
        /// <summary>
        /// The player's owned cards with their quantities, ordered by card level then card id. Pass
        /// <paramref name="level"/> to fetch a single level — the My Cards page loads one level at a time.
        /// </summary>
        Task<List<PlayerCard>> GetForPlayerAsync(string playerId, int? level = null);

        /// <summary>
        /// Per-level ownership for the player (distinct cards + copies held), ascending by level. Levels the
        /// player owns nothing in are absent, which is what the level picker lists.
        /// </summary>
        Task<List<LevelOwnership>> GetLevelOwnershipAsync(string playerId);

        /// <summary>
        /// How many distinct cards the player owns — one per holding, so extra copies of the same card do not
        /// inflate it. This is the "cards" figure the player profile (and its stats pill) reports.
        /// </summary>
        Task<int> GetOwnedCardCountAsync(string playerId);

        /// <summary>
        /// Which of <paramref name="cardIds"/> the player owns, projected to ids so a hand selection can be checked
        /// without loading the collection. The result may be shorter than the input — that is the failure signal.
        /// </summary>
        Task<List<int>> GetOwnedCardIdsAsync(string playerId, IReadOnlyCollection<int> cardIds);

        /// <summary>
        /// Files the drawn cards: +1 for a card the player already owns, a new row (quantity 1) otherwise.
        /// A card listed twice counts twice and still ends up in a single row.
        /// </summary>
        Task AddOrIncrementManyAsync(string playerId, IReadOnlyList<int> cardIds);
    }

    public class PlayerCardRepository(TripleTriadContext context) : IPlayerCardRepository
    {
        private readonly TripleTriadContext _context = context;

        public async Task<List<PlayerCard>> GetForPlayerAsync(string playerId, int? level = null)
        {
            var query = _context
                .PlayerCards.Include(pc => pc.Card)
                .Where(pc => pc.PlayerId == playerId);

            if (level is not null)
            {
                query = query.Where(pc => pc.Card.Level == level);
            }

            return await query.OrderBy(pc => pc.Card.Level).ThenBy(pc => pc.CardId).ToListAsync();
        }

        public async Task<List<LevelOwnership>> GetLevelOwnershipAsync(string playerId)
        {
            // Grouped in the database: the page only needs the counts, never the rows themselves.
            var rows = await _context
                .PlayerCards.Where(pc => pc.PlayerId == playerId)
                .GroupBy(pc => pc.Card.Level)
                .Select(group => new
                {
                    Level = group.Key,
                    OwnedCount = group.Count(),
                    OwnedCopies = group.Sum(pc => pc.Quantity),
                })
                .OrderBy(row => row.Level)
                .ToListAsync();

            return
            [
                .. rows.Select(row => new LevelOwnership(
                    row.Level,
                    row.OwnedCount,
                    row.OwnedCopies
                )),
            ];
        }

        public async Task<int> GetOwnedCardCountAsync(string playerId)
        {
            // One row per owned card (see the unique (PlayerId, CardId) index), so a plain row count is already
            // the distinct-card count — copies live in Quantity and never add rows.
            return await _context.PlayerCards.CountAsync(playerCard =>
                playerCard.PlayerId == playerId
            );
        }

        public async Task<List<int>> GetOwnedCardIdsAsync(
            string playerId,
            IReadOnlyCollection<int> cardIds
        )
        {
            if (cardIds.Count == 0)
            {
                return [];
            }

            return await _context
                .PlayerCards.Where(pc => pc.PlayerId == playerId && cardIds.Contains(pc.CardId))
                .Select(pc => pc.CardId)
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

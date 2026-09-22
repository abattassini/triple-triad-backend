using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    public interface IGameRepository
    {
        Task<List<Card>> GetAllCardsAsync();

        /// <summary>
        /// How many cards the catalogue holds per level (level → count). Used to show "3 of 23" for a level
        /// without loading the whole catalogue.
        /// </summary>
        Task<Dictionary<int, int>> GetCardCountsByLevelAsync();

        Task<Card?> GetCardByIdAsync(int cardId);
        Task<Match?> GetMatchByIdAsync(int matchId);

        /// <summary>
        /// Every match the player is still in — <c>waiting</c> or <c>active</c>, in either seat — oldest first.
        /// Starting a Quick Match gives up on all of them (<c>GameController</c>), so this is the list that decides
        /// what a new search abandons; nothing else is loaded with them, because only the status is written.
        /// </summary>
        Task<List<Match>> GetUnfinishedMatchesForPlayerAsync(string playerId);

        Task<Match> CreateMatchAsync(string player1Id, string? player2Id, List<MatchRule> rules);
        Task<Match> UpdateMatchAsync(Match match);
        Task UpdatePlayerHandAsync(PlayerHand playerHand);
        Task<List<CardPlacement>> GetCardPlacementsAsync(int matchId);
        Task<List<PlayerHand>> GetPlayerHandAsync(int matchId, string playerId);
        Task<CardPlacement> AddCardPlacementAsync(CardPlacement placement);
        Task<List<PlayerHand>> CreatePlayerHandsAsync(
            int matchId,
            string player1Id,
            string player2Id,
            List<Card> player1Cards,
            List<Card> player2Cards
        );
        Task UpdateCardPlacementOwnershipAsync(List<CardPlacement> placements);
        Task<List<Match>> GetWaitingMatchesAsync();

        /// <summary>The catalogue rows for the given ids — shorter than the input when an id does not exist.</summary>
        Task<List<Card>> GetCardsByIdsAsync(IReadOnlyCollection<int> cardIds);

        /// <summary>
        /// How many cards the player has **filed** in this match's hand, whether they have been played or not: the
        /// hand is filed once, so this reads 5 from the first pick onwards and never drops when a card leaves the
        /// hand for the board. It is the "this player brought a hand" test, never "how many cards are left".
        /// </summary>
        Task<int> GetFiledHandCountAsync(int matchId, string playerId);

        /// <summary>
        /// Files a player's hand in one go: their unused rows for the match are cleared first, so a retry after a
        /// failed request can never leave the hand doubled up. Rows already played are left untouched.
        /// </summary>
        Task<List<PlayerHand>> ReplacePlayerHandAsync(int matchId, string playerId, List<Card> cards);

        /// <summary>Every active match with its hands and placements — what the timeout sweep works from.</summary>
        Task<List<Match>> GetActiveMatchesAsync();

        /// <summary>
        /// The active matches in which <paramref name="playerId"/> is the opponent (player 2) and it is their turn —
        /// what the CPU's move engine works from, since the sentinel only ever plays that seat. The board and both
        /// hands come with their **cards**, because a move has to be evaluated against the real values (the timeout
        /// sweep's query loads neither: it only looks at counts and timestamps).
        /// </summary>
        Task<List<Match>> GetMatchesAwaitingTurnAsync(string playerId);
    }

    public class GameRepository(TripleTriadContext context) : IGameRepository
    {
        private readonly TripleTriadContext _context = context;

        public async Task<List<Card>> GetAllCardsAsync()
        {
            return await _context.Cards.ToListAsync();
        }

        public async Task<Dictionary<int, int>> GetCardCountsByLevelAsync()
        {
            // Counted in the database, so the collection summary never has to pull the catalogue.
            var rows = await _context
                .Cards.GroupBy(card => card.Level)
                .Select(group => new { Level = group.Key, Count = group.Count() })
                .ToListAsync();

            return rows.ToDictionary(row => row.Level, row => row.Count);
        }

        public async Task<Card?> GetCardByIdAsync(int cardId)
        {
            return await _context.Cards.FindAsync(cardId);
        }

        public async Task<Match?> GetMatchByIdAsync(int matchId)
        {
            return await _context
                .Matches.Include(m => m.CardPlacements)
                .ThenInclude(cp => cp.Card)
                .Include(m => m.PlayerHands)
                .ThenInclude(ph => ph.Card)
                .FirstOrDefaultAsync(m => m.Id == matchId);
        }

        public async Task<List<Match>> GetUnfinishedMatchesForPlayerAsync(string playerId)
        {
            return await _context
                .Matches.Where(m =>
                    (m.Player1Id == playerId || m.Player2Id == playerId)
                    && (m.Status == "waiting" || m.Status == "active")
                )
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();
        }

        public async Task<Match> CreateMatchAsync(
            string player1Id,
            string? player2Id,
            List<MatchRule> rules
        )
        {
            var match = new Match
            {
                Player1Id = player1Id,
                Player2Id = player2Id ?? string.Empty, // Empty string for waiting matches
                CurrentPlayerTurn = player1Id, // Player 1 starts
                Status = string.IsNullOrEmpty(player2Id)
                    ? "waiting"
                    : (player2Id == "AI" ? "active" : "active"),
                Player1Score = 5, // Both players start with 5 points (their 5 cards)
                Player2Score = 5,
                Rules = rules, // Rules the creator enabled for this match
                CreatedAt = DateTime.UtcNow,
            };

            _context.Matches.Add(match);
            await _context.SaveChangesAsync();

            return match;
        }

        public async Task<Match> UpdateMatchAsync(Match match)
        {
            _context.Matches.Update(match);
            await _context.SaveChangesAsync();
            return match;
        }

        public async Task UpdatePlayerHandAsync(PlayerHand playerHand)
        {
            _context.PlayerHands.Update(playerHand);
            await _context.SaveChangesAsync();
        }

        public async Task<List<CardPlacement>> GetCardPlacementsAsync(int matchId)
        {
            return await _context
                .CardPlacements.Include(cp => cp.Card)
                .Where(cp => cp.MatchId == matchId)
                .OrderBy(cp => cp.PlacedAt)
                .ToListAsync();
        }

        public async Task<List<PlayerHand>> GetPlayerHandAsync(int matchId, string playerId)
        {
            return await _context
                .PlayerHands.Include(ph => ph.Card)
                .Where(ph => ph.MatchId == matchId && ph.PlayerId == playerId && !ph.IsUsed)
                // Ordered by id, i.e. the order the cards were filed (creation, join, or a later pick): that is the
                // order the player picked them in, and the board renders the hand exactly as it arrives.
                .OrderBy(ph => ph.Id)
                .ToListAsync();
        }

        public async Task<CardPlacement> AddCardPlacementAsync(CardPlacement placement)
        {
            _context.CardPlacements.Add(placement);
            await _context.SaveChangesAsync();
            return placement;
        }

        public async Task<List<PlayerHand>> CreatePlayerHandsAsync(
            int matchId,
            string player1Id,
            string player2Id,
            List<Card> player1Cards,
            List<Card> player2Cards
        )
        {
            var hands = new List<PlayerHand>();

            // Create Player 1 hand
            foreach (var card in player1Cards)
            {
                hands.Add(
                    new PlayerHand
                    {
                        MatchId = matchId,
                        PlayerId = player1Id,
                        CardId = card.Id,
                        IsUsed = false,
                    }
                );
            }

            // Create Player 2 hand
            foreach (var card in player2Cards)
            {
                hands.Add(
                    new PlayerHand
                    {
                        MatchId = matchId,
                        PlayerId = player2Id,
                        CardId = card.Id,
                        IsUsed = false,
                    }
                );
            }

            _context.PlayerHands.AddRange(hands);
            await _context.SaveChangesAsync();

            return hands;
        }

        public async Task UpdateCardPlacementOwnershipAsync(List<CardPlacement> placements)
        {
            _context.CardPlacements.UpdateRange(placements);
            await _context.SaveChangesAsync();
        }

        public async Task<List<Card>> GetCardsByIdsAsync(IReadOnlyCollection<int> cardIds)
        {
            if (cardIds.Count == 0)
            {
                return [];
            }

            return await _context.Cards.Where(card => cardIds.Contains(card.Id)).ToListAsync();
        }

        public async Task<int> GetFiledHandCountAsync(int matchId, string playerId)
        {
            // Played rows count too: `IsUsed` says a card is on the board, not that the hand was never filed.
            return await _context.PlayerHands.CountAsync(hand =>
                hand.MatchId == matchId && hand.PlayerId == playerId
            );
        }

        public async Task<List<PlayerHand>> ReplacePlayerHandAsync(
            int matchId,
            string playerId,
            List<Card> cards
        )
        {
            var unused = await _context
                .PlayerHands.Where(hand =>
                    hand.MatchId == matchId && hand.PlayerId == playerId && !hand.IsUsed
                )
                .ToListAsync();

            _context.PlayerHands.RemoveRange(unused);

            var replacement = cards
                .Select(card => new PlayerHand
                {
                    MatchId = matchId,
                    PlayerId = playerId,
                    CardId = card.Id,
                    IsUsed = false,
                })
                .ToList();

            _context.PlayerHands.AddRange(replacement);
            await _context.SaveChangesAsync();

            return replacement;
        }

        public async Task<List<Match>> GetActiveMatchesAsync()
        {
            return await _context
                .Matches.Include(m => m.CardPlacements)
                .Include(m => m.PlayerHands)
                .Where(m => m.Status == "active")
                .ToListAsync();
        }

        public async Task<List<Match>> GetWaitingMatchesAsync()
        {
            return await _context
                .Matches.Where(m => m.Status == "waiting" && string.IsNullOrEmpty(m.Player2Id))
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();
        }

        public async Task<List<Match>> GetMatchesAwaitingTurnAsync(string playerId)
        {
            return await _context
                .Matches.Include(m => m.CardPlacements)
                .ThenInclude(placement => placement.Card)
                .Include(m => m.PlayerHands)
                .ThenInclude(hand => hand.Card)
                .Where(m =>
                    m.Status == "active"
                    && m.Player2Id == playerId
                    && m.CurrentPlayerTurn == playerId
                )
                .ToListAsync();
        }
    }
}

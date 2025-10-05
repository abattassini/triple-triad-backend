using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    public interface IGameRepository
    {
        Task<List<Card>> GetAllCardsAsync();
        Task<Card?> GetCardByIdAsync(int cardId);
        Task<Match?> GetMatchByIdAsync(int matchId);
        Task<Match?> GetActiveMatchForPlayerAsync(string playerId);
        Task<Match> CreateMatchAsync(string player1Id, string? player2Id);
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
    }

    public class GameRepository(TripleTriadContext context) : IGameRepository
    {
        private readonly TripleTriadContext _context = context;

        public async Task<List<Card>> GetAllCardsAsync()
        {
            return await _context.Cards.ToListAsync();
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

        public async Task<Match?> GetActiveMatchForPlayerAsync(string playerId)
        {
            return await _context
                .Matches.Include(m => m.CardPlacements)
                .ThenInclude(cp => cp.Card)
                .Include(m => m.PlayerHands)
                .ThenInclude(ph => ph.Card)
                .FirstOrDefaultAsync(m =>
                    (m.Player1Id == playerId || m.Player2Id == playerId)
                    && (m.Status == "waiting" || m.Status == "active")
                );
        }

        public async Task<Match> CreateMatchAsync(string player1Id, string? player2Id)
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

        public async Task<List<Match>> GetWaitingMatchesAsync()
        {
            return await _context
                .Matches.Where(m => m.Status == "waiting" && string.IsNullOrEmpty(m.Player2Id))
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();
        }
    }
}

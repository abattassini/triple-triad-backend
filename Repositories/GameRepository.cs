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

        /// <summary>
        /// Starts a match. <paramref name="startingPlayer"/> is the seat that opens it — drawn at random by the
        /// caller once both seats are known, rather than assumed to be player 1 (PLAN-024).
        /// </summary>
        Task<Match> CreateMatchAsync(
            string player1Id,
            string? player2Id,
            List<MatchRule> rules,
            string startingPlayer
        );
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

        /// <summary>
        /// Seats <paramref name="playerId"/> in a match that is still waiting, but only if it is genuinely still
        /// waiting when the write lands. Returns false when it is not — someone else took it first.
        ///
        /// This is the claim Quick Match makes, and it has to be a single guarded statement rather than a read
        /// followed by a save: the read says "available", and by the time a separate write runs it may not be. On
        /// success the tracked row (when there is one) is refreshed, because the guarded statement bypasses the change
        /// tracker — the same reason <c>TrySpendCoinsAsync</c> reads the balance back.
        ///
        /// <paramref name="startingPlayer"/> is written in the same statement as the seat: the opening turn is drawn
        /// when the second player lands, so the seat and the turn can never disagree (PLAN-024).
        /// </summary>
        Task<bool> TryClaimWaitingMatchAsync(
            int matchId,
            string playerId,
            DateTime now,
            string startingPlayer
        );

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
        Task<List<PlayerHand>> ReplacePlayerHandAsync(
            int matchId,
            string playerId,
            List<Card> cards
        );

        /// <summary>Every active match with its hands and placements — what the timeout sweep works from.</summary>
        Task<List<Match>> GetActiveMatchesAsync();

        /// <summary>
        /// The active matches in which <paramref name="playerId"/> is the opponent (player 2) and it is their turn —
        /// what the CPU's move engine works from, since the sentinel only ever plays that seat. The board and both
        /// hands come with their **cards**, because a move has to be evaluated against the real values (the timeout
        /// sweep's query loads neither: it only looks at counts and timestamps).
        /// </summary>
        Task<List<Match>> GetMatchesAwaitingTurnAsync(string playerId);

        /// <summary>
        /// Takes a turn in one guarded statement: <paramref name="actor"/>'s turn only flips to
        /// <paramref name="next"/> while the match is still <c>active</c> and genuinely still theirs, so of any number
        /// of writers of one turn exactly one is told true. Returns false for the ones that lost, and a caller that
        /// reads false must persist nothing.
        ///
        /// The point is that a turn has **one writer**, however many backends are pointed at the same database (a
        /// local one and the deployed one, say): both find the same match on the same player's turn — the due time is
        /// derived from the match id and the board, so they even agree on *when* — and without this claim both would
        /// play it. See <c>plans/PLAN-016-one-writer-per-turn/plan.md</c>.
        /// </summary>
        Task<bool> TryTakeTurnAsync(int matchId, string actor, string next);

        /// <summary>
        /// Writes a whole move in **one** save: the placement, the cards it captured, the hand row it spent and the
        /// match row (scores, turn, status). One save is one transaction on PostgreSQL, so a move lands completely or
        /// not at all — a board that shows a card whose hand row was never marked (or a turn that flipped without a
        /// card) is not a state this can leave behind.
        /// </summary>
        Task PersistMoveAsync(
            CardPlacement placement,
            List<CardPlacement> capturedCards,
            PlayerHand? usedCard,
            Match match
        );

        /// <summary>
        /// Stamps <c>ActivatedAt</c> on an <c>active</c> match that does not have it yet — the moment both hands are
        /// filed and the board opens, which is the reference the CPU's opening move is timed from (PLAN-024).
        /// Deliberately narrow: it only ever writes that one column, and only where the stamp is still missing, so the
        /// PvP paths (which stamp at join/claim) are untouched and a hand a caller just replaced is never disturbed.
        /// Returns true when it stamped.
        /// </summary>
        Task<bool> TryActivateAsync(int matchId, DateTime now);
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
            List<MatchRule> rules,
            string startingPlayer
        )
        {
            var match = new Match
            {
                Player1Id = player1Id,
                Player2Id = player2Id ?? string.Empty, // Empty string for waiting matches
                // The opener, drawn at random by the caller once both seats are known (PLAN-024). A waiting match has
                // only its creator to go on, so it carries them as a placeholder until the claim draws for real.
                CurrentPlayerTurn = startingPlayer,
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

        public async Task<bool> TryClaimWaitingMatchAsync(
            int matchId,
            string playerId,
            DateTime now,
            string startingPlayer
        )
        {
            if (_context.Database.IsInMemory())
            {
                // The in-memory provider has no bulk update, so the guarded read-modify-write below is the only option
                // there — the same accommodation PlayerRepository.TrySpendCoinsAsync makes for coins.
                var tracked = await _context.Matches.FirstOrDefaultAsync(m => m.Id == matchId);
                if (
                    tracked is null
                    || tracked.Status != "waiting"
                    || !string.IsNullOrEmpty(tracked.Player2Id)
                )
                {
                    return false;
                }

                tracked.Player2Id = playerId;
                tracked.Status = "active";
                tracked.ActivatedAt = now;
                tracked.CurrentPlayerTurn = startingPlayer;
                await _context.SaveChangesAsync();

                return true;
            }

            // One guarded statement: the row is only seated while it is genuinely still waiting with no second player,
            // so two players racing for the same match can never both end up in it.
            var claimed = await _context
                .Matches.Where(m =>
                    m.Id == matchId && m.Status == "waiting" && m.Player2Id == string.Empty
                )
                .ExecuteUpdateAsync(setters =>
                    setters
                        .SetProperty(m => m.Player2Id, playerId)
                        .SetProperty(m => m.Status, "active")
                        .SetProperty(m => m.ActivatedAt, now)
                        .SetProperty(m => m.CurrentPlayerTurn, startingPlayer)
                );

            if (claimed == 0)
            {
                return false;
            }

            // The bulk update bypassed the change tracker, so a copy of this row that the caller already loaded still
            // shows it as waiting. Refreshing it here is what lets the winner read the match it just claimed.
            var cached = _context
                .ChangeTracker.Entries<Match>()
                .FirstOrDefault(entry => entry.Entity.Id == matchId);

            if (cached is not null)
            {
                await cached.ReloadAsync();
            }

            return true;
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

        public async Task<bool> TryTakeTurnAsync(int matchId, string actor, string next)
        {
            if (_context.Database.IsInMemory())
            {
                // No bulk update on the in-memory provider (the same accommodation TryClaimWaitingMatchAsync makes), so
                // the guard is read first — and deliberately *not* through the change tracker: a context that loaded
                // this row earlier holds a copy the store has since moved on from, and that stale copy is exactly what
                // must not win here.
                var stored = await _context
                    .Matches.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == matchId);

                if (
                    stored is null
                    || stored.Status != "active"
                    || stored.CurrentPlayerTurn != actor
                )
                {
                    return false;
                }

                var tracked = await _context.Matches.FirstOrDefaultAsync(m => m.Id == matchId);
                if (tracked is null)
                {
                    return false;
                }

                // Only this column changed, so only this column is written: the rest of the row may be older in this
                // context than it is in the store, and saving it back would undo whatever it missed.
                tracked.CurrentPlayerTurn = next;
                await _context.SaveChangesAsync();

                return true;
            }

            // One guarded statement, so two writers of the same turn cannot both be told true. The change tracker is
            // left alone on purpose: the caller writes the turn itself when it saves the move (with the same value)
            // and every other column it saves is one it set from the move it just resolved.
            var claimed = await _context
                .Matches.Where(m =>
                    m.Id == matchId && m.Status == "active" && m.CurrentPlayerTurn == actor
                )
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.CurrentPlayerTurn, next));

            return claimed > 0;
        }

        public async Task PersistMoveAsync(
            CardPlacement placement,
            List<CardPlacement> capturedCards,
            PlayerHand? usedCard,
            Match match
        )
        {
            _context.CardPlacements.Add(placement);

            if (capturedCards.Count != 0)
            {
                _context.CardPlacements.UpdateRange(capturedCards);
            }

            if (usedCard is not null)
            {
                _context.PlayerHands.Update(usedCard);
            }

            _context.Matches.Update(match);

            // Everything the move changed is saved once, which is one transaction on PostgreSQL and one write on the
            // in-memory store. Nothing here is a partial update, so a board can never disagree with the hand row and
            // the turn that describe it — see plans/PLAN-016-one-writer-per-turn/plan.md.
            await _context.SaveChangesAsync();
        }

        public async Task<bool> TryActivateAsync(int matchId, DateTime now)
        {
            if (_context.Database.IsInMemory())
            {
                // The in-memory provider has no bulk update (the same accommodation TryClaimWaitingMatchAsync makes),
                // so the guarded read-modify-write is the only option there.
                var tracked = await _context.Matches.FirstOrDefaultAsync(m => m.Id == matchId);
                if (
                    tracked is null
                    || tracked.Status != "active"
                    || tracked.ActivatedAt is not null
                )
                {
                    return false;
                }

                tracked.ActivatedAt = now;
                await _context.SaveChangesAsync();

                return true;
            }

            // One guarded statement: the stamp only lands on a match that is active and still unstamped, so a PvP
            // match (which stamped at join/claim) is left exactly as it was.
            var activated = await _context
                .Matches.Where(m =>
                    m.Id == matchId && m.Status == "active" && m.ActivatedAt == null
                )
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.ActivatedAt, now));

            return activated > 0;
        }
    }
}

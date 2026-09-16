using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The card shop: buys a card pack and files the cards in the player's collection.
    ///
    /// A pack is drawn level-first: the level is picked with weight <c>600 - 20x</c> over the levels 1..10, so a
    /// level's chance is exactly <c>(600 - 20x) / 49</c> percent, and then a card of that level is picked
    /// uniformly at random. The five draws are independent, so a pack may hold the same card twice and may hand
    /// out a card the player already owns — that copy only raises its quantity.
    /// </summary>
    public class PackService
    {
        // Tunables, in one place (the same style as MatchRewardService's reward table).
        public const int PackPrice = 1500;
        public const int CardsPerPack = 5;
        public const int MinCardLevel = 1;
        public const int MaxCardLevel = 10;

        private readonly IGameRepository _gameRepository;
        private readonly IPlayerRepository _playerRepository;
        private readonly IPlayerCardRepository _playerCardRepository;
        private readonly IRandomSource _random;

        public PackService(
            IGameRepository gameRepository,
            IPlayerRepository playerRepository,
            IPlayerCardRepository playerCardRepository,
            IRandomSource random
        )
        {
            _gameRepository = gameRepository;
            _playerRepository = playerRepository;
            _playerCardRepository = playerCardRepository;
            _random = random;
        }

        /// <summary>One row of the shop's odds table.</summary>
        public sealed record LevelOdds(int Level, int Weight, double ChancePercent);

        /// <summary>What a pack costs and what it can hold — what the shop page shows before buying.</summary>
        public sealed record PackOffer(
            int Price,
            int CardCount,
            IReadOnlyList<LevelOdds> LevelOdds
        );

        /// <summary>A card the pack handed out, with the collection state that follows from it.</summary>
        public sealed record PackCard(Card Card, int QuantityOwned, bool IsNew);

        public sealed record PurchaseResult
        {
            public bool Succeeded { get; init; }
            public string? ErrorMessage { get; init; }
            public int CoinsAfter { get; init; }
            public IReadOnlyList<PackCard> Cards { get; init; } = [];

            public static PurchaseResult Failure(string message) =>
                new() { Succeeded = false, ErrorMessage = message };
        }

        /// <summary>The weight of one card level in the draw — the <c>600 - 20x</c> of the odds formula.</summary>
        public static int LevelWeight(int level) => 600 - 20 * level;

        /// <summary>Total weight of every level, i.e. the <c>49</c> in <c>(600 - 20x) / 49</c> (4900).</summary>
        public static int TotalLevelWeight =>
            Enumerable.Range(MinCardLevel, MaxCardLevel - MinCardLevel + 1).Sum(LevelWeight);

        /// <summary>The pack on offer: price, size and the level odds table.</summary>
        public PackOffer GetOffer()
        {
            var totalWeight = TotalLevelWeight;
            var odds = new List<LevelOdds>(MaxCardLevel - MinCardLevel + 1);

            for (var level = MinCardLevel; level <= MaxCardLevel; level++)
            {
                var weight = LevelWeight(level);
                odds.Add(new LevelOdds(level, weight, Math.Round(weight * 100.0 / totalWeight, 2)));
            }

            return new PackOffer(PackPrice, CardsPerPack, odds);
        }

        /// <summary>
        /// Buys one pack for the player. The price is charged first, so a wallet that cannot afford it is
        /// rejected before anything is granted, and only then are the five cards drawn and filed.
        /// </summary>
        public async Task<PurchaseResult> PurchaseAsync(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                return PurchaseResult.Failure("User not authenticated");
            }

            var player = await _playerRepository.FindByLoginAsync(playerId);
            if (player is null)
            {
                return PurchaseResult.Failure("Player not found");
            }

            var cardsByLevel = BuildLevelIndex(await _gameRepository.GetAllCardsAsync());
            if (cardsByLevel.Count == 0)
            {
                return PurchaseResult.Failure("No cards are available right now");
            }

            var coinsAfter = await _playerRepository.TrySpendCoinsAsync(playerId, PackPrice);
            if (coinsAfter is null)
            {
                return PurchaseResult.Failure($"Not enough coins. A pack costs {PackPrice} coins.");
            }

            // What the player owned before this pack. Both the "is new" set and the quantities are snapshotted
            // here, because filing the cards mutates the very rows the repository just returned (same context).
            var owned = await _playerCardRepository.GetForPlayerAsync(playerId);
            var previouslyOwned = owned.Select(playerCard => playerCard.CardId).ToHashSet();
            var quantities = owned.ToDictionary(playerCard => playerCard.CardId, pc => pc.Quantity);

            var drawn = DrawPack(cardsByLevel);
            await _playerCardRepository.AddOrIncrementManyAsync(
                playerId,
                [.. drawn.Select(card => card.Id)]
            );

            // Quantities are counted forwards, so a card drawn twice reports its running total.
            var cards = new List<PackCard>(drawn.Count);
            foreach (var card in drawn)
            {
                var quantityOwned = quantities.GetValueOrDefault(card.Id) + 1;
                quantities[card.Id] = quantityOwned;
                cards.Add(new PackCard(card, quantityOwned, !previouslyOwned.Contains(card.Id)));
            }

            return new PurchaseResult
            {
                Succeeded = true,
                CoinsAfter = coinsAfter.Value,
                Cards = cards,
            };
        }

        /// <summary>Draws one pack: <see cref="CardsPerPack"/> independent level-then-card draws.</summary>
        private List<Card> DrawPack(IReadOnlyDictionary<int, List<Card>> cardsByLevel)
        {
            var pack = new List<Card>(CardsPerPack);

            for (var draw = 0; draw < CardsPerPack; draw++)
            {
                var levelCards = cardsByLevel[DrawLevel(cardsByLevel)];
                pack.Add(levelCards[_random.Next(levelCards.Count)]);
            }

            return pack;
        }

        /// <summary>
        /// Picks a level by walking the cumulative weights (<c>600 - 20x</c>). The weights are summed over the
        /// levels the catalogue actually has, so an empty level can never be drawn and the remaining levels keep
        /// their ratio — with every level populated the total is the 4900 of the formula.
        /// </summary>
        private int DrawLevel(IReadOnlyDictionary<int, List<Card>> cardsByLevel)
        {
            var totalWeight = 0;
            for (var level = MinCardLevel; level <= MaxCardLevel; level++)
            {
                if (cardsByLevel.ContainsKey(level))
                {
                    totalWeight += LevelWeight(level);
                }
            }

            var pick = _random.Next(totalWeight);
            var cumulative = 0;

            for (var level = MinCardLevel; level <= MaxCardLevel; level++)
            {
                if (!cardsByLevel.ContainsKey(level))
                {
                    continue;
                }

                cumulative += LevelWeight(level);
                if (pick < cumulative)
                {
                    return level;
                }
            }

            // Unreachable: pick always falls inside the summed range above.
            throw new InvalidOperationException("The card catalogue has no drawable level.");
        }

        /// <summary>
        /// Indexes the catalogue by level, keeping card ids ascending so a scripted rng yields a reproducible
        /// card for a given index. Cards outside the level range are ignored.
        /// </summary>
        private static Dictionary<int, List<Card>> BuildLevelIndex(List<Card> catalogue)
        {
            var byLevel = new Dictionary<int, List<Card>>();

            foreach (var card in catalogue.OrderBy(card => card.Id))
            {
                if (card.Level < MinCardLevel || card.Level > MaxCardLevel)
                {
                    continue;
                }

                if (!byLevel.TryGetValue(card.Level, out var levelCards))
                {
                    levelCards = [];
                    byLevel[card.Level] = levelCards;
                }

                levelCards.Add(card);
            }

            return byLevel;
        }
    }
}

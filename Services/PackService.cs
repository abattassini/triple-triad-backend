using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The card shop, in two steps on purpose: <see cref="PurchaseAsync"/> charges the coins and puts a pack in the
    /// player's inventory, and <see cref="OpenAsync"/> consumes one of those packs and files the cards it held in
    /// the collection.
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

        // What a brand-new account is given, so a collection can be started without playing a match first.
        // Registration is the only grant that skips the coin charge, and because the packs are filled later (when
        // they are opened) it needs no catalogue check either.
        public const int StartingPacks = 6;

        // The one pack the shop sells today. The inventory is keyed by this code (see Models/PlayerPack.cs), so a
        // second pack type is a definition here plus its own row — not a schema or client-contract change.
        public const string StandardPackCode = "standard";
        public const string StandardPackName = "Standard Pack";

        // The level range the catalogue uses (see Card.MinLevel/MaxLevel), shared with the collection filter.
        public const int MinCardLevel = Card.MinLevel;
        public const int MaxCardLevel = Card.MaxLevel;

        private readonly IGameRepository _gameRepository;
        private readonly IPlayerRepository _playerRepository;
        private readonly IPlayerCardRepository _playerCardRepository;
        private readonly IPlayerPackRepository _playerPackRepository;
        private readonly IRandomSource _random;

        public PackService(
            IGameRepository gameRepository,
            IPlayerRepository playerRepository,
            IPlayerCardRepository playerCardRepository,
            IPlayerPackRepository playerPackRepository,
            IRandomSource random
        )
        {
            _gameRepository = gameRepository;
            _playerRepository = playerRepository;
            _playerCardRepository = playerCardRepository;
            _playerPackRepository = playerPackRepository;
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

        /// <summary>One stack of unopened packs, as the inventory endpoint reports it.</summary>
        public sealed record PackInventoryEntry(
            string Code,
            string Name,
            int CardCount,
            int Quantity
        );

        /// <summary>A card the pack handed out, with the collection state that follows from it.</summary>
        public sealed record PackCard(Card Card, int QuantityOwned, bool IsNew);

        /// <summary>What a purchase did: the new wallet balance and how many packs the player now holds.</summary>
        public sealed record PurchaseResult
        {
            public bool Succeeded { get; init; }
            public string? ErrorMessage { get; init; }
            public int CoinsAfter { get; init; }
            public int PacksOwned { get; init; }

            public static PurchaseResult Failure(string message) =>
                new() { Succeeded = false, ErrorMessage = message };
        }

        /// <summary>What opening a pack did: the cards it held and how many packs are left.</summary>
        public sealed record OpenResult
        {
            public bool Succeeded { get; init; }
            public string? ErrorMessage { get; init; }
            public string PackCode { get; init; } = StandardPackCode;
            public int PacksOwned { get; init; }
            public IReadOnlyList<PackCard> Cards { get; init; } = [];

            public static OpenResult Failure(string message) =>
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

        /// <summary>The player's unopened packs — one entry per stack they hold, for the My Packs page.</summary>
        public async Task<IReadOnlyList<PackInventoryEntry>> GetInventoryAsync(string playerId)
        {
            var stacks = await _playerPackRepository.GetForPlayerAsync(playerId);

            return
            [
                .. stacks.Select(stack => new PackInventoryEntry(
                    stack.PackCode,
                    NameFor(stack.PackCode),
                    CardsPerPack,
                    stack.Quantity
                )),
            ];
        }

        /// <summary>How many unopened standard packs the player holds — the count the profile reports.</summary>
        public Task<int> GetPackCountAsync(string playerId) =>
            _playerPackRepository.GetCountAsync(playerId, StandardPackCode);

        /// <summary>Display name for a pack code; an unknown code falls back to the code itself.</summary>
        public static string NameFor(string packCode) =>
            packCode == StandardPackCode ? StandardPackName : packCode;

        /// <summary>
        /// Buys one pack for the player. The price is charged first, so a wallet that cannot afford it is rejected
        /// before anything is granted, and the pack is then added to the inventory. **No cards are drawn here** —
        /// that happens when the player opens the pack (see <see cref="OpenAsync"/>), which is what lets the card
        /// shop hand out packs instead of cards.
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

            // The catalogue is checked before the wallet is touched, so a shop with nothing to draw from can never
            // charge for a pack it cannot fill later.
            if (BuildLevelIndex(await _gameRepository.GetAllCardsAsync()).Count == 0)
            {
                return PurchaseResult.Failure("No cards are available right now");
            }

            var coinsAfter = await _playerRepository.TrySpendCoinsAsync(playerId, PackPrice);
            if (coinsAfter is null)
            {
                return PurchaseResult.Failure($"Not enough coins. A pack costs {PackPrice} coins.");
            }

            var stack = await _playerPackRepository.GrantAsync(playerId, StandardPackCode);

            return new PurchaseResult
            {
                Succeeded = true,
                CoinsAfter = coinsAfter.Value,
                PacksOwned = stack.Quantity,
            };
        }

        /// <summary>
        /// Opens one of the player's packs: the pack is consumed first (a guarded decrement, so an empty inventory
        /// is rejected before anything is drawn) and only then are the five cards drawn and filed in the
        /// collection. The player receives the cards here, which is why the frontend's reveal is purely cosmetic.
        /// </summary>
        public async Task<OpenResult> OpenAsync(string playerId, string packCode = StandardPackCode)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                return OpenResult.Failure("User not authenticated");
            }

            var cardsByLevel = BuildLevelIndex(await _gameRepository.GetAllCardsAsync());
            if (cardsByLevel.Count == 0)
            {
                return OpenResult.Failure("No cards are available right now");
            }

            // Consume first: the draw below must never be able to eat a pack, and the guarded decrement is what
            // stops two racing requests from opening the same pack twice.
            var packsLeft = await _playerPackRepository.TryConsumeAsync(playerId, packCode);
            if (packsLeft is null)
            {
                return OpenResult.Failure("You don't have any packs to open.");
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

            return new OpenResult
            {
                Succeeded = true,
                PackCode = packCode,
                PacksOwned = packsLeft.Value,
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

namespace TripleTriadApi.Models
{
    public class Card
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
        public int TopValue { get; set; }
        public int RightValue { get; set; }
        public int BottomValue { get; set; }
        public int LeftValue { get; set; }

        // A card can carry zero or more elements (empty list = no element).
        // Persisted as a PostgreSQL text[] array.
        public List<string> Element { get; set; } = [];
        public int Level { get; set; }

        /// <summary>Lowest card level the catalogue uses (levels run <see cref="MinLevel"/>..<see cref="MaxLevel"/>).</summary>
        public const int MinLevel = 1;

        /// <summary>Highest card level the catalogue uses — the shop's draw weights and the collection filter share it.</summary>
        public const int MaxLevel = 10;

        // Navigation properties for tracking card ownership in matches
        public virtual ICollection<CardPlacement> CardPlacements { get; set; } =
            new List<CardPlacement>();
        public virtual ICollection<PlayerHand> PlayerHands { get; set; } = new List<PlayerHand>();

        // Who owns copies of this card outside of a match (the card shop's collection).
        public virtual ICollection<PlayerCard> PlayerCards { get; set; } = new List<PlayerCard>();
    }
}

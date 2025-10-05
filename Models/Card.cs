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
        public string Element { get; set; } = "none";
        public int Level { get; set; }

        // Navigation properties for tracking card ownership in matches
        public virtual ICollection<CardPlacement> CardPlacements { get; set; } =
            new List<CardPlacement>();
        public virtual ICollection<PlayerHand> PlayerHands { get; set; } = new List<PlayerHand>();
    }
}

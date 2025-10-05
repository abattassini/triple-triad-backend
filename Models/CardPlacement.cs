namespace TripleTriadApi.Models
{
    public class CardPlacement
    {
        public int Id { get; set; }
        public int MatchId { get; set; }
        public int CardId { get; set; }
        public string PlayerId { get; set; } = string.Empty;
        public int X { get; set; } // 0, 1, 2 for board position
        public int Y { get; set; } // 0, 1, 2 for board position
        public DateTime PlacedAt { get; set; } = DateTime.UtcNow;
        public string Owner { get; set; } = string.Empty; // Current owner after captures

        // Navigation properties
        public virtual Match Match { get; set; } = null!;
        public virtual Card Card { get; set; } = null!;
    }
}

namespace TripleTriadApi.Models
{
    public class PlayerHand
    {
        public int Id { get; set; }
        public int MatchId { get; set; }
        public string PlayerId { get; set; } = string.Empty;
        public int CardId { get; set; }
        public bool IsUsed { get; set; } = false;

        // Navigation properties
        public virtual Match Match { get; set; } = null!;
        public virtual Card Card { get; set; } = null!;
    }
}

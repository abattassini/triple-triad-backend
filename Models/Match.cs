namespace TripleTriadApi.Models
{
    public class Match
    {
        public int Id { get; set; }
        public string Player1Id { get; set; } = string.Empty;
        public string Player2Id { get; set; } = string.Empty;
        public string? CurrentPlayerTurn { get; set; }
        public string Status { get; set; } = "waiting"; // waiting, active, completed, abandoned
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public string? WinnerId { get; set; }
        public int Player1Score { get; set; }
        public int Player2Score { get; set; }

        // Optional rules enabled for this match (bitmask). Defaults to none so every match
        // created before rules existed keeps the original behaviour.
        public MatchRule Rules { get; set; } = MatchRule.None;

        // Navigation properties
        public virtual ICollection<CardPlacement> CardPlacements { get; set; } =
            new List<CardPlacement>();
        public virtual ICollection<PlayerHand> PlayerHands { get; set; } = new List<PlayerHand>();
    }
}

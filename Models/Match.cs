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

        // When the second player joined and the match became active. The timeout rules measure "this player never
        // picked their hand" and "nobody has moved" from here (see Services/MatchTimeouts.cs).
        public DateTime? ActivatedAt { get; set; }
        public string? WinnerId { get; set; }
        public int Player1Score { get; set; }
        public int Player2Score { get; set; }

        // Rules enabled for this match. A rule is active when it appears in this list; an empty
        // list means the match is played with the basic rules only.
        public List<MatchRule> Rules { get; set; } = [];

        // Navigation properties
        public virtual ICollection<CardPlacement> CardPlacements { get; set; } =
            new List<CardPlacement>();
        public virtual ICollection<PlayerHand> PlayerHands { get; set; } = new List<PlayerHand>();
    }
}

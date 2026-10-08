namespace TripleTriadApi.Models
{
    public class Match
    {
        public int Id { get; set; }
        public string Player1Id { get; set; } = string.Empty;
        public string Player2Id { get; set; } = string.Empty;
        public string? CurrentPlayerTurn { get; set; }

        // The match's lifecycle: `waiting` (created, with no second seat yet), `pending` (a challenge awaiting the
        // challenged player's answer — plans/PLAN-027-friend-challenge/plan.md), `active` (both seated, being played),
        // `refused` (the challenged declined a challenge), `abandoned` (given up: a timeout, a cancelled search, or a
        // challenge that was cancelled, superseded or expired) and `completed` (played out or forfeited).
        public string Status { get; set; } = "waiting";
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

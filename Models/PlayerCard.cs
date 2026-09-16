namespace TripleTriadApi.Models
{
    /// <summary>
    /// One card a player owns, with how many copies of it. The collection is stored one row per card — the row
    /// is created when the card is first acquired and its <see cref="Quantity"/> is incremented for every later
    /// copy — so a player's holdings are a quantity list rather than a duplicate-heavy row per copy.
    ///
    /// <see cref="PlayerId"/> is the player's login, the same identifier the game layer uses
    /// (<see cref="PlayerHand.PlayerId"/>, <see cref="CardPlacement.PlayerId"/>) and the JWT subject.
    /// </summary>
    public class PlayerCard
    {
        public int Id { get; set; }
        public string PlayerId { get; set; } = string.Empty;
        public int CardId { get; set; }

        /// <summary>Copies owned. Always at least 1 for an existing row.</summary>
        public int Quantity { get; set; } = 1;

        /// <summary>When the player acquired their first copy of this card.</summary>
        public DateTime FirstAcquiredAt { get; set; } = DateTime.UtcNow;

        /// <summary>When the player last acquired a copy (i.e. the most recent increment).</summary>
        public DateTime LastAcquiredAt { get; set; } = DateTime.UtcNow;

        public virtual Card Card { get; set; } = null!;
    }
}

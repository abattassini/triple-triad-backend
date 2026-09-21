namespace TripleTriadApi.Models
{
    /// <summary>
    /// One stack of unopened packs a player owns. Buying a pack on the card shop adds one to the standard stack;
    /// opening a pack consumes it and files the drawn cards into the collection (<see cref="PlayerCard"/>).
    ///
    /// The shape mirrors <see cref="PlayerCard"/> on purpose — one row per (player, pack code) carrying a
    /// <see cref="Quantity"/> — so a second kind of pack is a data change (<see cref="PackCode"/>) rather than a
    /// schema change. <see cref="PlayerId"/> is the player's login, the identifier the game layer, the JWT
    /// subject and the shop all use, and there is deliberately no FK to <see cref="Player"/>, exactly like
    /// <see cref="PlayerCard"/>, <see cref="PlayerHand"/> and <see cref="CardPlacement"/>.
    /// </summary>
    public class PlayerPack
    {
        public int Id { get; set; }
        public string PlayerId { get; set; } = string.Empty;

        /// <summary>Which pack this stack holds (see <c>PackService.StandardPackCode</c>).</summary>
        public string PackCode { get; set; } = string.Empty;

        /// <summary>
        /// Packs left in the stack. A row starts at 1, every purchase raises it, and it is removed once its last
        /// pack has been opened — so a stored row always holds at least one unopened pack.
        /// </summary>
        public int Quantity { get; set; } = 1;

        /// <summary>When the player bought their first pack of this code.</summary>
        public DateTime FirstAcquiredAt { get; set; } = DateTime.UtcNow;

        /// <summary>When the stack last grew (i.e. the most recent purchase).</summary>
        public DateTime LastAcquiredAt { get; set; } = DateTime.UtcNow;
    }
}

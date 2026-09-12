namespace TripleTriadApi.Models
{
    public class Player
    {
        public int Id { get; set; }
        public string Login { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Progression & economy (see plans/player-stats-economy-plan.md)
        public int Coins { get; set; }
        public int Experience { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Ties { get; set; }

        // Null until an avatar is chosen; the frontend shows a placeholder.
        public string? AvatarUrl { get; set; }
    }
}
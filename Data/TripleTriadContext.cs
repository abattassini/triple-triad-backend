using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Models;

namespace TripleTriadApi.Data
{
    public class TripleTriadContext : DbContext
    {
        public TripleTriadContext(DbContextOptions<TripleTriadContext> options)
            : base(options) { }

        public DbSet<Card> Cards { get; set; }
        public DbSet<Match> Matches { get; set; }
        public DbSet<CardPlacement> CardPlacements { get; set; }
        public DbSet<PlayerHand> PlayerHands { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Only configure if not already configured (for design-time support)
            if (!optionsBuilder.IsConfigured)
            {
                // Use connection string from environment variable
                var connectionString = Environment.GetEnvironmentVariable(
                    "ConnectionStrings__DefaultConnection"
                );
                if (!string.IsNullOrEmpty(connectionString))
                {
                    optionsBuilder.UseNpgsql(connectionString);
                }
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Card entity configuration
            modelBuilder.Entity<Card>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Image).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Element).HasMaxLength(20);

                // Ensure stat values are within valid range (1-10 or A)
                entity.Property(e => e.TopValue).HasAnnotation("Range", new[] { 1, 10 });
                entity.Property(e => e.RightValue).HasAnnotation("Range", new[] { 1, 10 });
                entity.Property(e => e.BottomValue).HasAnnotation("Range", new[] { 1, 10 });
                entity.Property(e => e.LeftValue).HasAnnotation("Range", new[] { 1, 10 });
            });

            // Match entity configuration
            modelBuilder.Entity<Match>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Player1Id).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Player2Id).IsRequired().HasMaxLength(100);
                entity.Property(e => e.CurrentPlayerTurn).HasMaxLength(100);
                entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
                entity.Property(e => e.WinnerId).HasMaxLength(100);

                entity.HasIndex(e => new { e.Player1Id, e.Player2Id });
                entity.HasIndex(e => e.Status);
            });

            // CardPlacement entity configuration
            modelBuilder.Entity<CardPlacement>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.PlayerId).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Owner).IsRequired().HasMaxLength(100);

                // Ensure X and Y are within board bounds (0-2)
                entity.Property(e => e.X).HasAnnotation("Range", new[] { 0, 2 });
                entity.Property(e => e.Y).HasAnnotation("Range", new[] { 0, 2 });

                // Unique constraint for board position per match
                entity
                    .HasIndex(e => new
                    {
                        e.MatchId,
                        e.X,
                        e.Y,
                    })
                    .IsUnique();

                // Foreign key relationships
                entity
                    .HasOne(d => d.Match)
                    .WithMany(p => p.CardPlacements)
                    .HasForeignKey(d => d.MatchId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity
                    .HasOne(d => d.Card)
                    .WithMany(p => p.CardPlacements)
                    .HasForeignKey(d => d.CardId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // PlayerHand entity configuration
            modelBuilder.Entity<PlayerHand>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.PlayerId).IsRequired().HasMaxLength(100);

                // Foreign key relationships
                entity
                    .HasOne(d => d.Match)
                    .WithMany(p => p.PlayerHands)
                    .HasForeignKey(d => d.MatchId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity
                    .HasOne(d => d.Card)
                    .WithMany(p => p.PlayerHands)
                    .HasForeignKey(d => d.CardId)
                    .OnDelete(DeleteBehavior.Restrict);

                // Unique constraint to prevent duplicate cards in same hand
                entity
                    .HasIndex(e => new
                    {
                        e.MatchId,
                        e.PlayerId,
                        e.CardId,
                    })
                    .IsUnique();
            });
        }
    }
}

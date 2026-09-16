using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        public DbSet<Player> Players { get; set; }
        public DbSet<PlayerCard> PlayerCards { get; set; }

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

            // Player entity configuration
            modelBuilder.Entity<Player>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Login).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Email).IsRequired().HasMaxLength(254);
                entity.Property(e => e.PasswordHash).IsRequired().HasMaxLength(60);
                entity.Property(e => e.AvatarUrl).HasMaxLength(300);

                // Progression & economy defaults keep existing rows and new inserts valid.
                entity.Property(e => e.Coins).HasDefaultValue(0);
                entity.Property(e => e.Experience).HasDefaultValue(0);
                entity.Property(e => e.Wins).HasDefaultValue(0);
                entity.Property(e => e.Losses).HasDefaultValue(0);
                entity.Property(e => e.Ties).HasDefaultValue(0);

                // Unique constraints for authentication
                entity.HasIndex(e => e.Login).IsUnique();
                entity.HasIndex(e => e.Email).IsUnique();
            });

            // Card entity configuration
            modelBuilder.Entity<Card>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Image).IsRequired().HasMaxLength(200);
                // Stored as a PostgreSQL text[] array (Npgsql maps List<string> natively).
                entity.Property(e => e.Element).HasColumnType("text[]");

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

                // A rule is enabled when it is present in the match's rules list. The list is
                // stored as a comma separated column of rule names ("" = no rules), which keeps
                // the schema stable no matter how many rules are added later.
                entity
                    .Property(e => e.Rules)
                    .HasConversion(
                        rules => rules.ToStorageString(),
                        value => MatchRuleExtensions.ParseStorageString(value),
                        new ValueComparer<List<MatchRule>>(
                            (left, right) => left!.SequenceEqual(right!),
                            rules =>
                                rules.Aggregate(0, (hash, rule) => HashCode.Combine(hash, rule)),
                            rules => rules.ToList()
                        )
                    )
                    .HasColumnType("text")
                    .HasDefaultValue(new List<MatchRule>());

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

            // PlayerCard entity configuration (cards owned outside of a match)
            modelBuilder.Entity<PlayerCard>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.PlayerId).IsRequired().HasMaxLength(100);

                // One row per owned card, so further copies only raise the quantity.
                entity.Property(e => e.Quantity).HasDefaultValue(1);

                entity.HasIndex(e => new { e.PlayerId, e.CardId }).IsUnique();

                // The catalogue is reconciled on startup and never deleted, so an owned card can never
                // disappear from under a collection row.
                entity
                    .HasOne(d => d.Card)
                    .WithMany(c => c.PlayerCards)
                    .HasForeignKey(d => d.CardId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}

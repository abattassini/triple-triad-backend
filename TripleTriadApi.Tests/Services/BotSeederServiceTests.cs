using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The bot cohort the startup plants (plans/PLAN-025-bots/plan.md §3.7): twenty emulated players, each a real
    /// <see cref="Player"/> row with a name, a record and an activity value — and a second boot that adds nothing,
    /// because a bot's record is state a match actually plays with.
    /// </summary>
    public class BotSeederServiceTests
    {
        [Fact]
        public async Task Seed_PlantsTheCohort_WithAUniqueNameAndHashEach()
        {
            using var context = CreateContext();

            await BotSeederService.SeedBotsAsync(context, new SystemRandomSource());

            var bots = await context.Players.Where(player => player.IsBot).ToListAsync();

            Assert.Equal(BotSeederService.BotCount, bots.Count);
            Assert.All(bots, bot => Assert.False(string.IsNullOrWhiteSpace(bot.Login)));
            Assert.All(bots, bot => Assert.False(string.IsNullOrWhiteSpace(bot.PasswordHash)));
            Assert.All(bots, bot => Assert.False(string.IsNullOrWhiteSpace(bot.Email)));

            // A login and an email are unique columns, so the cohort must not collide with itself either.
            Assert.Equal(
                bots.Count,
                bots.Select(bot => bot.Login).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            );
            Assert.Equal(bots.Count, bots.Select(bot => bot.Email).Distinct().Count());
        }

        [Fact]
        public async Task Seed_KeepsActivityAndRecordInRange()
        {
            using var context = CreateContext();

            await BotSeederService.SeedBotsAsync(context, new SystemRandomSource());

            var bots = await context.Players.Where(player => player.IsBot).ToListAsync();

            // The activity ceiling is the whole point of §3.2's "no bot online almost all day".
            Assert.All(
                bots,
                bot => Assert.InRange(bot.Activity, 0, BotSeederService.MaxSeededActivity)
            );
            Assert.All(bots, bot => Assert.InRange(bot.Wins, 0, BotSeederService.MaxSeededOutcome));
            Assert.All(
                bots,
                bot => Assert.InRange(bot.Losses, 0, BotSeederService.MaxSeededOutcome)
            );
            Assert.All(bots, bot => Assert.InRange(bot.Ties, 0, BotSeederService.MaxSeededOutcome));
        }

        [Fact]
        public async Task Seed_IsIdempotent_AndDoesNotResetAChangedRecord()
        {
            using var context = CreateContext();
            await BotSeederService.SeedBotsAsync(context, new SystemRandomSource());

            var original = await context
                .Players.Where(p => p.IsBot)
                .Select(p => p.Login)
                .ToListAsync();

            // Stand in for a match having moved one bot's record on.
            var bot = await context.Players.FirstAsync(p => p.IsBot);
            bot.Wins += 5;
            await context.SaveChangesAsync();

            await BotSeederService.SeedBotsAsync(context, new SystemRandomSource());

            var bots = await context.Players.Where(player => player.IsBot).ToListAsync();
            Assert.Equal(BotSeederService.BotCount, bots.Count);
            Assert.Equal(
                original.OrderBy(login => login),
                bots.Select(b => b.Login).OrderBy(login => login)
            );
            Assert.Equal(bot.Wins, (await context.Players.FirstAsync(p => p.Id == bot.Id)).Wins);
        }

        [Fact]
        public async Task Seed_StepsAroundALoginAHumanAlreadyHolds()
        {
            using var context = CreateContext();
            context.Players.Add(
                new Player
                {
                    Login = "existing",
                    Email = "existing@example.com",
                    PasswordHash = "x",
                }
            );
            await context.SaveChangesAsync();

            await BotSeederService.SeedBotsAsync(context, new SystemRandomSource());

            var logins = await context.Players.Select(player => player.Login).ToListAsync();
            Assert.Equal(logins.Count, logins.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );
    }
}

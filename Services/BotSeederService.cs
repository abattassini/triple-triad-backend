using Bogus;
using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Seeds the bots — the emulated players the backend can always match somebody against
    /// (plans/PLAN-025-bots/plan.md §3.7). They are ordinary <see cref="Player"/> rows flagged
    /// <see cref="Player.IsBot"/>, each with a Bogus name, a random record and an activity value, so every screen that
    /// draws a player draws a bot too and no special "bot" identity is needed.
    ///
    /// Runs at startup beside <see cref="CardSeederService"/> and is idempotent in the coarser way a cohort allows: it
    /// plants the whole set once and then does nothing whenever any bot exists. That is deliberate and unlike the
    /// cards — a card's values are corrected on every boot, but a bot's record and activity are *state* that matches
    /// actually change, and must never be reset.
    /// </summary>
    public static class BotSeederService
    {
        /// <summary>How many bots the cohort holds.</summary>
        public const int BotCount = 20;

        /// <summary>
        /// The ceiling on a seeded bot's activity for now (plans/PLAN-025-bots/plan.md): low enough that no bot idles
        /// online all day, while the presence floor — three online at all times — is what keeps a match available
        /// regardless. The column still allows 0–100, so a later plan can raise this with no migration.
        /// </summary>
        public const int MaxSeededActivity = 40;

        /// <summary>Upper bound for each of a fresh bot's wins/losses/ties, so its profile reads plausibly at once.</summary>
        public const int MaxSeededOutcome = 80;

        /// <summary>
        /// BCrypt cost for a bot's password hash. Deliberately far below the production factor: the hashed secret is a
        /// fresh random GUID that nobody — not even the app — ever uses, and a bot can never authenticate, so the only
        /// thing the cost buys here is the two seconds a whole cohort of twenty would otherwise add to every boot.
        /// </summary>
        private const int BotHashWorkFactor = 4;

        /// <summary>
        /// Plants the cohort if the database holds no bots yet. <paramref name="random"/> is the shared seam, so a test
        /// can drive the record and activity values (the Bogus names have their own randomness and are not scripted).
        /// </summary>
        public static async Task SeedBotsAsync(TripleTriadContext context, IRandomSource random)
        {
            if (await context.Players.AnyAsync(player => player.IsBot))
            {
                return;
            }

            var faker = new Faker();
            var taken = new HashSet<string>(
                await context.Players.Select(player => player.Login).ToListAsync(),
                StringComparer.OrdinalIgnoreCase
            );

            var bots = new List<Player>(BotCount);
            for (var i = 0; i < BotCount; i++)
            {
                var login = UniqueLogin(faker, taken);
                taken.Add(login);

                bots.Add(
                    new Player
                    {
                        Login = login,
                        // A bot never signs in, so the address is a marker rather than a mailbox. Deriving it from the
                        // (unique) login is what satisfies the unique email index without a second collision loop.
                        Email = $"{login}@bots.triad.example",
                        // An unguessable secret, so the one way to "be" a bot — typing its login into sign-in — still
                        // needs a password nobody holds. The hash is real BCrypt, so a stray attempt fails cleanly
                        // rather than throwing on a malformed value.
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                            Guid.NewGuid().ToString(),
                            BotHashWorkFactor
                        ),
                        // Backdated, so a bot does not read as "joined today" beside the humans.
                        CreatedAt = DateTime.UtcNow.AddDays(-(random.Next(179) + 1)),
                        IsBot = true,
                        Activity = random.Next(MaxSeededActivity + 1),
                        Wins = random.Next(MaxSeededOutcome + 1),
                        Losses = random.Next(MaxSeededOutcome + 1),
                        Ties = random.Next(MaxSeededOutcome + 1),
                    }
                );
            }

            context.Players.AddRange(bots);
            await context.SaveChangesAsync();
        }

        /// <summary>
        /// A Bogus name that no existing player (nor an earlier bot in this cohort) holds — regenerated on collision,
        /// because a login is a bot's identity everywhere (the match's player id, the profile path, the friend list).
        /// </summary>
        private static string UniqueLogin(Faker faker, HashSet<string> taken)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                // Internet.UserName() can come back blank; skip those rather than store a nameless bot.
                var login = faker.Internet.UserName().Trim();
                if (login.Length > 0 && !taken.Contains(login))
                {
                    return login;
                }
            }

            // The pool of Bogus usernames is effectively endless, so this is belt-and-braces — but a bot the login
            // column will still accept beats a seeder that loops forever.
            return $"bot-{Guid.NewGuid():N}";
        }
    }
}

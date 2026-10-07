using System.Text;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The emulated presence of bots (plans/PLAN-025-bots/plan.md §3.2): a bot is online for the share of the time its
    /// activity names, and **at least <see cref="MinimumOnline"/> bots are online at all times**, so a match can always
    /// be found.
    ///
    /// It is a pure function of the cohort and the current time — a time "bucket" is hashed per login, and a bot is
    /// online when that hash falls under its activity. Deliberately stateless: no timer to keep alive, no column to
    /// write, and two requests inside the same bucket can never disagree. A promotion pass fills the floor when the
    /// rolls leave too few online, choosing by a per-bucket rotating key so the same bots are not always the ones on.
    /// </summary>
    public static class BotPresence
    {
        /// <summary>How long a bot's online/offline state lasts before it is rolled again.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

        /// <summary>The floor: never fewer bots online than this, whatever the rolls say.</summary>
        public const int MinimumOnline = 3;

        /// <summary>
        /// The bots online right now. Independent rolls first, then the floor filled by a rotating key when the rolls
        /// leave fewer than <see cref="MinimumOnline"/> — so the guarantee holds even if every roll fails, and which
        /// bots are promoted rotates every bucket.
        /// </summary>
        public static HashSet<string> OnlineBots(IReadOnlyList<BotRegistry.Bot> bots, DateTime now)
        {
            var bucket = Bucket(now);

            var online = bots.Where(bot => Roll(bot.Login, bucket) < bot.Activity)
                .Select(bot => bot.Login)
                .ToHashSet(StringComparer.Ordinal);

            // A cohort no larger than the floor is simply all online: there is no spare bot to promote.
            if (bots.Count <= MinimumOnline)
            {
                foreach (var bot in bots)
                {
                    online.Add(bot.Login);
                }

                return online;
            }

            if (online.Count >= MinimumOnline)
            {
                return online;
            }

            // Top up to the floor. The order is a per-bucket key rather than the activity, so the bots kept online
            // when the rolls favour nobody rotate instead of being the same few forever.
            foreach (
                var bot in bots.Where(bot => !online.Contains(bot.Login))
                    .OrderBy(bot => RotateKey(bot.Login, bucket))
                    .Take(MinimumOnline - online.Count)
            )
            {
                online.Add(bot.Login);
            }

            return online;
        }

        /// <summary>Whether one bot is online — the same answer <see cref="OnlineBots"/> gives for the whole cohort.</summary>
        public static bool IsOnline(
            IReadOnlyList<BotRegistry.Bot> bots,
            string login,
            DateTime now
        ) => OnlineBots(bots, now).Contains(login);

        /// <summary>
        /// The 0–99 roll for one bot in one bucket: below its activity means online, so an activity of 40 is online
        /// ~40 % of the time and an activity of 0 never is.
        /// </summary>
        public static int Roll(string login, long bucket) =>
            (int)(StableHash($"{login}:{bucket}") % 100);

        private static long Bucket(DateTime now) => now.ToUniversalTime().Ticks / Window.Ticks;

        /// <summary>The rotating tiebreak used only to fill the floor — deliberately not the activity.</summary>
        private static uint RotateKey(string login, long bucket) =>
            StableHash($"rotate:{login}:{bucket}");

        /// <summary>
        /// FNV-1a (32-bit) over the UTF-8 bytes — stable across processes and runs, so the emulated presence is
        /// reproducible in a test and identical between two requests (unlike <c>string.GetHashCode</c>, which .NET
        /// randomises per process).
        /// </summary>
        public static uint StableHash(string value)
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;

            var hash = offset;
            foreach (var octet in Encoding.UTF8.GetBytes(value))
            {
                hash ^= octet;
                hash *= prime;
            }

            return hash;
        }
    }
}

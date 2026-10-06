using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// Who hears that a friend came online (plans/PLAN-023-social-friends-list/plan.md §3.3): that player's **accepted**
    /// friends, once per real transition, and nobody else. The registry is the real one — it has no dependencies — so a
    /// test connects and disconnects connections exactly as the hub does.
    /// </summary>
    public class PresenceServiceTests
    {
        private const string Me = "argel";
        private const string Rival = "rival";
        private const string Stranger = "stranger";
        private const string Waiter = "waiter";

        [Fact]
        public async Task Connect_TellsEveryFriendOnce()
        {
            using var context = CreateContext();
            var (service, notifier, _) = CreateService(context);
            await SeedAcceptedAsync(context, Me, Rival);
            await SeedAcceptedAsync(context, Me, Stranger);

            await service.ConnectAsync("c1", Me);

            // Two friends, one push each, naming **me** as the one who appeared.
            Assert.Equal(2, notifier.PresencePushes.Count);
            var push = Assert.Single(notifier.PresenceFor(Rival));
            Assert.Equal(Me, push.Login);
            Assert.True(push.Online);
            Assert.Single(notifier.PresenceFor(Stranger));
        }

        [Fact]
        public async Task Connect_DoesNotTellAnyoneWhoIsNotAFriend()
        {
            using var context = CreateContext();
            var (service, notifier, _) = CreateService(context);
            await SeedPendingAsync(context, Waiter, Me);
            await SeedAcceptedAsync(context, Rival, Stranger);

            await service.ConnectAsync("c1", Me);

            // A pending request is not a friendship, and a pair I am not in is not my business.
            Assert.Empty(notifier.PresencePushes);
        }

        [Fact]
        public async Task Connect_WithASecondConnection_IsSilent()
        {
            using var context = CreateContext();
            var (service, notifier, _) = CreateService(context);
            await SeedAcceptedAsync(context, Me, Rival);

            await service.ConnectAsync("c1", Me);
            await service.ConnectAsync("c2", Me);

            // Already online: a second tab changes nothing anyone else should hear about.
            Assert.Single(notifier.PresenceFor(Rival));
        }

        [Fact]
        public async Task Disconnect_WhenTheLastConnectionGoes_TellsEveryFriendOnce()
        {
            using var context = CreateContext();
            var (service, notifier, _) = CreateService(context);
            await SeedAcceptedAsync(context, Me, Rival);

            await service.ConnectAsync("c1", Me);
            await service.DisconnectAsync("c1");

            Assert.Equal(2, notifier.PresenceFor(Rival).Count);
            var final = notifier.PresenceFor(Rival).Last();
            Assert.Equal(Me, final.Login);
            Assert.False(final.Online);
        }

        [Fact]
        public async Task Disconnect_WithAnotherConnectionLeft_IsSilent()
        {
            using var context = CreateContext();
            var (service, notifier, _) = CreateService(context);
            await SeedAcceptedAsync(context, Me, Rival);

            await service.ConnectAsync("c1", Me);
            await service.ConnectAsync("c2", Me);
            await service.DisconnectAsync("c1");

            // Still one connection holding me: no departure to announce.
            Assert.Single(notifier.PresenceFor(Rival));

            await service.DisconnectAsync("c2");
            Assert.Equal(2, notifier.PresenceFor(Rival).Count);
            Assert.False(notifier.PresenceFor(Rival).Last().Online);
        }

        [Fact]
        public async Task Disconnect_ForAnUnknownConnection_DoesNothing()
        {
            using var context = CreateContext();
            var (service, notifier, presence) = CreateService(context);
            await SeedAcceptedAsync(context, Me, Rival);

            await service.DisconnectAsync("never-seen");

            // A connection that never subscribed has no login and no friends to tell.
            Assert.Empty(notifier.PresencePushes);
            Assert.False(presence.IsOnline(Me));
        }

        private static (PresenceService Service, RecordingPlayerNotifier Notifier, IPlayerPresence Presence)
            CreateService(TripleTriadContext context)
        {
            var notifier = new RecordingPlayerNotifier();
            var presence = new ConnectionPresence();

            return (
                new PresenceService(presence, new FriendshipRepository(context), notifier),
                notifier,
                presence
            );
        }

        /// <summary>An accepted pair, stored in the canonical order the table insists on.</summary>
        private static async Task SeedAcceptedAsync(
            TripleTriadContext context,
            string first,
            string second
        )
        {
            var (a, b) = string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);

            context.Friendships.Add(
                new Friendship
                {
                    PlayerA = a,
                    PlayerB = b,
                    Status = FriendshipStatus.Accepted,
                    RequestedBy = first,
                    CreatedAt = DateTime.UtcNow,
                    RespondedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }

        /// <summary>A request nobody has answered — deliberately not a friendship.</summary>
        private static async Task SeedPendingAsync(
            TripleTriadContext context,
            string requester,
            string target
        )
        {
            var (a, b) =
                string.CompareOrdinal(requester, target) <= 0
                    ? (requester, target)
                    : (target, requester);

            context.Friendships.Add(
                new Friendship
                {
                    PlayerA = a,
                    PlayerB = b,
                    Status = FriendshipStatus.Pending,
                    RequestedBy = requester,
                    CreatedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );
    }
}

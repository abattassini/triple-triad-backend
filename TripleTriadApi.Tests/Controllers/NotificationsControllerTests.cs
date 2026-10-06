using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Controllers;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;
using TripleTriadApi.Tests.Services;

namespace TripleTriadApi.Tests.Controllers
{
    /// <summary>
    /// The inbox endpoints as the client sees them: the page the panel lists (with the badge's number in the same
    /// answer), the mark-read pair, and the `404` that keeps somebody else's inbox unprobeable
    /// (plans/PLAN-022-notifications-and-friends/plan.md §3.4).
    /// </summary>
    public class NotificationsControllerTests
    {
        private const string Me = "argel";
        private const string Rival = "rival";

        [Fact]
        public async Task GetNotifications_AnswersTheCountAndTheRowsShapedForThePanel()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Rival, avatarUrl: "avatars/rival.png");

            // A request that is still the caller's to answer, and a kind that has nothing to do with a friendship.
            var waiting = await NotifyAsync(context, NotificationTypes.FriendRequest, Rival, 12);
            await SeedFriendshipAsync(context, 12, FriendshipStatus.Pending);
            var other = await NotifyAsync(context, "something_else", Rival, subjectId: null);

            var result = await CreateController(context, Me).GetNotifications();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal(2, root.GetProperty("unreadCount").GetInt32());

            var rows = root.GetProperty("notifications");
            Assert.Equal(2, rows.GetArrayLength());

            // Newest first, and every field the panel draws is present — including the actor's avatar, so a
            // notification looks like the player it is about.
            var newest = rows[0];
            Assert.Equal(other.Id, newest.GetProperty("id").GetInt32());
            Assert.Equal("something_else", newest.GetProperty("type").GetString());
            Assert.Equal(Rival, newest.GetProperty("actorLogin").GetString());
            Assert.Equal("avatars/rival.png", newest.GetProperty("actorAvatarUrl").GetString());
            Assert.Equal(JsonValueKind.Null, newest.GetProperty("subjectId").ValueKind);
            Assert.Equal(JsonValueKind.Null, newest.GetProperty("readAt").ValueKind);
            // Not a friend kind, so there is no state to act on — the property is present and null either way, which is
            // what lets the client read one field instead of switching on the kind.
            Assert.Equal(JsonValueKind.Null, newest.GetProperty("friendshipState").ValueKind);

            var oldest = rows[1];
            Assert.Equal(waiting.Id, oldest.GetProperty("id").GetInt32());
            Assert.Equal(FriendshipStates.Incoming, oldest.GetProperty("friendshipState").GetString());
        }

        [Fact]
        public async Task GetNotifications_HidesARequestOnceItWasAnswered()
        {
            using var context = CreateContext();
            var request = await NotifyAsync(context, NotificationTypes.FriendRequest, Rival, 12);
            // The state an answer leaves behind: the pair accepted, and the request it answered already stamped read.
            request.ReadAt = DateTime.UtcNow;
            await SeedFriendshipAsync(context, 12, FriendshipStatus.Accepted);

            var result = await CreateController(context, Me).GetNotifications();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(0, json.RootElement.GetProperty("unreadCount").GetInt32());
            Assert.Empty(json.RootElement.GetProperty("notifications").EnumerateArray());
        }

        /// <summary>The pair row a request's `SubjectId` points at — its state is what decides whether it is listed.</summary>
        private static async Task SeedFriendshipAsync(
            TripleTriadContext context,
            int id,
            string status
        )
        {
            context.Friendships.Add(
                new Friendship
                {
                    Id = id,
                    PlayerA = Me,
                    PlayerB = Rival,
                    Status = status,
                    RequestedBy = Rival,
                    CreatedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }

        [Fact]
        public async Task GetNotifications_WithLimitZero_AnswersTheCountWithNoRows()
        {
            using var context = CreateContext();
            await NotifyAsync(context, NotificationTypes.FriendRequest, Rival, 1);

            var result = await CreateController(context, Me).GetNotifications(limit: 0);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(1, json.RootElement.GetProperty("unreadCount").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("notifications").GetArrayLength());
        }

        [Fact]
        public async Task GetNotifications_ForAnEmptyInbox_IsAnEmptyPageRatherThan404()
        {
            using var context = CreateContext();

            var result = await CreateController(context, Me).GetNotifications();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(0, json.RootElement.GetProperty("unreadCount").GetInt32());
            Assert.Empty(json.RootElement.GetProperty("notifications").EnumerateArray());
        }

        [Fact]
        public async Task MarkRead_AnswersTheNewCount()
        {
            using var context = CreateContext();
            var notification = await NotifyAsync(context, NotificationTypes.FriendRequest, Rival, 1);

            var result = await CreateController(context, Me).MarkRead(notification.Id);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(0, json.RootElement.GetProperty("unreadCount").GetInt32());
            Assert.NotNull((await context.Notifications.SingleAsync()).ReadAt);
        }

        [Fact]
        public async Task MarkRead_ForSomeoneElsesNotification_Is404()
        {
            using var context = CreateContext();
            var notification = await NotifyAsync(context, NotificationTypes.FriendRequest, Rival, 1);

            // The row is in *my* inbox; the other player may not reach it, and learns nothing by trying.
            var result = await CreateController(context, Rival).MarkRead(notification.Id);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.Null((await context.Notifications.SingleAsync()).ReadAt);
        }

        [Fact]
        public async Task MarkAllRead_AnswersHowManyItChanged()
        {
            using var context = CreateContext();
            await NotifyAsync(context, NotificationTypes.FriendRequest, Rival, 1);
            await NotifyAsync(context, "something_else", Rival, subjectId: null);

            var result = await CreateController(context, Me).MarkAllRead();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(2, json.RootElement.GetProperty("markedRead").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("unreadCount").GetInt32());
            Assert.Empty(await context.Notifications.Where(row => row.ReadAt == null).ToListAsync());
        }

        [Theory]
        [InlineData("get")]
        [InlineData("mark-one")]
        [InlineData("mark-all")]
        public async Task EveryInboxEndpoint_WithoutALogin_Is401(string endpoint)
        {
            using var context = CreateContext();
            var notification = await NotifyAsync(context, NotificationTypes.FriendRequest, Rival, 1);
            var controller = CreateController(context, login: null);

            var result = endpoint switch
            {
                "get" => await controller.GetNotifications(),
                "mark-one" => await controller.MarkRead(notification.Id),
                _ => await controller.MarkAllRead(),
            };

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        /// <summary>
        /// Puts a notification in <see cref="Me"/>'s inbox through the real service — which is also what proves the
        /// controller is reading rows the write path produced rather than rows a test invented.
        /// </summary>
        private static async Task<Notification> NotifyAsync(
            TripleTriadContext context,
            string type,
            string actorId,
            int? subjectId
        )
        {
            var service = new NotificationService(
                new NotificationRepository(context),
                new FriendshipRepository(context),
                new PlayerRepository(context),
                new RecordingPlayerNotifier()
            );

            return await service.CreateAsync(Me, type, actorId, subjectId);
        }

        /// <summary>The controller under test, with the authenticated login (or none) in its HttpContext.</summary>
        private static NotificationsController CreateController(
            TripleTriadContext context,
            string? login
        )
        {
            var controller = new NotificationsController(
                new NotificationService(
                    new NotificationRepository(context),
                    new FriendshipRepository(context),
                    new PlayerRepository(context),
                    new RecordingPlayerNotifier()
                )
            );

            var claims = login is null
                ? new List<Claim>()
                : [new Claim(ClaimTypes.NameIdentifier, login)];

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims)),
                },
            };

            return controller;
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        private static async Task SeedPlayerAsync(
            TripleTriadContext context,
            string login,
            string? avatarUrl = null
        )
        {
            context.Players.Add(
                new Player
                {
                    Login = login,
                    Email = $"{login}@example.com",
                    PasswordHash = "hash",
                    AvatarUrl = avatarUrl,
                    CreatedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }
    }
}

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Controllers;
using TripleTriadApi.Data;
using TripleTriadApi.Models;
using TripleTriadApi.Services;
using TripleTriadApi.Tests.Services;

namespace TripleTriadApi.Tests.Controllers
{
    /// <summary>
    /// The three friend endpoints as the client sees them: asking (including the ask that answers somebody else's),
    /// accepting, and removing — with each refusal landing on the status code the panel can act on
    /// (plans/PLAN-022-notifications-and-friends/plan.md §3.4).
    ///
    /// The controller is exercised directly with a hand-built <see cref="HttpContext"/> over EF InMemory, the same
    /// pattern as the other controller tests.
    /// </summary>
    public class FriendsControllerTests
    {
        private const string Me = "argel";
        private const string Rival = "rival";

        [Fact]
        public async Task RequestFriend_AnswersTheStoredLoginAndTheState()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);

            var result = await CreateController(context, Me).RequestFriend(Rival);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
            var root = json.RootElement;

            Assert.Equal(Rival, root.GetProperty("login").GetString());
            Assert.Equal(FriendshipStates.Requested, root.GetProperty("friendship").GetString());
            // …and the other player now has the request in their inbox.
            Assert.Equal(NotificationTypes.FriendRequest, (await context.Notifications.SingleAsync()).Type);
        }

        [Fact]
        public async Task RequestFriend_ThatAnswersTheirOwnRequest_ReportsFriends()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);

            await CreateController(context, Rival).RequestFriend(Me);
            var result = await CreateController(context, Me).RequestFriend(Rival);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(FriendshipStates.Friends, json.RootElement.GetProperty("friendship").GetString());
        }

        [Theory]
        [InlineData("nobody-here")]
        [InlineData(CpuOpponent.Login)] // the CPU sentinel: an identity, never a friend
        public async Task RequestFriend_ForAPlayerWhoDoesNotExist_Is404(string login)
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);

            var result = await CreateController(context, Me).RequestFriend(login);

            Assert.IsType<NotFoundObjectResult>(result.Result);
            Assert.Empty(context.Friendships);
        }

        [Fact]
        public async Task RequestFriend_ForYourself_Is400()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);

            var result = await CreateController(context, Me).RequestFriend(Me);

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }

        [Fact]
        public async Task AcceptFriend_TurnsTheRequestIntoAFriendship()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);

            await CreateController(context, Rival).RequestFriend(Me);
            var result = await CreateController(context, Me).AcceptFriend(Rival);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(Rival, json.RootElement.GetProperty("login").GetString());
            Assert.Equal(FriendshipStates.Friends, json.RootElement.GetProperty("friendship").GetString());
            Assert.Equal(FriendshipStatus.Accepted, (await context.Friendships.SingleAsync()).Status);
        }

        [Fact]
        public async Task AcceptFriend_WithNothingWaiting_Is409()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);

            var result = await CreateController(context, Me).AcceptFriend(Rival);

            Assert.IsType<ConflictObjectResult>(result.Result);
        }

        [Fact]
        public async Task RemoveFriend_AnswersNoneAndDeletesTheRow()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);
            await SeedPlayerAsync(context, Rival);

            await CreateController(context, Me).RequestFriend(Rival);
            var result = await CreateController(context, Rival).RemoveFriend(Me);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal(FriendshipStates.None, json.RootElement.GetProperty("friendship").GetString());
            Assert.Empty(context.Friendships);
        }

        [Fact]
        public async Task RemoveFriend_ForAnUnknownPlayer_Is404()
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Me);

            var result = await CreateController(context, Me).RemoveFriend("nobody-here");

            Assert.IsType<NotFoundObjectResult>(result.Result);
        }

        [Theory]
        [InlineData("request")]
        [InlineData("accept")]
        [InlineData("remove")]
        public async Task EveryFriendAction_WithoutALogin_Is401(string action)
        {
            using var context = CreateContext();
            await SeedPlayerAsync(context, Rival);

            var controller = CreateController(context, login: null);

            var result = action switch
            {
                "request" => await controller.RequestFriend(Rival),
                "accept" => await controller.AcceptFriend(Rival),
                _ => await controller.RemoveFriend(Rival),
            };

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        /// <summary>The controller under test, with the authenticated login (or none) in its HttpContext.</summary>
        private static FriendsController CreateController(TripleTriadContext context, string? login)
        {
            var controller = new FriendsController(FriendshipTestHarness.CreateFriendService(context));

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

        private static async Task SeedPlayerAsync(TripleTriadContext context, string login)
        {
            context.Players.Add(
                new Player
                {
                    Login = login,
                    Email = $"{login}@example.com",
                    PasswordHash = "hash",
                    CreatedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }
    }
}

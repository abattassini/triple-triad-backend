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
    /// The challenge endpoints as the client sees them (plans/PLAN-027-friend-challenge/plan.md §3.4): the pending match
    /// an invitation answers, and each refusal landing on a status code the UI can act on. The controller is exercised
    /// directly with a hand-built <see cref="HttpContext"/> over EF InMemory, like the other controller tests.
    /// </summary>
    public class ChallengesControllerTests
    {
        private const string Me = "argel";
        private const string Rival = "rival";
        private const string Stranger = "stranger";

        [Fact]
        public async Task Challenge_AFriendWhoIsOnline_AnswersThePendingMatch()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);

            var result = await CreateController(context, Me, presence: Online(Rival))
                .Challenge(Rival);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            Assert.Equal("pending", json.RootElement.GetProperty("status").GetString());
            Assert.True(json.RootElement.GetProperty("matchId").GetInt32() > 0);
        }

        [Fact]
        public async Task Challenge_ForAPlayerWhoDoesNotExist_Is404()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me);

            var result = await CreateController(context, Me).Challenge("nobody-here");

            Assert.IsType<NotFoundObjectResult>(result.Result);
        }

        [Fact]
        public async Task Accept_ForSomebodyElsesChallenge_Is403()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival, Stranger);
            await BefriendAsync(context, Me, Rival);

            var created = await CreateController(context, Me, presence: Online(Rival))
                .Challenge(Rival);
            var matchId = ReadMatchId(created);

            // Stranger is not the match's second seat, so answering it is forbidden rather than hidden.
            var result = await CreateController(context, Stranger).Accept(matchId);

            var status = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(403, status.StatusCode);
        }

        [Fact]
        public async Task Refuse_WhenTheChallengeIsAlreadyAnswered_Is409()
        {
            using var context = CreateContext();
            await SeedAsync(context, Me, Rival);
            await BefriendAsync(context, Me, Rival);

            var controller = CreateController(context, Me, presence: Online(Rival));
            var matchId = ReadMatchId(await controller.Challenge(Rival));
            Assert.IsType<OkObjectResult>(
                (await CreateController(context, Rival).Refuse(matchId)).Result
            );

            // A stale dialog answering twice deserves a conflict, not a silent second write.
            Assert.IsType<ConflictObjectResult>(
                (await CreateController(context, Rival).Refuse(matchId)).Result
            );
        }

        [Theory]
        [InlineData("challenge")]
        [InlineData("accept")]
        [InlineData("refuse")]
        [InlineData("cancel")]
        [InlineData("expire")]
        public async Task EveryChallengeAction_WithoutALogin_Is401(string action)
        {
            using var context = CreateContext();
            await SeedAsync(context, Rival);

            var controller = CreateController(context, login: null);

            var result = action switch
            {
                "challenge" => await controller.Challenge(Rival),
                "accept" => await controller.Accept(1),
                "refuse" => await controller.Refuse(1),
                "cancel" => await controller.Cancel(1),
                _ => await controller.Expire(),
            };

            Assert.IsType<UnauthorizedObjectResult>(result.Result);
        }

        /// <summary>The id of the match a successful `Challenge` answered.</summary>
        private static int ReadMatchId(ActionResult<object> result)
        {
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

            return json.RootElement.GetProperty("matchId").GetInt32();
        }

        private static ConnectionPresence Online(params string[] logins)
        {
            var presence = new ConnectionPresence();

            foreach (var login in logins)
            {
                presence.AddConnection($"c-{login}", login);
            }

            return presence;
        }

        private static TripleTriadContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<TripleTriadContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options
            );

        private static async Task SeedAsync(TripleTriadContext context, params string[] logins)
        {
            foreach (var login in logins)
            {
                context.Players.Add(
                    new Player
                    {
                        Login = login,
                        Email = $"{login}@example.com",
                        PasswordHash = "hash",
                    }
                );
            }

            await context.SaveChangesAsync();
        }

        private static async Task BefriendAsync(
            TripleTriadContext context,
            string first,
            string second
        )
        {
            var (a, b) =
                string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);

            context.Friendships.Add(
                new Friendship
                {
                    PlayerA = a,
                    PlayerB = b,
                    Status = FriendshipStatus.Accepted,
                    RequestedBy = a,
                    CreatedAt = DateTime.UtcNow,
                    RespondedAt = DateTime.UtcNow,
                }
            );

            await context.SaveChangesAsync();
        }

        /// <summary>The controller under test, with the authenticated login (or none) in its HttpContext.</summary>
        private static ChallengesController CreateController(
            TripleTriadContext context,
            string? login,
            IPlayerPresence? presence = null
        )
        {
            var controller = new ChallengesController(
                FriendshipTestHarness.CreateChallengeService(context, presence: presence)
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
    }
}

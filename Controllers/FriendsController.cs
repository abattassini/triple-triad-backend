using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TripleTriadApi.Services;

namespace TripleTriadApi.Controllers
{
    /// <summary>
    /// Asking to be friends, accepting, and removing — the relationship half of
    /// `plans/PLAN-022-notifications-and-friends/plan.md` §3.4. Every answer echoes the **stored** login of the other
    /// player (so a caller who guessed the casing is corrected) and the pair's state from the caller's point of view,
    /// which is exactly what the panel behind the request draws.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class FriendsController : ControllerBase
    {
        private readonly FriendService _friendService;

        public FriendsController(FriendService friendService)
        {
            _friendService = friendService;
        }

        /// <summary>
        /// Asks another player to be friends. Asking again while your own request is outstanding is the same request
        /// rather than an error, and asking someone who has already asked you **accepts theirs** instead of queueing a
        /// second one (<see cref="FriendService.RequestAsync"/>). The answer is `requested`, `friends`, or the refusal.
        /// </summary>
        [Authorize]
        [EnableRateLimiting(FriendService.RateLimitPolicyName)]
        [HttpPost("{login}")]
        public async Task<ActionResult<object>> RequestFriend(string login)
        {
            try
            {
                var caller = GetCurrentLogin();
                if (string.IsNullOrEmpty(caller))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _friendService.RequestAsync(caller, login);

                return ToActionResult(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Accepts the request <paramref name="login"/> has outstanding, and tells them it was accepted. An accept
        /// with nothing waiting is a `409` rather than a silent friendship: a stale screen deserves an answer.
        /// </summary>
        [Authorize]
        [EnableRateLimiting(FriendService.RateLimitPolicyName)]
        [HttpPost("{login}/accept")]
        public async Task<ActionResult<object>> AcceptFriend(string login)
        {
            try
            {
                var caller = GetCurrentLogin();
                if (string.IsNullOrEmpty(caller))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _friendService.AcceptAsync(caller, login);

                return ToActionResult(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Removes whatever the two have — a decline, a cancellation of your own request, or an unfriending, which are
        /// one write. Idempotent, so a stale panel's *Decline* is never an error.
        /// </summary>
        [Authorize]
        [EnableRateLimiting(FriendService.RateLimitPolicyName)]
        [HttpDelete("{login}")]
        public async Task<ActionResult<object>> RemoveFriend(string login)
        {
            try
            {
                var caller = GetCurrentLogin();
                if (string.IsNullOrEmpty(caller))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _friendService.RemoveAsync(caller, login);

                return ToActionResult(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// The one place a refusal becomes a status code: an unknown login is a `404` (which is also how the CPU
        /// sentinel is refused — it has no row), a conflict is a `409`, and everything else the service refuses is a
        /// `400` with its sentence.
        /// </summary>
        private ActionResult<object> ToActionResult(FriendService.FriendResult result)
        {
            if (result.Succeeded)
            {
                return Ok(new { login = result.Login, friendship = result.Friendship });
            }

            var body = new { error = result.ErrorMessage };

            return result.Failure switch
            {
                FriendService.FriendFailure.UnknownPlayer => NotFound(body),
                FriendService.FriendFailure.Conflict => Conflict(body),
                _ => BadRequest(body),
            };
        }

        // The JWT subject is the player's login, which is also the playerId used across the game layer.
        private string? GetCurrentLogin()
        {
            return User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        }
    }
}

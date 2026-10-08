using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripleTriadApi.Services;

namespace TripleTriadApi.Controllers
{
    /// <summary>
    /// Challenging a friend to a match (plans/PLAN-027-friend-challenge/plan.md §3.4). Thin on purpose: every rule lives
    /// in <see cref="ChallengeService"/>, and this only maps its results onto status codes.
    ///
    /// The answering endpoints name the **match**, not the challenge, because a challenge *is* a `Matches` row — which
    /// is also the id the invited player's inbox row carries as its `SubjectId`, so the same call serves the dialog and
    /// the bell.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ChallengesController : ControllerBase
    {
        private readonly ChallengeService _challenges;

        public ChallengesController(ChallengeService challenges)
        {
            _challenges = challenges;
        }

        /// <summary>
        /// Invites <paramref name="login"/> — a friend who must be online. The answer is the pending match, so the
        /// caller can hold on to it while it waits.
        /// </summary>
        [Authorize]
        [HttpPost("{login}")]
        public async Task<ActionResult<object>> Challenge(string login)
        {
            try
            {
                var caller = GetCurrentLogin();
                if (string.IsNullOrEmpty(caller))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _challenges.ChallengeAsync(caller, login, DateTime.UtcNow);

                return ToActionResult(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Accepts the challenge: the match goes `active` and both sides pick their hand.</summary>
        [Authorize]
        [HttpPost("{matchId:int}/accept")]
        public async Task<ActionResult<object>> Accept(int matchId)
        {
            try
            {
                var caller = GetCurrentLogin();
                if (string.IsNullOrEmpty(caller))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _challenges.AcceptAsync(matchId, caller, DateTime.UtcNow);

                return ToActionResult(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Declines the challenge — the match's status becomes `refused`.</summary>
        [Authorize]
        [HttpPost("{matchId:int}/refuse")]
        public async Task<ActionResult<object>> Refuse(int matchId)
        {
            try
            {
                var caller = GetCurrentLogin();
                if (string.IsNullOrEmpty(caller))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _challenges.RefuseAsync(matchId, caller);

                return ToActionResult(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Withdraws the caller's own outstanding challenge.</summary>
        [Authorize]
        [HttpPost("{matchId:int}/cancel")]
        public async Task<ActionResult<object>> Cancel(int matchId)
        {
            try
            {
                var caller = GetCurrentLogin();
                if (string.IsNullOrEmpty(caller))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _challenges.CancelAsync(matchId, caller);

                return ToActionResult(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// The **sign-out** hook (§3.2 #5): ends every pending challenge the caller is in, either seat, and tells the
        /// other player. Called fire-and-forget by the client as the session is cleared — a refresh is deliberately not
        /// a sign-out, or reloading would void an invitation.
        ///
        /// The literal `expire` segment wins over `{login}` in ASP.NET Core's routing, which keeps the two apart.
        /// </summary>
        [Authorize]
        [HttpPost("expire")]
        public async Task<ActionResult<object>> Expire()
        {
            try
            {
                var caller = GetCurrentLogin();
                if (string.IsNullOrEmpty(caller))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var expired = await _challenges.ExpireInvolvingAsync(caller);

                return Ok(new { expired });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// The one place a refusal becomes a status code: an unknown player or match is a `404`, somebody else's
        /// challenge is a `403`, "not pending" and "you are busy" are a `409`, and everything else is a `400` with its
        /// sentence.
        /// </summary>
        private ActionResult<object> ToActionResult(ChallengeService.ChallengeResult result)
        {
            if (result.Succeeded)
            {
                return Ok(new { matchId = result.MatchId, status = result.Status });
            }

            var body = new { error = result.ErrorMessage };

            return result.Failure switch
            {
                ChallengeService.ChallengeFailure.UnknownPlayer => NotFound(body),
                ChallengeService.ChallengeFailure.UnknownMatch => NotFound(body),
                ChallengeService.ChallengeFailure.NotYours => StatusCode(403, body),
                ChallengeService.ChallengeFailure.Busy => Conflict(body),
                ChallengeService.ChallengeFailure.NotPending => Conflict(body),
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

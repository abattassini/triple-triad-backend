using System.Security.Claims;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;
using TripleTriadApi.Validators;

namespace TripleTriadApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlayerController : ControllerBase
    {
        private readonly IPlayerRepository _playerRepository;
        private readonly IPlayerCardRepository _playerCardRepository;
        private readonly IPlayerPackRepository _playerPackRepository;
        private readonly IGameRepository _gameRepository;
        private readonly PasswordHasherService _passwordHasher;
        private readonly RegisterPlayerRequestValidator _registerValidator;
        private readonly ResetPasswordRequestValidator _resetPasswordValidator;
        private readonly TokenService _tokenService;
        private readonly PasswordResetService _passwordResetService;

        public PlayerController(
            IPlayerRepository playerRepository,
            IPlayerCardRepository playerCardRepository,
            IPlayerPackRepository playerPackRepository,
            IGameRepository gameRepository,
            PasswordHasherService passwordHasher,
            RegisterPlayerRequestValidator registerValidator,
            ResetPasswordRequestValidator resetPasswordValidator,
            TokenService tokenService,
            PasswordResetService passwordResetService
        )
        {
            _playerRepository = playerRepository;
            _playerCardRepository = playerCardRepository;
            _playerPackRepository = playerPackRepository;
            _gameRepository = gameRepository;
            _passwordHasher = passwordHasher;
            _registerValidator = registerValidator;
            _resetPasswordValidator = resetPasswordValidator;
            _tokenService = tokenService;
            _passwordResetService = passwordResetService;
        }

        [HttpPost("register")]
        public async Task<ActionResult<object>> Register([FromBody] RegisterPlayerRequest request)
        {
            try
            {
                // Server-side validation via FluentValidation mirrors the frontend rules.
                var validationResult = _registerValidator.Validate(request);
                if (!validationResult.IsValid)
                {
                    return BadRequest(new { error = FirstErrorMessage(validationResult) });
                }

                var login = request.Login.Trim();
                var email = request.Email.Trim();

                if (await _playerRepository.LoginExistsAsync(login))
                {
                    return StatusCode(409, new { error = "Login is already taken." });
                }

                if (await _playerRepository.EmailExistsAsync(email))
                {
                    return StatusCode(409, new { error = "Email is already in use." });
                }

                var player = await _playerRepository.CreateAsync(
                    new Player
                    {
                        Login = login,
                        Email = email,
                        PasswordHash = _passwordHasher.Hash(request.Password),
                        CreatedAt = DateTime.UtcNow,
                    }
                );

                // A new account starts with 0 cards and PackService.StartingPacks unopened packs, so the profile
                // below already reports them and the Welcome page can send the player straight to opening them.
                await _playerPackRepository.GrantAsync(
                    player.Login,
                    PackService.StandardPackCode,
                    PackService.StartingPacks
                );

                return StatusCode(201, await ToProfileAsync(player));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Verifies credentials and returns a signed JWT plus the player profile.
        /// Accepts either the player's login or email as the identifier.
        /// </summary>
        [HttpPost("sign-in")]
        public async Task<ActionResult<object>> SignIn([FromBody] SignInPlayerRequest request)
        {
            try
            {
                var identifier = request.Identifier.Trim();
                if (string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(request.Password))
                {
                    return BadRequest(new { error = "Identifier and password are required." });
                }

                var player = await _playerRepository.FindByLoginAsync(identifier);
                if (player is null && identifier.Contains("@"))
                {
                    player = await _playerRepository.FindByEmailAsync(identifier);
                }

                // Generic message avoids leaking whether the login/email exists.
                if (
                    player is null
                    || !_passwordHasher.Verify(request.Password, player.PasswordHash)
                )
                {
                    return Unauthorized(new { error = "Invalid login or password." });
                }

                var token = _tokenService.IssueToken(player);

                return Ok(new { token, player = await ToProfileAsync(player) });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Starts a password recovery: if the address belongs to an account, a single-use code is emailed to it.
        ///
        /// The answer is <strong>always</strong> the same <c>202</c> with the same body — whether the address exists,
        /// whether a limit refused it, and whether the mail actually went out. That is deliberate. Any other answer
        /// would let this endpoint be used to ask "does this person have an account here?", which is the
        /// reconnaissance half of an account-takeover attempt; it is the same reasoning that makes sign-in answer with
        /// a generic "Invalid login or password." The real signal lives in the logs: a refusal is logged as a warning
        /// and a delivery failure as an error.
        ///
        /// There is no "was my email sent?" follow-up either, for the same reason.
        /// </summary>
        [HttpPost("forgot-password")]
        [EnableRateLimiting(PasswordResetOptions.RateLimitPolicyName)]
        public async Task<ActionResult<object>> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            try
            {
                var email = request.Email is null ? string.Empty : request.Email.Trim();

                if (!string.IsNullOrEmpty(email))
                {
                    // The outcome is deliberately dropped: every value leads to the same response. It exists so the
                    // service can log exactly what happened, and so tests can assert on it.
                    await _passwordResetService.RequestResetAsync(
                        email,
                        HttpContext.Connection.RemoteIpAddress?.ToString(),
                        DateTime.UtcNow
                    );
                }

                return Accepted(
                    new { message = "If that email belongs to an account, a reset code is on its way." }
                );
            }
            catch (Exception ex)
            {
                // Safe to be honest here: a database failure is orthogonal to whether the account exists, so a 500
                // leaks nothing that the generic 202 above is protecting.
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Completes a recovery: the emailed code plus the new password to set.
        ///
        /// This endpoint <em>can</em> be specific about failure, unlike <see cref="ForgotPassword"/>, because the code
        /// is the proof of ownership rather than the address. "That code is not valid" tells an attacker nothing they
        /// did not already know, and a player who mistyped needs to be told. It cannot be used to probe for accounts:
        /// a code belongs to an account, an address does not.
        ///
        /// A success invalidates every session the player had (see <see cref="Player.SessionVersion"/>), so the client
        /// must send them to sign in again rather than leaving them signed in.
        /// </summary>
        [HttpPost("reset-password")]
        [EnableRateLimiting(PasswordResetOptions.RateLimitPolicyName)]
        public async Task<ActionResult<object>> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            try
            {
                var validationResult = _resetPasswordValidator.Validate(request);
                if (!validationResult.IsValid)
                {
                    return BadRequest(new { error = FirstErrorMessage(validationResult) });
                }

                var result = await _passwordResetService.ResetPasswordAsync(
                    request.Token,
                    request.NewPassword,
                    DateTime.UtcNow
                );

                if (!result.Succeeded)
                {
                    return BadRequest(new { error = result.Error });
                }

                return Ok(new { message = "Your password has been changed. You can sign in now." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Returns the profile of the authenticated player (JWT subject is the login).
        /// </summary>
        [Authorize]
        [HttpGet("me")]
        public async Task<ActionResult<object>> Me()
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var player = await _playerRepository.FindByLoginAsync(login);
                if (player is null)
                {
                    return NotFound(new { error = "Player not found" });
                }

                return Ok(await ToProfileAsync(player));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private string? GetCurrentLogin()
        {
            return User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        }

        /// <summary>
        /// The authenticated player's card collection: one entry per owned card with the number of copies held,
        /// ordered by card level. <paramref name="level"/> narrows the result to a single level, which is how the
        /// My Cards page loads one level at a time; a level outside the catalogue's range is a 400.
        /// </summary>
        [Authorize]
        [HttpGet("cards")]
        public async Task<ActionResult<object>> Cards([FromQuery] int? level = null)
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                if (level is not null && (level < Card.MinLevel || level > Card.MaxLevel))
                {
                    return BadRequest(
                        new
                        {
                            error = $"Level must be between {Card.MinLevel} and {Card.MaxLevel}.",
                        }
                    );
                }

                var owned = await _playerCardRepository.GetForPlayerAsync(login, level);

                return Ok(owned.Select(ToCollectionEntry));
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// The collection at a glance: the header totals plus one row per level the player owns cards in (distinct
        /// cards held, and how many the catalogue has at that level). This is what fills the level picker, so the
        /// page can show a level without loading the others.
        /// </summary>
        [Authorize]
        [HttpGet("cards/summary")]
        public async Task<ActionResult<object>> CardsSummary()
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var ownership = await _playerCardRepository.GetLevelOwnershipAsync(login);
                var catalogueCounts = await _gameRepository.GetCardCountsByLevelAsync();

                return Ok(
                    new
                    {
                        distinctCards = ownership.Sum(row => row.OwnedCount),
                        copiesOwned = ownership.Sum(row => row.OwnedCopies),
                        levels = ownership
                            .Select(row => new
                            {
                                level = row.Level,
                                ownedCount = row.OwnedCount,
                                totalCount = catalogueCounts.GetValueOrDefault(
                                    row.Level,
                                    row.OwnedCount
                                ),
                            })
                            .ToList(),
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>One collection entry: the card plus how many copies the player holds.</summary>
        private static object ToCollectionEntry(PlayerCard playerCard) =>
            new
            {
                card = new
                {
                    id = playerCard.Card.Id,
                    name = playerCard.Card.Name,
                    image = playerCard.Card.Image,
                    topValue = playerCard.Card.TopValue,
                    rightValue = playerCard.Card.RightValue,
                    bottomValue = playerCard.Card.BottomValue,
                    leftValue = playerCard.Card.LeftValue,
                    element = playerCard.Card.Element,
                    level = playerCard.Card.Level,
                },
                quantity = playerCard.Quantity,
                firstAcquiredAt = playerCard.FirstAcquiredAt,
                lastAcquiredAt = playerCard.LastAcquiredAt,
            };

        /// <summary>
        /// Shared player projection used by register, sign-in and me so the frontend
        /// always receives the same profile shape (including stats, avatar and the two
        /// shop counts the card/pack pills are drawn from).
        /// </summary>
        private async Task<object> ToProfileAsync(Player player) =>
            new
            {
                id = player.Id,
                login = player.Login,
                email = player.Email,
                createdAt = player.CreatedAt,
                coins = player.Coins,
                experience = player.Experience,
                wins = player.Wins,
                losses = player.Losses,
                ties = player.Ties,
                avatarUrl = player.AvatarUrl,
                // Counted here rather than on the client so every screen that already shows the profile has the
                // numbers the pills need — no extra request per page, and no chance of the two disagreeing.
                cardsOwned = await _playerCardRepository.GetOwnedCardCountAsync(player.Login),
                packsOwned = await _playerPackRepository.GetCountAsync(
                    player.Login,
                    PackService.StandardPackCode
                ),
            };

        /// <summary>
        /// Returns the first validation failure message, or null when valid.
        /// </summary>
        private static string? FirstErrorMessage(ValidationResult result)
        {
            foreach (var failure in result.Errors)
            {
                return failure.ErrorMessage;
            }

            return null;
        }
    }

    public class RegisterPlayerRequest
    {
        public string Login { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class SignInPlayerRequest
    {
        public string Identifier { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// Starts a recovery. Only the address is needed — the code is mailed to it, which is also the whole point: the
    /// address, not this request, is what proves ownership.
    /// </summary>
    public class ForgotPasswordRequest
    {
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>
    /// Completes a recovery: the code from the email, and the password to set. The code is the proof of ownership, so
    /// there is no login or email in this request.
    /// </summary>
    public class ResetPasswordRequest
    {
        public string Token { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }
}

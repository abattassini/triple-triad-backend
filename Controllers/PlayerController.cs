using System.Security.Claims;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
        private readonly PasswordHasherService _passwordHasher;
        private readonly RegisterPlayerRequestValidator _registerValidator;
        private readonly TokenService _tokenService;

        public PlayerController(
            IPlayerRepository playerRepository,
            IPlayerCardRepository playerCardRepository,
            PasswordHasherService passwordHasher,
            RegisterPlayerRequestValidator registerValidator,
            TokenService tokenService
        )
        {
            _playerRepository = playerRepository;
            _playerCardRepository = playerCardRepository;
            _passwordHasher = passwordHasher;
            _registerValidator = registerValidator;
            _tokenService = tokenService;
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

                return StatusCode(201, ToProfile(player));
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

                return Ok(new { token, player = ToProfile(player) });
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

                return Ok(ToProfile(player));
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
        /// ordered by card level. This is the shop's ownership table; there is no "My Cards" screen yet, so the
        /// endpoint exists for the next step (browsing a collection / building a deck).
        /// </summary>
        [Authorize]
        [HttpGet("cards")]
        public async Task<ActionResult<object>> Cards()
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var owned = await _playerCardRepository.GetForPlayerAsync(login);

                return Ok(
                    owned.Select(playerCard => new
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
                    })
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Shared player projection used by register, sign-in and me so the frontend
        /// always receives the same profile shape (including stats and avatar).
        /// </summary>
        private static object ToProfile(Player player) =>
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
}

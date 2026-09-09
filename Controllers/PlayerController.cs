using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
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
        private readonly PasswordHasherService _passwordHasher;
        private readonly RegisterPlayerRequestValidator _registerValidator;
        private readonly TokenService _tokenService;

        public PlayerController(
            IPlayerRepository playerRepository,
            PasswordHasherService passwordHasher,
            RegisterPlayerRequestValidator registerValidator,
            TokenService tokenService
        )
        {
            _playerRepository = playerRepository;
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

                return StatusCode(
                    201,
                    new
                    {
                        id = player.Id,
                        login = player.Login,
                        email = player.Email,
                        createdAt = player.CreatedAt,
                    }
                );
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
                if (player is null || !_passwordHasher.Verify(request.Password, player.PasswordHash))
                {
                    return Unauthorized(new { error = "Invalid login or password." });
                }

                var token = _tokenService.IssueToken(player);

                return Ok(
                    new
                    {
                        token,
                        player = new
                        {
                            id = player.Id,
                            login = player.Login,
                            email = player.Email,
                            createdAt = player.CreatedAt,
                        },
                    }
                );
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

                return Ok(
                    new
                    {
                        id = player.Id,
                        login = player.Login,
                        email = player.Email,
                        createdAt = player.CreatedAt,
                    }
                );
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
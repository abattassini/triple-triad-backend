using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripleTriadApi.Services;

namespace TripleTriadApi.Controllers
{
    /// <summary>
    /// The card shop: what a pack costs and what it can hold, and buying (and immediately opening) one.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ShopController : ControllerBase
    {
        private readonly PackService _packService;

        public ShopController(PackService packService)
        {
            _packService = packService;
        }

        /// <summary>
        /// The pack on offer — price, size and the odds of each card level. Served from the backend so the shop
        /// page can never show numbers that differ from what a purchase actually does.
        /// </summary>
        [Authorize]
        [HttpGet("pack")]
        public ActionResult<object> GetPack()
        {
            try
            {
                var offer = _packService.GetOffer();

                return Ok(
                    new
                    {
                        price = offer.Price,
                        cardCount = offer.CardCount,
                        levelOdds = offer
                            .LevelOdds.Select(odds => new
                            {
                                level = odds.Level,
                                weight = odds.Weight,
                                chancePercent = odds.ChancePercent,
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

        /// <summary>
        /// Buys a pack for the authenticated player and returns the cards it contained. Insufficient coins are a
        /// 400 and leave the player (and their collection) untouched.
        /// </summary>
        [Authorize]
        [HttpPost("pack/purchase")]
        public async Task<ActionResult<object>> PurchasePack()
        {
            try
            {
                var playerId = GetCurrentLogin();
                if (string.IsNullOrEmpty(playerId))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _packService.PurchaseAsync(playerId);
                if (!result.Succeeded)
                {
                    return BadRequest(new { error = result.ErrorMessage });
                }

                return Ok(
                    new
                    {
                        success = true,
                        price = PackService.PackPrice,
                        coinsAfter = result.CoinsAfter,
                        cards = result.Cards.Select(ToCardJson).ToList(),
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // The JWT subject is the player's login, which is also the playerId used across the game layer.
        private string? GetCurrentLogin()
        {
            return User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        }

        /// <summary>
        /// A drawn card plus what the pack did to the collection: how many copies the player holds now, and
        /// whether this pack was their first copy of it.
        /// </summary>
        private static object ToCardJson(PackService.PackCard packCard) =>
            new
            {
                id = packCard.Card.Id,
                name = packCard.Card.Name,
                image = packCard.Card.Image,
                topValue = packCard.Card.TopValue,
                rightValue = packCard.Card.RightValue,
                bottomValue = packCard.Card.BottomValue,
                leftValue = packCard.Card.LeftValue,
                element = packCard.Card.Element,
                level = packCard.Card.Level,
                quantityOwned = packCard.QuantityOwned,
                isNew = packCard.IsNew,
            };
    }
}

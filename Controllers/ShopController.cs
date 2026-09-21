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
        /// The pack on offer — price, size, how many the player already holds and the odds of each card level.
        /// Served from the backend so the shop page can never show numbers that differ from what a purchase
        /// actually does.
        /// </summary>
        [Authorize]
        [HttpGet("pack")]
        public async Task<ActionResult<object>> GetPack()
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var offer = _packService.GetOffer();
                var packsOwned = await _packService.GetPackCountAsync(login);

                return Ok(
                    new
                    {
                        price = offer.Price,
                        cardCount = offer.CardCount,
                        packsOwned,
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
        /// Buys a pack for the authenticated player and adds it to their inventory — the cards are drawn later,
        /// when the pack is opened (`POST pack/open`). Insufficient coins are a 400 and leave the wallet and the
        /// inventory untouched.
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
                        packsOwned = result.PacksOwned,
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Opens one of the player's unopened packs: the pack is consumed, the five cards it held are drawn and
        /// filed in the collection, and they are returned. The player owns the cards the moment this call
        /// succeeds — the frontend's flip animation only replays what was already granted. An empty inventory is
        /// a 400 and leaves the collection untouched.
        /// </summary>
        [Authorize]
        [HttpPost("pack/open")]
        public async Task<ActionResult<object>> OpenPack([FromBody] OpenPackRequest request)
        {
            try
            {
                var playerId = GetCurrentLogin();
                if (string.IsNullOrEmpty(playerId))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                // An omitted code means the pack the shop sells today, so a client can post `{}`.
                var packCode = string.IsNullOrWhiteSpace(request.PackCode)
                    ? PackService.StandardPackCode
                    : request.PackCode;

                var result = await _packService.OpenAsync(playerId, packCode);
                if (!result.Succeeded)
                {
                    return BadRequest(new { error = result.ErrorMessage });
                }

                return Ok(
                    new
                    {
                        success = true,
                        packCode = result.PackCode,
                        packsOwned = result.PacksOwned,
                        cards = result.Cards.Select(ToCardJson).ToList(),
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// The packs the player holds, one entry per stack: the code, a display name, how many cards it holds and
        /// how many are left. This is the list the My Packs page renders.
        /// </summary>
        [Authorize]
        [HttpGet("packs")]
        public async Task<ActionResult<object>> GetInventory()
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var packs = await _packService.GetInventoryAsync(login);

                return Ok(
                    packs.Select(pack => new
                    {
                        code = pack.Code,
                        name = pack.Name,
                        cardCount = pack.CardCount,
                        quantity = pack.Quantity,
                    })
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

    /// <summary>Body of `POST api/shop/pack/open`: which pack to open (omitted = the pack the shop sells).</summary>
    public class OpenPackRequest
    {
        public string? PackCode { get; set; }
    }
}

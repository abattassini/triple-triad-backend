using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TripleTriadApi.Services;

namespace TripleTriadApi.Controllers
{
    /// <summary>
    /// The inbox: what the bell counts and what the panel lists
    /// (`plans/PLAN-022-notifications-and-friends/plan.md` §3.4). Deliberately generic — this controller knows a
    /// notification has a type and an actor, not what any particular type means, which is what lets a kind be added
    /// without touching it.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class NotificationsController : ControllerBase
    {
        private readonly NotificationService _notificationService;

        public NotificationsController(NotificationService notificationService)
        {
            _notificationService = notificationService;
        }

        /// <summary>
        /// One page of the caller's inbox, newest first, together with the badge's number — one request, because the
        /// bell wants both. `limit=0` answers the count alone, which is what a badge refresh costs, and `beforeId`
        /// walks backwards through older rows.
        /// </summary>
        [Authorize]
        [HttpGet]
        public async Task<ActionResult<object>> GetNotifications(
            [FromQuery] int? limit = null,
            [FromQuery] int? beforeId = null
        )
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var page = await _notificationService.GetPageAsync(login, limit, beforeId);

                return Ok(
                    new
                    {
                        unreadCount = page.UnreadCount,
                        notifications = page
                            .Entries.Select(entry => new
                            {
                                id = entry.Notification.Id,
                                type = entry.Notification.Type,
                                actorLogin = entry.Notification.ActorId,
                                actorAvatarUrl = entry.ActorAvatarUrl,
                                // What the row means *now*: null for a kind that is not about a friendship, and one of
                                // none/requested/incoming/friends for the two that are. The panel acts on this rather
                                // than on the row's age, so a request answered elsewhere offers no button.
                                friendshipState = entry.FriendshipState,
                                subjectId = entry.Notification.SubjectId,
                                createdAt = entry.Notification.CreatedAt,
                                readAt = entry.Notification.ReadAt,
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
        /// Marks one of the caller's notifications read, answering the badge's new number. An id that is not the
        /// caller's is a `404` — never a `403` — so someone else's inbox cannot be probed for what is in it.
        /// </summary>
        [Authorize]
        [HttpPost("{id}/read")]
        public async Task<ActionResult<object>> MarkRead(int id)
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var unreadCount = await _notificationService.MarkReadAsync(login, id);

                if (unreadCount is null)
                {
                    return NotFound(new { error = "No such notification." });
                }

                return Ok(new { unreadCount });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>Marks the caller's whole inbox read — the panel's *Mark all read*.</summary>
        [Authorize]
        [HttpPost("read")]
        public async Task<ActionResult<object>> MarkAllRead()
        {
            try
            {
                var login = GetCurrentLogin();
                if (string.IsNullOrEmpty(login))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

                var result = await _notificationService.MarkAllReadAsync(login);

                return Ok(new { unreadCount = result.UnreadCount, markedRead = result.MarkedRead });
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
    }
}

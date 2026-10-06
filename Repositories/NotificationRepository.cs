using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    /// <summary>
    /// A player's inbox: the rows the bell counts and the panel lists
    /// (`plans/PLAN-022-notifications-and-friends/plan.md` §3.1). Deliberately generic — nothing here knows what a
    /// `friend_request` is, which is what lets a new kind arrive without a schema change.
    /// </summary>
    public interface INotificationRepository
    {
        /// <summary>
        /// Writes one notification. Uniqueness is <c>NotificationService</c>'s job (it asks
        /// <see cref="FindUnreadAsync"/> first), not the schema's — the double-write rule is easier to state in code
        /// than in a filtered index a portable provider would not honour.
        /// </summary>
        Task<Notification> CreateAsync(
            string recipientId,
            string type,
            string actorId,
            int? subjectId,
            string? payload = null
        );

        /// <summary>
        /// The one row still unread for this recipient, kind, actor and subject — the lookup behind "asking twice does
        /// not queue twice" (§3.2). Null when there is nothing outstanding.
        /// </summary>
        Task<Notification?> FindUnreadAsync(string recipientId, string type, string actorId, int? subjectId);

        /// <summary>
        /// One page of a recipient's rows, newest first. <paramref name="beforeId"/> is the cursor: the page holds
        /// rows written before the one the caller already has, so pages cannot repeat or skip.
        /// </summary>
        Task<List<Notification>> GetPageAsync(string recipientId, int limit, int? beforeId);

        /// <summary>How many of the recipient's rows are still unread — the badge's number.</summary>
        Task<int> CountUnreadAsync(string recipientId);

        /// <summary>
        /// One row, but only when it belongs to that recipient. Another player's id therefore reads as absent rather
        /// than as forbidden, so an id cannot be probed for someone else's inbox (§3.4).
        /// </summary>
        Task<Notification?> FindForRecipientAsync(int id, string recipientId);

        /// <summary>Marks these rows read for this recipient, returning how many were actually still unread.</summary>
        Task<int> MarkReadAsync(IReadOnlyCollection<int> ids, string recipientId);

        /// <summary>Marks every unread row read for this recipient, returning how many changed.</summary>
        Task<int> MarkAllReadAsync(string recipientId);
    }

    public class NotificationRepository(TripleTriadContext context) : INotificationRepository
    {
        private readonly TripleTriadContext _context = context;

        public async Task<Notification> CreateAsync(
            string recipientId,
            string type,
            string actorId,
            int? subjectId,
            string? payload = null
        )
        {
            var notification = new Notification
            {
                RecipientId = recipientId,
                Type = type,
                ActorId = actorId,
                SubjectId = subjectId,
                Payload = payload,
                CreatedAt = DateTime.UtcNow,
            };

            _context.Notifications.Add(notification);
            await _context.SaveChangesAsync();

            return notification;
        }

        public async Task<Notification?> FindUnreadAsync(
            string recipientId,
            string type,
            string actorId,
            int? subjectId
        )
        {
            return await _context.Notifications.FirstOrDefaultAsync(notification =>
                notification.RecipientId == recipientId
                && notification.Type == type
                && notification.ActorId == actorId
                && notification.SubjectId == subjectId
                && notification.ReadAt == null
            );
        }

        public async Task<List<Notification>> GetPageAsync(string recipientId, int limit, int? beforeId)
        {
            var query = _context.Notifications.Where(notification =>
                notification.RecipientId == recipientId
            );

            if (beforeId is { } cursor)
            {
                query = query.Where(notification => notification.Id < cursor);
            }

            // Newest first, with the id breaking a tie: two rows written in the same tick still come back in one
            // stable order, so the cursor above can neither repeat nor skip one of them.
            return await query
                .OrderByDescending(notification => notification.CreatedAt)
                .ThenByDescending(notification => notification.Id)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<int> CountUnreadAsync(string recipientId)
        {
            return await _context.Notifications.CountAsync(notification =>
                notification.RecipientId == recipientId && notification.ReadAt == null
            );
        }

        public async Task<Notification?> FindForRecipientAsync(int id, string recipientId)
        {
            return await _context.Notifications.FirstOrDefaultAsync(notification =>
                notification.Id == id && notification.RecipientId == recipientId
            );
        }

        public async Task<int> MarkReadAsync(IReadOnlyCollection<int> ids, string recipientId)
        {
            if (ids.Count == 0)
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            var targets = ids.ToList();

            if (_context.Database.IsInMemory())
            {
                // The in-memory provider has no bulk update, so the tracked read-modify-write below is the only option
                // there — acceptable because every row lives inside one process, the same branch PlayerPackRepository
                // takes when it consumes a pack.
                var rows = await _context
                    .Notifications.Where(notification =>
                        targets.Contains(notification.Id)
                        && notification.RecipientId == recipientId
                        && notification.ReadAt == null
                    )
                    .ToListAsync();

                foreach (var row in rows)
                {
                    row.ReadAt = now;
                }

                await _context.SaveChangesAsync();

                return rows.Count;
            }

            // One statement, and only over rows that are genuinely unread, so calling this twice reports zero the
            // second time rather than re-stamping rows that were already seen.
            return await _context
                .Notifications.Where(notification =>
                    targets.Contains(notification.Id)
                    && notification.RecipientId == recipientId
                    && notification.ReadAt == null
                )
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(notification => notification.ReadAt, now)
                );
        }

        public async Task<int> MarkAllReadAsync(string recipientId)
        {
            var now = DateTime.UtcNow;

            if (_context.Database.IsInMemory())
            {
                var rows = await _context
                    .Notifications.Where(notification =>
                        notification.RecipientId == recipientId && notification.ReadAt == null
                    )
                    .ToListAsync();

                foreach (var row in rows)
                {
                    row.ReadAt = now;
                }

                await _context.SaveChangesAsync();

                return rows.Count;
            }

            return await _context
                .Notifications.Where(notification =>
                    notification.RecipientId == recipientId && notification.ReadAt == null
                )
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(notification => notification.ReadAt, now)
                );
        }
    }
}

using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Models;

namespace TripleTriadApi.Repositories
{
    /// <summary>
    /// The friendships a player has or has been offered — the relationship half of
    /// `plans/PLAN-022-notifications-and-friends/plan.md` (the inbox half is <see cref="INotificationRepository"/>).
    ///
    /// Every lookup takes the pair in whatever order the caller has it and canonicalises it here, so no caller has to
    /// remember that <see cref="Friendship.PlayerA"/> is the smaller login (§3.1).
    /// </summary>
    public interface IFriendshipRepository
    {
        /// <summary>The pair's single row, whichever order the two logins arrive in — null when they have none.</summary>
        Task<Friendship?> FindPairAsync(string first, string second);

        /// <summary>Creates the pending request <paramref name="requester"/> has made of <paramref name="target"/>.</summary>
        Task<Friendship> CreateRequestAsync(string requester, string target);

        /// <summary>Writes an acceptance onto a pending row and returns it.</summary>
        Task<Friendship> AcceptAsync(Friendship friendship);

        /// <summary>
        /// Deletes the row. A decline, a cancellation and an unfriending are the same write — the caller has already
        /// decided which one it is.
        /// </summary>
        Task DeleteAsync(Friendship friendship);

        /// <summary>How many requests the player has sent that nobody has answered yet (the cap in §3.2).</summary>
        Task<int> CountPendingSentByAsync(string sender);

        /// <summary>
        /// These friendships by id, for the inbox — a notification carries the friendship's id, so a page of them is
        /// stamped with their current state in one query instead of one per row.
        /// </summary>
        Task<List<Friendship>> FindByIdsAsync(IReadOnlyCollection<int> ids);
    }

    public class FriendshipRepository(TripleTriadContext context) : IFriendshipRepository
    {
        private readonly TripleTriadContext _context = context;

        public async Task<Friendship?> FindPairAsync(string first, string second)
        {
            var (a, b) = CanonicalOrder(first, second);

            return await _context.Friendships.FirstOrDefaultAsync(friendship =>
                friendship.PlayerA == a && friendship.PlayerB == b
            );
        }

        public async Task<Friendship> CreateRequestAsync(string requester, string target)
        {
            var (a, b) = CanonicalOrder(requester, target);
            var friendship = new Friendship
            {
                PlayerA = a,
                PlayerB = b,
                Status = FriendshipStatus.Pending,
                RequestedBy = requester,
                CreatedAt = DateTime.UtcNow,
            };

            _context.Friendships.Add(friendship);
            await _context.SaveChangesAsync();

            return friendship;
        }

        public async Task<Friendship> AcceptAsync(Friendship friendship)
        {
            friendship.Status = FriendshipStatus.Accepted;
            friendship.RespondedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return friendship;
        }

        public async Task DeleteAsync(Friendship friendship)
        {
            _context.Friendships.Remove(friendship);
            await _context.SaveChangesAsync();
        }

        public async Task<int> CountPendingSentByAsync(string sender)
        {
            return await _context
                .Friendships.Where(friendship =>
                    friendship.RequestedBy == sender && friendship.Status == FriendshipStatus.Pending
                )
                .CountAsync();
        }

        public async Task<List<Friendship>> FindByIdsAsync(IReadOnlyCollection<int> ids)
        {
            if (ids.Count == 0)
            {
                return [];
            }

            var targets = ids.ToList();

            return await _context
                .Friendships.Where(friendship => targets.Contains(friendship.Id))
                .ToListAsync();
        }

        /// <summary>
        /// The pair in the one order the table stores them in: the smaller login first, compared **ordinally** so the
        /// result never depends on a collation (the database's or the runtime's) and the same pair always maps to the
        /// same row — which is what makes the unique index mean "one friendship" rather than "one direction".
        /// </summary>
        internal static (string A, string B) CanonicalOrder(string first, string second) =>
            string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);
    }
}

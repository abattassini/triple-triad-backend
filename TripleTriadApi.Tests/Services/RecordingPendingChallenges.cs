using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// A stand-in for the challenge lifecycle writes: it records which expiry rule asked, instead of touching a
    /// database. That is what lets matchmaking's test stay about matchmaking while still proving that starting a game
    /// gives up an outstanding invitation (plans/PLAN-027-friend-challenge/plan.md §3.5).
    /// </summary>
    public class RecordingPendingChallenges : IPendingChallenges
    {
        private readonly List<Expiry> _expiries = [];

        /// <summary>Every expiry asked for, oldest first.</summary>
        public IReadOnlyList<Expiry> Expiries => _expiries;

        public Task<int> ExpireInvolvingAsync(string login)
        {
            _expiries.Add(new Expiry("involving", login));
            return Task.FromResult(0);
        }

        public Task<int> ExpireTimedOutAsync(DateTime now)
        {
            _expiries.Add(new Expiry("timed-out", string.Empty));
            return Task.FromResult(0);
        }

        /// <summary>One expiry: which rule (created-by / involving / timed-out) and whose login it named.</summary>
        public sealed record Expiry(string Kind, string Login);
    }
}

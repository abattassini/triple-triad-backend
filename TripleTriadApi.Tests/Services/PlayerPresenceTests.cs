using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The presence registry (plans/PLAN-023-social-friends-list/plan.md §3.1): it counts connections, so a player with
    /// two tabs is online until the last one goes, and an id nobody recorded is nothing to do rather than an error.
    /// </summary>
    public class PlayerPresenceTests
    {
        private const string Me = "argel";
        private const string Rival = "rival";

        [Fact]
        public void IsOnline_ForSomeoneNobodyConnected_IsFalse()
        {
            var presence = new ConnectionPresence();

            Assert.False(presence.IsOnline(Me));
            Assert.Equal(0, presence.ConnectionCount(Me));
        }

        [Fact]
        public void IsOnline_WhileAConnectionHoldsTheLogin_IsTrue()
        {
            var presence = new ConnectionPresence();

            presence.AddConnection("c1", Me);

            Assert.True(presence.IsOnline(Me));
            Assert.False(presence.IsOnline(Rival));
        }

        [Fact]
        public void IsOnline_WithTwoConnections_StaysTrueUntilBothAreGone()
        {
            var presence = new ConnectionPresence();

            presence.AddConnection("c1", Me);
            presence.AddConnection("c2", Me);
            Assert.Equal(2, presence.ConnectionCount(Me));

            Assert.Equal(Me, presence.RemoveConnection("c1"));
            Assert.True(presence.IsOnline(Me));

            Assert.Equal(Me, presence.RemoveConnection("c2"));
            Assert.False(presence.IsOnline(Me));
            Assert.Equal(0, presence.ConnectionCount(Me));
        }

        [Fact]
        public void RemoveConnection_ForAnIdNobodyRecorded_IsNull()
        {
            var presence = new ConnectionPresence();

            Assert.Null(presence.RemoveConnection("never-seen"));
            Assert.False(presence.IsOnline(Me));
        }

        [Fact]
        public void RemoveConnection_ForOnePlayer_LeavesAnotherAlone()
        {
            var presence = new ConnectionPresence();
            presence.AddConnection("c1", Me);
            presence.AddConnection("c2", Rival);

            presence.RemoveConnection("c1");

            Assert.False(presence.IsOnline(Me));
            Assert.True(presence.IsOnline(Rival));
        }

        [Fact]
        public void AddConnection_ForTheSameIdTwice_CountsItOnce()
        {
            var presence = new ConnectionPresence();

            presence.AddConnection("c1", Me);
            presence.AddConnection("c1", Me);

            Assert.Equal(1, presence.ConnectionCount(Me));
        }

        [Fact]
        public void OnlineLogins_IsEmptyWhenNobodyIsConnected()
        {
            var presence = new ConnectionPresence();

            Assert.Empty(presence.OnlineLogins());
        }

        [Fact]
        public void OnlineLogins_NamesEachOnlinePlayerOnceWhateverTheConnectionCount()
        {
            var presence = new ConnectionPresence();
            presence.AddConnection("c1", Me);
            presence.AddConnection("c2", Me);
            presence.AddConnection("c3", Rival);

            var online = presence.OnlineLogins();

            Assert.Equal(2, online.Count);
            Assert.Contains(Me, online);
            Assert.Contains(Rival, online);
        }

        [Fact]
        public void OnlineLogins_DropsAPlayerWhenTheirLastConnectionGoes()
        {
            var presence = new ConnectionPresence();
            presence.AddConnection("c1", Me);
            presence.AddConnection("c2", Me);

            Assert.Equal(Me, presence.RemoveConnection("c1"));
            Assert.Contains(Me, presence.OnlineLogins());

            Assert.Equal(Me, presence.RemoveConnection("c2"));
            Assert.DoesNotContain(Me, presence.OnlineLogins());
        }
    }
}

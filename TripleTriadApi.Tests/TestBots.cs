namespace TripleTriadApi.Tests
{
    /// <summary>
    /// The login a test seeds as a bot — the stand-in for the machine player the bot sweep drives
    /// (plans/PLAN-025-bots/plan.md). A bot is an ordinary player row now, so a test that wants the turn service to
    /// move must seed a row with this login flagged <c>IsBot</c> **and** load a <c>BotRegistry</c> holding it — the
    /// sweep plays whatever the registry calls a bot, not a sentinel string.
    /// </summary>
    internal static class TestBots
    {
        public const string Login = "sparring-bot";
    }
}

using System.Runtime.CompilerServices;

namespace TripleTriadApi.Tests
{
    /// <summary>
    /// Gives the test process the one piece of configuration <see cref="Services.TokenService"/> refuses to work
    /// without.
    ///
    /// <c>TokenService</c> reads its signing secret while the type is initialising and throws when it is absent —
    /// deliberately, since a server that issued unsigned tokens would be far worse than one that refuses to start.
    /// The side effect is that any test which actually <em>issues</em> a token would depend on the developer's machine
    /// environment, passing here and failing on a clean checkout. Setting the value once for the whole assembly
    /// removes that: it is a throwaway test value, and nothing outside this process ever sees a token signed with it.
    ///
    /// This is why the older tests could get away without it — they only ever constructed a <c>TokenService</c>, and
    /// a type with no explicit static constructor is not required to have run its field initialisers by then.
    /// </summary>
    internal static class TestEnvironment
    {
        [ModuleInitializer]
        internal static void Initialise()
        {
            Environment.SetEnvironmentVariable(
                "Supabase__JwtSecret",
                "triple-triad-test-signing-secret-not-a-real-one"
            );
        }
    }
}

namespace TripleTriadApi.Services
{
    /// <summary>
    /// The randomness the card shop needs, behind a seam so a test can script an exact sequence of draws.
    /// Production uses <see cref="SystemRandomSource"/>.
    /// </summary>
    public interface IRandomSource
    {
        /// <summary>A random integer in <c>[0, exclusiveMax)</c>.</summary>
        int Next(int exclusiveMax);
    }

    /// <summary>The production implementation, backed by <see cref="Random.Shared"/>.</summary>
    public sealed class SystemRandomSource : IRandomSource
    {
        public int Next(int exclusiveMax) => Random.Shared.Next(exclusiveMax);
    }
}

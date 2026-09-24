namespace TripleTriadApi.Services
{
    /// <summary>
    /// Where the frontend lives, so a link the API emails can point back at it without the URL being hardcoded in the
    /// service that builds it.
    ///
    /// This matters more than it looks: the app is served from a GitHub Pages **sub-path** (`/triple-triad/`, see
    /// <c>vite.config.ts</c>), it is a different origin from the API, and the dev value is a Vite port. Baking that
    /// into a string literal would mean the reset link is wrong in exactly one environment and nobody notices until a
    /// player cannot recover their account.
    /// </summary>
    public class AppOptions
    {
        /// <summary>The configuration section these values bind from.</summary>
        public const string SectionName = "App";

        /// <summary>
        /// The frontend's base URL with no trailing slash, e.g. <c>https://abattassini.github.io/triple-triad</c>.
        /// </summary>
        public string FrontendBaseUrl { get; set; } = "http://localhost:5173/triple-triad";
    }
}

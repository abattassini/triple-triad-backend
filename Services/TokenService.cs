using System.Security.Cryptography;
using System.Text;
using TripleTriadApi.Models;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Issues HS256 JSON Web Tokens for signed-in players.
    /// The token subject ("sub") is the player's login, which is also the
    /// playerId used across the game layer, so existing match data stays valid.
    /// </summary>
    public class TokenService
    {
        private static readonly string Secret = LoadSecret();

        private static string LoadSecret()
        {
            var secret = Environment.GetEnvironmentVariable("Supabase__JwtSecret");
            if (string.IsNullOrEmpty(secret))
            {
                throw new InvalidOperationException(
                    "Supabase__JwtSecret environment variable is required to issue JWTs."
                );
            }

            return secret;
        }

        public string IssueToken(Player player, int daysToExpire = 7)
        {
            var nowEpochSeconds = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10000000;
            var header = Base64UrlEncode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
            var payloadJson =
                "{\"sub\":\"" + JsonEscape(player.Login)
                + "\",\"login\":\"" + JsonEscape(player.Login)
                + "\",\"email\":\"" + JsonEscape(player.Email)
                + "\",\"iat\":" + nowEpochSeconds
                + ",\"exp\":" + (nowEpochSeconds + (daysToExpire * 86400))
                + "}";
            var payload = Base64UrlEncode(payloadJson);
            var signingInput = header + "." + payload;
            var signature = Base64UrlEncode(HmacSha256(signingInput));

            return signingInput + "." + signature;
        }

        private static byte[] HmacSha256(string input)
        {
            return CryptographicOperations.HmacData(
                HashAlgorithmName.SHA256,
                Encoding.UTF8.GetBytes(Secret),
                Encoding.UTF8.GetBytes(input)
            );
        }

        // Standard Base64 with padding, then converted to the URL-safe JWT alphabet:
        // '+' -> '-', '/' -> '_', and trailing '=' padding is removed.
        private static string Base64UrlEncode(string value)
        {
            return Base64UrlEncode(Encoding.UTF8.GetBytes(value));
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert
                .ToBase64String(data)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
        }

        private static string JsonEscape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
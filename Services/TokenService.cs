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
        private static readonly string Alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        /// <summary>
        /// A token that checked out: who it was issued to, and which generation of that player's credentials it
        /// belongs to.
        ///
        /// <see cref="SessionVersion"/> is compared against <see cref="Player.SessionVersion"/> by whoever is
        /// authenticating. A mismatch means the player has changed their password since this token was issued, and the
        /// token must be refused — see <see cref="Player.SessionVersion"/> for why that matters.
        /// </summary>
        public sealed record TokenPayload(string Login, int SessionVersion);

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
                // The credential generation this token belongs to. Bumped by a password reset, which is how a reset
                // retires every session the player already had. See Player.SessionVersion.
                //
                // The name is deliberately not "ver": that short form IS in the JWT handler's default inbound
                // claim-type map, so on the way back in it would silently arrive under ClaimTypes.Version instead and
                // the check in Program.cs would read nothing. An unmapped name cannot be rewritten.
                + "\",\"session_version\":" + player.SessionVersion
                + ",\"iat\":" + nowEpochSeconds
                + ",\"exp\":" + (nowEpochSeconds + (daysToExpire * 86400))
                + "}";
            var payload = Base64UrlEncode(payloadJson);
            var signingInput = header + "." + payload;
            var signature = Base64UrlEncode(HmacSha256(signingInput));

            return signingInput + "." + signature;
        }

        // Validates an HS256 JWT and returns its payload, or null if the token is
        // malformed, expired, or has an invalid signature.
        // This is used to authenticate SignalR hub calls, where the connection
        // middleware does not populate the hub's user context for WebSockets, and it
        // is the only place the session version is read back out of a token.
        public TokenPayload? Validate(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            // Verify the signature over the <header>.<payload> portion.
            var signingInput = parts[0] + "." + parts[1];
            var expectedSignature = Base64UrlEncode(HmacSha256(signingInput));
            if (expectedSignature != parts[2])
            {
                return null;
            }

            var payloadJson = Base64UrlDecodeToUtf8(parts[1]);
            if (string.IsNullOrEmpty(payloadJson))
            {
                return null;
            }

            // Reject expired tokens.
            var expString = ExtractJsonNumber(payloadJson, "\"exp\":");
            if (!string.IsNullOrEmpty(expString))
            {
                var nowSeconds = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10000000;
                if (nowSeconds >= Convert.ToInt64(expString))
                {
                    return null;
                }
            }

            var subject = ExtractJsonString(payloadJson, "\"sub\":");
            if (string.IsNullOrEmpty(subject))
            {
                return null;
            }

            // A token issued before password recovery existed carries no session_version. It belongs to generation 0,
            // which is what every pre-existing player row holds, so those sessions keep working until the player's
            // first reset — reading a missing claim as 0 is what makes that true.
            var versionString = ExtractJsonNumber(payloadJson, "\"session_version\":");
            var sessionVersion = string.IsNullOrEmpty(versionString) ? 0 : Convert.ToInt32(versionString);

            return new TokenPayload(subject, sessionVersion);
        }

        // The login from a valid token, or null.
        //
        // Kept as the narrow, version-blind accessor for callers that only need "who is this?" and have no player row
        // to compare against. Anything authenticating a request should use Validate and check the session version
        // instead; see Player.SessionVersion.
        public string? ValidateToken(string token) => Validate(token)?.Login;

        private static string Base64UrlDecodeToUtf8(string value)
        {
            if (value.Length % 4 == 1)
            {
                return ""; // invalid base64 length
            }

            var output = new Char[value.Length];
            var outLength = 0;
            var buffer = 0;
            var bits = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch == '=')
                {
                    break;
                }

                var index = Alphabet.IndexOf(ch);
                if (index < 0)
                {
                    continue; // skip any unexpected character
                }

                buffer = (buffer << 6) | index;
                bits += 6;
                if (bits >= 8)
                {
                    bits -= 8;
                    output[outLength++] = (Char)((buffer >> bits) & 0xFF);
                }
            }

            return new String(output, 0, outLength);
        }

        private static string ExtractJsonString(string json, string key)
        {
            var start = json.IndexOf(key);
            if (start < 0)
            {
                return "";
            }

            start += key.Length;
            if (start >= json.Length || json[start] != '"')
            {
                return "";
            }

            var end = json.IndexOf('"', start + 1);
            if (end < 0)
            {
                return "";
            }

            return json.Substring(start + 1, end - start - 1);
        }

        private static string ExtractJsonNumber(string json, string key)
        {
            var start = json.IndexOf(key);
            if (start < 0)
            {
                return "";
            }

            start += key.Length;
            var end = start;
            while (end < json.Length && IsDigit(json[end]))
            {
                end++;
            }

            return end == start ? "" : json.Substring(start, end - start);
        }

        private static bool IsDigit(char ch)
        {
            return ch >= '0' && ch <= '9';
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
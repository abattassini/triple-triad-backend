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

        // Validates an HS256 JWT and returns its subject (player login), or null
        // if the token is malformed, expired, or has an invalid signature.
        // This is used to authenticate SignalR hub calls, where the connection
        // middleware does not populate the hub's user context for WebSockets.
        public string? ValidateToken(string token)
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
            return string.IsNullOrEmpty(subject) ? null : subject;
        }

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
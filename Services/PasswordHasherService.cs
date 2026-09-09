using BC = BCrypt.Net.BCrypt;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Thin wrapper around BCrypt for password hashing and verification.
    /// BCrypt embeds a per-password salt in the generated hash.
    /// </summary>
    public class PasswordHasherService
    {
        public string Hash(string plainPassword)
        {
            return BC.HashPassword(plainPassword);
        }

        public bool Verify(string plainPassword, string passwordHash)
        {
            return BC.Verify(plainPassword, passwordHash);
        }
    }
}
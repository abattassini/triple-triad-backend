using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>What became of a recovery *request*. The controller answers identically for all of these (plan §2).</summary>
    public enum RecoveryRequestOutcome
    {
        Sent,
        NoSuchAccount,
        RateLimited,
        SendFailed,
    }

    /// <summary>The result of spending a recovery code. <see cref="Error"/> is safe to show a player.</summary>
    public sealed record RecoveryResetResult(bool Succeeded, string? Error)
    {
        public static RecoveryResetResult Ok() => new(true, null);

        public static RecoveryResetResult Fail(string error) => new(false, error);
    }

    /// <summary>
    /// The password-recovery rules, in one place: who may ask for a code, what a code looks like, how it is stored,
    /// and what spending it does to the account.
    ///
    /// <para><strong>Tokens are looked up by hash, and that shapes the whole design.</strong> The request carries only
    /// a code — no email, no login — because the code <em>is</em> the proof. A wrong code therefore matches no row at
    /// all, so there is deliberately <strong>no per-token wrong-guess counter</strong>: there would be no row to hang
    /// an attempt on. Brute force is controlled instead by the code's entropy (10 characters from a 32-symbol
    /// alphabet, ~50 bits), its 15-minute life, its single use, and the per-address throttle on the reset endpoint
    /// (<see cref="PasswordResetOptions.ResetAttemptsPerIpPerHour"/>). An earlier draft of this plan specified a
    /// "5 attempts then destroy the token" rule; that counter could never fire, because a code is only ever found
    /// once it is already correct. It was removed rather than shipped as security theatre.</para>
    ///
    /// <para><strong>Nothing here reveals whether an account exists.</strong> An unknown address produces the same
    /// outcome as a real one, and so does the CPU sentinel — see <see cref="RequestResetAsync"/>.</para>
    ///
    /// <para><strong>The code is stored hashed, the password is stored hashed, and neither is ever emailed.</strong>
    /// See <see cref="PasswordResetToken.TokenHash"/> and <see cref="Player.PasswordHash"/>.</para>
    /// </summary>
    public class PasswordResetService(
        IPlayerRepository playerRepository,
        IPasswordResetRepository resetRepository,
        PasswordHasherService passwordHasher,
        IEmailSender emailSender,
        IOptions<PasswordResetOptions> resetOptions,
        IOptions<AppOptions> appOptions,
        ILogger<PasswordResetService> logger
    )
    {
        /// <summary>
        /// The alphabet a code is drawn from: the digits and letters that survive being read aloud, copied off a
        /// phone screen and retyped. It omits <c>O</c>/<c>0</c> and <c>I</c>/<c>1</c>, which is why it is 32 symbols
        /// rather than 36 — and 32 being a power of two means <c>RandomNumberGenerator.GetInt32(32)</c> is unbiased
        /// with no modulo arithmetic.
        /// </summary>
        private const string CodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

        /// <summary>
        /// Code length. Ten symbols of a 32-symbol alphabet is 50 bits of entropy — far above NIST's floor for an
        /// out-of-band secret ("at least six decimal digits … or equivalent") while staying short enough to type.
        /// A 256-bit URL-only token would be stronger but could not be read over the phone or retyped.
        /// </summary>
        private const int CodeLength = 10;

        /// <summary>Shown when a code matches nothing. Also shown for a code that is structurally impossible.</summary>
        private const string InvalidCodeMessage = "That code is not valid. Request a new one.";

        /// <summary>
        /// Shown when a code matched but is spent or past its time-to-live. Distinguishing this from
        /// <see cref="InvalidCodeMessage"/> leaks nothing: whoever holds a real-but-stale code already knew it was
        /// real, and everyone else gets the same answer as for a wrong code.
        /// </summary>
        private const string ExpiredCodeMessage =
            "That code has expired or has already been used. Request a new one.";

        private readonly PasswordResetOptions _options = resetOptions.Value;
        private readonly AppOptions _appOptions = appOptions.Value;

        /// <summary>
        /// Issues a recovery code for the account owning <paramref name="email"/>, subject to the layered limits, and
        /// mails it.
        ///
        /// Every refusal below is deliberately indistinguishable to the caller — unknown address, rate-limited and CPU
        /// sentinel all return something the controller turns into the same response — so the endpoint cannot be used
        /// to ask "does this email have an account here?".
        /// </summary>
        public async Task<RecoveryRequestOutcome> RequestResetAsync(
            string email,
            string? requestedFromIp,
            DateTime now,
            CancellationToken cancellationToken = default
        )
        {
            var player = await playerRepository.FindByEmailAsync(email.Trim());

            // The CPU opponent is a real row with a real login, and no human should be able to take it over.
            if (player is null || player.Login == CpuOpponent.Login)
            {
                return RecoveryRequestOutcome.NoSuchAccount;
            }

            var refusal = await RefusalReasonAsync(player.Login, requestedFromIp, now);

            if (refusal is not null)
            {
                return refusal.Value;
            }

            // One live code per player: asking again invalidates whatever was mailed before, so a request cannot be
            // used to keep several codes alive at once.
            await resetRepository.RetireOutstandingAsync(player.Login, now);

            var code = GenerateCode();

            await resetRepository.CreateAsync(
                new PasswordResetToken
                {
                    PlayerLogin = player.Login,
                    TokenHash = HashCode(code),
                    CreatedAt = now,
                    ExpiresAt = now.AddMinutes(_options.CodeTtlMinutes),
                    RequestedFromIp = requestedFromIp,
                }
            );

            // Opportunistic housekeeping rather than a scheduled sweep: issuing is rare, and the repository holds rows
            // for two days precisely so this can never delete part of the rate-limit ledger.
            await resetRepository.PurgeStaleAsync(now);

            try
            {
                await emailSender.SendAsync(BuildMessage(player, code), cancellationToken);

                return RecoveryRequestOutcome.Sent;
            }
            catch (Exception ex)
            {
                // A delivery failure must NOT become a different HTTP response, or a broken mailbox would leak which
                // addresses exist. It is logged instead — that log line is the operator's signal recovery is down.
                logger.LogError(ex, "Password recovery email could not be sent.");

                return RecoveryRequestOutcome.SendFailed;
            }
        }

        /// <summary>
        /// Which limit, if any, refuses this request — the layered design from plan §6, checked cheapest-first.
        ///
        /// The per-account caps come first because they protect the <em>player's inbox</em>, which is the thing this
        /// feature must not abuse. The per-address cap is next. The global cap is last and is only a backstop: it is a
        /// shared resource any single actor can drain, so it must never be the first thing to trip, and tripping it is
        /// logged as an error because it means either abuse or that the sending mailbox has been outgrown.
        /// </summary>
        private async Task<RecoveryRequestOutcome?> RefusalReasonAsync(
            string playerLogin,
            string? requestedFromIp,
            DateTime now
        )
        {
            var perAccountHour = await resetRepository.CountIssuedSinceAsync(
                now.AddHours(-1),
                playerLogin: playerLogin
            );

            if (perAccountHour >= _options.PerAccountPerHour)
            {
                logger.LogWarning("Password recovery request refused: per-account hourly limit reached.");

                return RecoveryRequestOutcome.RateLimited;
            }

            var perAccountDay = await resetRepository.CountIssuedSinceAsync(
                now.AddDays(-1),
                playerLogin: playerLogin
            );

            if (perAccountDay >= _options.PerAccountPerDay)
            {
                logger.LogWarning("Password recovery request refused: per-account daily limit reached.");

                return RecoveryRequestOutcome.RateLimited;
            }

            // Only meaningful when the source is actually known. Behind a proxy this can be the proxy's address unless
            // ForwardedHeaders is configured, in which case it behaves more like a global cap than a per-client one.
            if (!string.IsNullOrWhiteSpace(requestedFromIp))
            {
                var perAddress = await resetRepository.CountIssuedSinceAsync(
                    now.AddHours(-1),
                    requestedFromIp: requestedFromIp
                );

                if (perAddress >= _options.PerIpPerHour)
                {
                    logger.LogWarning("Password recovery request refused: per-address hourly limit reached.");

                    return RecoveryRequestOutcome.RateLimited;
                }
            }

            var globalToday = await resetRepository.CountIssuedSinceAsync(now.Date);

            if (globalToday >= _options.GlobalPerDay)
            {
                logger.LogError(
                    "Password recovery request refused: the global daily cap of {Cap} is exhausted. This is either "
                        + "abuse or the sending mailbox has been outgrown.",
                    _options.GlobalPerDay
                );

                return RecoveryRequestOutcome.RateLimited;
            }

            return null;
        }

        /// <summary>
        /// Spends a recovery code and sets the new password.
        ///
        /// The caller has already put the new password through
        /// <see cref="Validators.ResetPasswordRequestValidator"/>, so this assumes it satisfies the policy; the
        /// plaintext is hashed and then dropped, never stored.
        ///
        /// Order matters: the code is spent <em>before</em> the password is written. If the write then failed, the
        /// player has to request a new code — annoying. The other order would leave a still-live code pointing at an
        /// already-changed password, which is a hole. Failing closed is the right way round.
        /// </summary>
        public async Task<RecoveryResetResult> ResetPasswordAsync(string code, string newPassword, DateTime now)
        {
            var normalised = NormaliseCode(code);

            if (normalised.Length != CodeLength)
            {
                return RecoveryResetResult.Fail(InvalidCodeMessage);
            }

            var token = await resetRepository.FindByHashAsync(HashCode(normalised));

            if (token is null)
            {
                return RecoveryResetResult.Fail(InvalidCodeMessage);
            }

            if (!token.IsUsableAt(now))
            {
                return RecoveryResetResult.Fail(ExpiredCodeMessage);
            }

            var player = await playerRepository.FindByLoginAsync(token.PlayerLogin);

            if (player is null)
            {
                return RecoveryResetResult.Fail(InvalidCodeMessage);
            }

            if (!await resetRepository.ConsumeAsync(token.Id, now))
            {
                // Spent between the lookup and here — a concurrent submission won the race.
                return RecoveryResetResult.Fail(ExpiredCodeMessage);
            }

            player.PasswordHash = passwordHasher.Hash(newPassword);

            // Retires every token already issued to this player (see Player.SessionVersion). This is what actually
            // evicts an attacker who had already opened the account.
            player.SessionVersion++;

            await playerRepository.UpdateAsync(player);

            logger.LogInformation(
                "Password reset completed; the player's session version is now {SessionVersion}.",
                player.SessionVersion
            );

            return RecoveryResetResult.Ok();
        }

        /// <summary>
        /// A fresh code. <see cref="RandomNumberGenerator.GetInt32(int)"/> is the cryptographic generator — never
        /// <c>Random</c>, which is seedable and predictable, and never <c>Guid.NewGuid()</c>, which looks random but
        /// is not a secret.
        /// </summary>
        private static string GenerateCode()
        {
            var chars = new char[CodeLength];

            for (var i = 0; i < CodeLength; i++)
            {
                chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            }

            return new string(chars);
        }

        /// <summary>The code as a human should see it: grouped in fours, which is how people read and retype them.</summary>
        private static string FormatCode(string code)
        {
            var builder = new StringBuilder(code.Length + (code.Length / 4));

            for (var i = 0; i < code.Length; i++)
            {
                if (i > 0 && i % 4 == 0)
                {
                    builder.Append('-');
                }

                builder.Append(code[i]);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Puts a typed code into the one form that is ever hashed, so the dashes we print, the case the player typed
        /// and whitespace from a copy-paste all still match. Without it, recoveries would fail on formatting rather
        /// than on the code being wrong — and the player would have no way to tell which.
        /// </summary>
        private static string NormaliseCode(string code)
        {
            var builder = new StringBuilder(code.Length);

            foreach (var ch in code.ToUpperInvariant())
            {
                if (ch == '-' || char.IsWhiteSpace(ch))
                {
                    continue;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        /// <summary>
        /// SHA-256, hex. A fast hash is right here — the input is high-entropy and short-lived, so there is nothing to
        /// slow down, and the lookup stays a single indexed equality test. <see cref="Player.PasswordHash"/> is BCrypt
        /// for the opposite reason.
        /// </summary>
        private static string HashCode(string normalisedCode) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalisedCode))).ToLowerInvariant();

        /// <summary>
        /// The mail itself: text only, short, naming the game and saying who asked for it. A recovery email is the
        /// most phished message in existence and this one arrives from a free-mail address, so it should read like
        /// something a person wrote rather than a template. See <see cref="IEmailSender"/>.
        ///
        /// It carries the code twice on purpose — as a link and as something to type. Mail clients mangle URLs often
        /// enough that link-only recovery strands real players, and the second form costs nothing: it is the same
        /// token.
        /// </summary>
        private EmailMessage BuildMessage(Player player, string code)
        {
            var link = $"{_appOptions.FrontendBaseUrl.TrimEnd('/')}/reset-password?token={code}";

            var body =
                $"Hi {player.Login},{Environment.NewLine}{Environment.NewLine}"
                + "Someone asked to reset the password for your Triple Triad account."
                + Environment.NewLine
                + Environment.NewLine
                + "Open this link to choose a new password:"
                + Environment.NewLine
                + link
                + Environment.NewLine
                + Environment.NewLine
                + "If the link does not work, enter this code on the reset page:"
                + Environment.NewLine
                + FormatCode(code)
                + Environment.NewLine
                + Environment.NewLine
                + $"The code stops working in {_options.CodeTtlMinutes} minutes and can only be used once."
                + Environment.NewLine
                + "If you did not ask for this, you can ignore this email - your password has not changed."
                + Environment.NewLine;

            return new EmailMessage(player.Email, "Reset your Triple Triad password", body);
        }
    }
}

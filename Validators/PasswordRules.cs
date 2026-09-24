using FluentValidation;

namespace TripleTriadApi.Validators
{
    /// <summary>
    /// The password policy, defined once and applied by every flow that sets a password.
    ///
    /// It lives outside <see cref="RegisterPlayerRequestValidator"/> so the recovery flow cannot become a back door
    /// that accepts something the registration flow would have refused — a reset endpoint with its own copy of the
    /// rules is exactly the kind of drift that ends with a weaker password on a recovered account than on a new one.
    ///
    /// The rules mirror the client-side ones in <c>AccountCreation.tsx</c>, so the two never disagree about what is
    /// acceptable; the backend re-validates everything regardless.
    /// </summary>
    public static class PasswordRules
    {
        public const int MinLength = 8;
        public const int MaxLength = 20;

        public const string LengthMessage = "Password must be between 8 and 20 characters.";
        public const string LetterMessage = "Password must contain at least one letter.";
        public const string NumberMessage = "Password must contain at least one number.";

        /// <summary>
        /// Applies the full policy to a password property. An extension on the rule builder rather than a method
        /// returning a validator, so a caller can chain more rules after it if a flow ever needs to.
        /// </summary>
        public static IRuleBuilderOptions<T, string> ApplyPasswordPolicy<T>(this IRuleBuilder<T, string> rule)
        {
            return rule
                .Length(MinLength, MaxLength)
                .WithMessage(LengthMessage)
                .Must(ContainsLetter)
                .WithMessage(LetterMessage)
                .Must(ContainsNumber)
                .WithMessage(NumberMessage);
        }

        /// <summary>
        /// Null-safe: a body with an explicit <c>"password": null</c> must produce the ordinary validation messages
        /// rather than a NullReferenceException on the way to a 500.
        /// </summary>
        public static bool ContainsLetter(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Null-safe; see <see cref="ContainsLetter"/>.</summary>
        public static bool ContainsNumber(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch >= '0' && ch <= '9')
                {
                    return true;
                }
            }

            return false;
        }
    }
}

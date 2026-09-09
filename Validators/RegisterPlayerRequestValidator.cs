using FluentValidation;
using TripleTriadApi.Controllers;

namespace TripleTriadApi.Validators
{
    /// <summary>
    /// FluentValidation rules for <see cref="RegisterPlayerRequest"/>.
    /// These mirror the client-side rules from the account creation form
    /// (see AccountCreation.tsx) so the backend re-validates everything.
    /// </summary>
    public class RegisterPlayerRequestValidator : AbstractValidator<RegisterPlayerRequest>
    {
        public RegisterPlayerRequestValidator()
        {
            RuleFor(x => x.Login)
                .Must(login => login.Trim().Length != 0)
                .WithMessage("Login is required.");

            RuleFor(x => x.Email)
                .Must(email => IsValidEmail(email.Trim()))
                .WithMessage("Please enter a valid email address.");

            RuleFor(x => x.Password)
                .Length(8, 20)
                .WithMessage("Password must be between 8 and 20 characters.")
                .Must(ContainsLetter)
                .WithMessage("Password must contain at least one letter.")
                .Must(ContainsNumber)
                .WithMessage("Password must contain at least one number.");
        }

        private static bool IsValidEmail(string email)
        {
            var atIndex = email.IndexOf('@');
            if (atIndex <= 0 || atIndex == email.Length - 1)
            {
                return false;
            }

            // Require a dot after the @ (e.g. user@domain.tld)
            return email.IndexOf('.', atIndex) != -1;
        }

        private static bool ContainsLetter(string value)
        {
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

        private static bool ContainsNumber(string value)
        {
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
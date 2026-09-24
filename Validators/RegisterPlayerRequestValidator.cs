using FluentValidation;
using TripleTriadApi.Controllers;

namespace TripleTriadApi.Validators
{
    /// <summary>
    /// FluentValidation rules for <see cref="RegisterPlayerRequest"/>.
    /// These mirror the client-side rules from the account creation form
    /// (see AccountCreation.tsx) so the backend re-validates everything.
    ///
    /// The password policy itself lives in <see cref="PasswordRules"/> so the recovery flow applies the identical
    /// rules — see that class for why sharing them matters.
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

            RuleFor(x => x.Password).ApplyPasswordPolicy();
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
    }
}
using FluentValidation;
using TripleTriadApi.Controllers;

namespace TripleTriadApi.Validators
{
    /// <summary>
    /// FluentValidation rules for <see cref="ResetPasswordRequest"/>.
    ///
    /// The new password goes through <see cref="PasswordRules"/> — the *same* policy the registration form applies —
    /// so recovering an account can never leave it with a weaker password than creating one. The code itself is only
    /// checked for presence here: whether it is correct, live, spent or out of attempts is the service's business,
    /// and answering that in the validator would leak the difference between "wrong code" and "no such account".
    /// </summary>
    public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
    {
        public ResetPasswordRequestValidator()
        {
            RuleFor(x => x.Token)
                .Must(token => !string.IsNullOrWhiteSpace(token))
                .WithMessage("Enter the code from the email.");

            RuleFor(x => x.NewPassword).ApplyPasswordPolicy();
        }
    }
}

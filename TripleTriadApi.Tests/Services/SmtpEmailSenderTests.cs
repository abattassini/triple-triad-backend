using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The contract <see cref="SmtpEmailSender"/> documents: an unconfigured mailbox must fail <em>when a message is
    /// sent</em>, with a message that says what to set — never at startup.
    ///
    /// That is what lets the API boot and serve on a machine with no mailbox at all (a fresh clone, CI, a review
    /// environment). If this check ever moves into a constructor the whole game stops being able to start because an
    /// email password is missing, so this test exists to make that mistake loud.
    ///
    /// Nothing here touches the network: an unconfigured sender is refused before any socket is opened.
    /// </summary>
    public class SmtpEmailSenderTests
    {
        [Fact]
        public async Task SendAsync_WhenEmailIsNotConfigured_FailsWithAnActionableMessage()
        {
            var sender = new SmtpEmailSender(
                Options.Create(new EmailOptions()),
                NullLogger<SmtpEmailSender>.Instance
            );

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                sender.SendAsync(new EmailMessage("player@example.com", "Subject", "Body"))
            );

            Assert.Contains("Email is not configured", error.Message);
            Assert.Contains("Email__Smtp__Password", error.Message);
        }

        [Fact]
        public void IsConfigured_NeedsEveryPartAndNotJustAHost()
        {
            // A host on its own is the tempting half-configuration: the send would then fail deep inside the SMTP
            // handshake with an authentication error rather than with "you have not set this up".
            var options = new EmailOptions
            {
                FromAddress = "triple-triad@example.com",
                Smtp = new SmtpOptions { Host = "smtp.gmail.com", Port = 587 },
            };

            Assert.False(options.IsConfigured);

            options.Smtp.User = "triple-triad@example.com";
            options.Smtp.Password = "app-password";

            Assert.True(options.IsConfigured);
        }
    }
}

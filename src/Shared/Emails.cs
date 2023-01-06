using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Shared;

public static class Emails
{
    public static async Task SendEmail(string subject, string message, bool toMe, ILogger log)
    {
        await SendEmail(subject, message, message.Replace("\r\n", "<br>"), toMe, log);
    }

    public static async Task SendEmail(string subject, string message, string messageHtml, bool toMe, ILogger log)
    {
        var emailFromAddress = Environment.GetEnvironmentVariable("NOTIFY_FROM_ADDRESS");
        var emailFromName = Environment.GetEnvironmentVariable("NOTIFY_FROM_NAME");
        var emailToAddress = toMe ? Environment.GetEnvironmentVariable("NOTIFY_ME_TO_ADDRESS") : Environment.GetEnvironmentVariable("NOTIFY_TO_ADDRESS");
        var emailToName = toMe ? Environment.GetEnvironmentVariable("NOTIFY_ME_TO_NAME") : Environment.GetEnvironmentVariable("NOTIFY_TO_NAME");
        var apiKey = Environment.GetEnvironmentVariable("SENDGRID_KEY");

        log.LogInformation("Sending email to {emailToAddress} from {emailFromAddress} subject {subject}", emailToAddress, emailFromAddress, subject);

        var client = new SendGridClient(apiKey);
        var from = new EmailAddress(emailFromAddress, emailFromName);
        var to = new EmailAddress(emailToAddress, emailToName);
        var msg = MailHelper.CreateSingleEmail(from, to, subject, message, messageHtml);
        await client.SendEmailAsync(msg);
    }
}

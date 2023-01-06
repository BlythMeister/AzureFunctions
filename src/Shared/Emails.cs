using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Shared;

public static class Emails
{
    public static async Task SendErrorEmail(string subject, string message, ILogger log)
    {
        var emailFromAddress = Environment.GetEnvironmentVariable("NOTIFY_FROM_ADDRESS");
        var emailFromName = Environment.GetEnvironmentVariable("NOTIFY_FROM_NAME");
        var emailToAddressMe = Environment.GetEnvironmentVariable("NOTIFY_ME_TO_ADDRESS");
        var emailToNameMe = Environment.GetEnvironmentVariable("NOTIFY_ME_TO_NAME");
        var apiKey = Environment.GetEnvironmentVariable("SENDGRID_KEY");

        log.LogInformation("Sending email to {emailToAddress} from {emailFromAddress} subject {subject}", emailToAddressMe, emailFromAddress, subject);

        var client = new SendGridClient(apiKey);
        var from = new EmailAddress(emailFromAddress, emailFromName);
        var to = new EmailAddress(emailToAddressMe, emailToNameMe);
        var msg = MailHelper.CreateSingleEmail(from, to, subject, message, null);
        await client.SendEmailAsync(msg);
    }

    public static async Task SendEmail(string subject, string message, ILogger log)
    {
        await SendEmail(subject, message, null, log);
    }

    public static async Task SendEmail(string subject, string message, string? messageHtml, ILogger log)
    {
        var emailFromAddress = Environment.GetEnvironmentVariable("NOTIFY_FROM_ADDRESS");
        var emailFromName = Environment.GetEnvironmentVariable("NOTIFY_FROM_NAME");
        var emailToAddress = Environment.GetEnvironmentVariable("NOTIFY_TO_ADDRESS");
        var emailToName = Environment.GetEnvironmentVariable("NOTIFY_TO_NAME");
        var emailToAddressMe = Environment.GetEnvironmentVariable("NOTIFY_ME_TO_ADDRESS");
        var emailToNameMe = Environment.GetEnvironmentVariable("NOTIFY_ME_TO_NAME");
        var apiKey = Environment.GetEnvironmentVariable("SENDGRID_KEY");

        log.LogInformation("Sending email to {emailToAddress} from {emailFromAddress} subject {subject}", emailToAddress, emailFromAddress, subject);

        var client = new SendGridClient(apiKey);
        var from = new EmailAddress(emailFromAddress, emailFromName);
        var to = new EmailAddress(emailToAddress, emailToName);
        var toMe = new EmailAddress(emailToAddressMe, emailToNameMe);
        var msg = MailHelper.CreateSingleEmail(from, to, subject, message, messageHtml);
        msg.AddCategory("AzureFunctions");
        msg.AddCc(toMe);
        await client.SendEmailAsync(msg);
    }
}

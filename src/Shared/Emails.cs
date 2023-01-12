using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Shared;

public static class Emails
{
    public record SendData(DateTime SentTime, string Destination, bool Success, string Subject);

    private enum DestinationType
    {
        ToMe,
        ToPerson,
        ToPersonCcMe
    }

    public static async Task SendErrorEmail(string subject, string message, ILogger log)
    {
        await DoSend(subject, message, null, DestinationType.ToMe, log);
    }

    public static async Task SendEmail(string subject, string message, ILogger log)
    {
        await SendEmail(subject, message, null, log);
    }

    public static async Task SendEmail(string subject, string message, string? messageHtml, ILogger log)
    {
        if (!bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_CC_ME"), out var ccMe))
        {
            ccMe = false;
        }

        await DoSend(subject, message, messageHtml, ccMe ? DestinationType.ToPersonCcMe : DestinationType.ToPerson, log);
    }

    private static async Task DoSend(string subject, string message, string? messageHtml, DestinationType destinationType, ILogger log)
    {
        var apiKey = Environment.GetEnvironmentVariable("SENDGRID_KEY");
        var emailFromAddress = Environment.GetEnvironmentVariable("NOTIFY_FROM_ADDRESS");
        var emailFromName = Environment.GetEnvironmentVariable("NOTIFY_FROM_NAME");
        var emailToAddress = Environment.GetEnvironmentVariable("NOTIFY_TO_ADDRESS");
        var emailToName = Environment.GetEnvironmentVariable("NOTIFY_TO_NAME");
        var emailToAddressMe = Environment.GetEnvironmentVariable("NOTIFY_ME_TO_ADDRESS");
        var emailToNameMe = Environment.GetEnvironmentVariable("NOTIFY_ME_TO_NAME");

        log.LogInformation("Sending email to {emailToAddress} from {emailFromAddress} subject {subject}", emailToAddress, emailFromAddress, subject);
        var sends = await Blobs.ReadAppDataBlob<List<SendData>>("emails.dat", log);

        var client = new SendGridClient(apiKey);
        var from = new EmailAddress(emailFromAddress, emailFromName);
        var to = new EmailAddress(emailToAddress, emailToName);
        var toMe = new EmailAddress(emailToAddressMe, emailToNameMe);
        var toUse = destinationType == DestinationType.ToMe ? toMe : to;

        var msg = MailHelper.CreateSingleEmail(from, toUse, subject, message, messageHtml);

        msg.AddCategory("AzureFunctions");

        if (destinationType == DestinationType.ToPersonCcMe)
        {
            msg.AddCc(toMe);
        }

        try
        {
            var response = await client.SendEmailAsync(msg);
            sends.Add(new SendData(DateTime.UtcNow, destinationType.ToString(), response.IsSuccessStatusCode, subject));
        }
        catch (Exception e)
        {
            log.LogError(e, "Error sending email");
            sends.Add(new SendData(DateTime.UtcNow, destinationType.ToString(), false, subject));
        }

        await Blobs.WriteAppDataBlob(sends, "emails.dat", log);
    }
}

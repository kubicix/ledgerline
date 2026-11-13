using Ledgerline.Notifications.Infrastructure.Entities;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Ledgerline.Notifications.Sending;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
}

public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ledgerline", "noreply@ledgerline.test"));
        message.To.Add(MailboxAddress.Parse(notification.RecipientEmail));
        message.Subject = notification.Subject;
        message.Body = new TextPart("plain") { Text = notification.Body };
        message.MessageId = notification.Id.ToString("N") + "@ledgerline.test";
        using var client = new SmtpClient { Timeout = 5000 };
        await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.None, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

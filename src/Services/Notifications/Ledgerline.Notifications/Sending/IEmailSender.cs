using Ledgerline.Notifications.Infrastructure.Entities;

namespace Ledgerline.Notifications.Sending;

public interface IEmailSender
{
    Task SendAsync(Notification notification, CancellationToken ct);
}

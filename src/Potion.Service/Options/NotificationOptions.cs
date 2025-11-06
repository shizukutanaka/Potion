using System.ComponentModel.DataAnnotations;

namespace Potion.Service.Options;

public class NotificationOptions
{
    public bool Enabled { get; set; } = false;

    [Url]
    public string SlackWebhookUrl { get; set; } = string.Empty;

    [Url]
    public string TeamsWebhookUrl { get; set; } = string.Empty;

    public int RetryAttempts { get; set; } = 3;
}

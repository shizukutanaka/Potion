using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Potion.Service.Infrastructure;

public class NotificationOptions
{
    public bool Enabled { get; set; } = false;
    public string SlackWebhookUrl { get; set; } = string.Empty;
    public string TeamsWebhookUrl { get; set; } = string.Empty;
    public int RetryAttempts { get; set; } = 3;
}

public class NotificationService
{
    private readonly ILogger<NotificationService> _logger;
    private readonly NotificationOptions _options;
    private readonly HttpClient _httpClient;

    public NotificationService(
        ILogger<NotificationService> logger,
        IOptions<NotificationOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _httpClient = new HttpClient();
    }

    public async Task SendSlackNotificationAsync(string message, string channel = "#general")
    {
        if (!_options.Enabled || string.IsNullOrEmpty(_options.SlackWebhookUrl))
            return;

        try
        {
            var payload = new
            {
                text = message,
                channel = channel
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            for (int i = 0; i < _options.RetryAttempts; i++)
            {
                try
                {
                    var response = await _httpClient.PostAsync(_options.SlackWebhookUrl, content);
                    response.EnsureSuccessStatusCode();
                    _logger.LogInformation("Slack notification sent successfully");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send Slack notification (attempt {Attempt})", i + 1);
                    if (i < _options.RetryAttempts - 1)
                        await Task.Delay(1000 * (i + 1));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Slack notification after all retries");
        }
    }

    public async Task SendTeamsNotificationAsync(string title, string message)
    {
        if (!_options.Enabled || string.IsNullOrEmpty(_options.TeamsWebhookUrl))
            return;

        try
        {
            var payload = new
            {
                title = title,
                text = message
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            for (int i = 0; i < _options.RetryAttempts; i++)
            {
                try
                {
                    var response = await _httpClient.PostAsync(_options.TeamsWebhookUrl, content);
                    response.EnsureSuccessStatusCode();
                    _logger.LogInformation("Teams notification sent successfully");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send Teams notification (attempt {Attempt})", i + 1);
                    if (i < _options.RetryAttempts - 1)
                        await Task.Delay(1000 * (i + 1));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Teams notification after all retries");
        }
    }

    public async Task SendAlertNotificationAsync(string alertType, string details)
    {
        var message = $"🚨 **Otedama Alert**: {alertType}\n{details}";
        var teamsTitle = $"Otedama Alert: {alertType}";

        await Task.WhenAll(
            SendSlackNotificationAsync(message),
            SendTeamsNotificationAsync(teamsTitle, details)
        );
    }
}

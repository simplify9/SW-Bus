using Microsoft.Extensions.Logging;
using SW.Bus.SampleWeb.Models;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb.Consumers;

/// <summary>
/// Scenario 5 — Very fast, high-throughput consumer (5–30 ms).
/// Simulates calling an email sending API (async fire-and-forget internally).
/// Prefetch is high (configured via AddQueueOption in Startup) to saturate throughput.
/// High-priority emails (Priority = 1) are handled first.
/// </summary>
public class EmailNotificationConsumer : IConsume<EmailNotificationMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<EmailNotificationConsumer> logger;

    public EmailNotificationConsumer(ILogger<EmailNotificationConsumer> logger)
        => this.logger = logger;

    public async Task Process(EmailNotificationMessage message)
    {
        await Task.Delay(Rng.Next(5, 30));

        logger.LogInformation(
            "Email sent to {Recipient} | Subject: {Subject} | Template: {Template} | Lang: {Lang}",
            message.RecipientEmail, message.Subject, message.TemplateId, message.Language);
    }
}

/// <summary>
/// Scenario 6 — Fast SMS consumer (8–40 ms).
/// Simulates SMS gateway API call. Occasionally fails (10 %) due to
/// rate limits on the gateway, producing a small but steady retry flow.
/// </summary>
public class SmsNotificationConsumer : IConsume<SmsNotificationMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<SmsNotificationConsumer> logger;

    public SmsNotificationConsumer(ILogger<SmsNotificationConsumer> logger)
        => this.logger = logger;

    public async Task Process(SmsNotificationMessage message)
    {
        await Task.Delay(Rng.Next(8, 40));

        // 10 % transient gateway-rate-limit failure
        if (Rng.NextDouble() < 0.10)
            throw new TimeoutException(
                $"SMS gateway rate limit exceeded for sender {message.SenderId}. Retry scheduled.");

        logger.LogInformation(
            "SMS sent via {Sender} → {Number}: {Content}",
            message.SenderId, message.PhoneNumber[..4] + "****", message.Content.Length > 40 ? message.Content[..40] + "…" : message.Content);
    }
}

/// <summary>
/// Scenario 7 — Push notification consumer (5–20 ms).
/// Very fast; virtually never fails. High prefetch for burst throughput.
/// </summary>
public class PushNotificationConsumer : IConsume<PushNotificationMessage>
{
    private static readonly Random Rng = Random.Shared;
    private readonly ILogger<PushNotificationConsumer> logger;

    public PushNotificationConsumer(ILogger<PushNotificationConsumer> logger)
        => this.logger = logger;

    public async Task Process(PushNotificationMessage message)
    {
        await Task.Delay(Rng.Next(5, 20));
        logger.LogInformation("Push sent: [{Title}] → token {Token}",
            message.Title, message.DeviceToken[..8] + "…");
    }
}


namespace NavalArchive.Messaging;

/// <summary>Wallet lifecycle events published to RabbitMQ for distributed tracing (Order → Payment → Shipping → Notification).</summary>
public record WalletActivity(string Step, string? AccountId, decimal? Amount, DateTimeOffset At);

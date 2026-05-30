namespace NavalArchive.Messaging;

public record OrderSubmitted(Guid OrderId, DateTimeOffset SubmittedAt);

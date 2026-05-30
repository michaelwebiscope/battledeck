namespace NavalArchive.Messaging;

public record WalletActivityRequest(string Step, string? AccountId, decimal? Amount);

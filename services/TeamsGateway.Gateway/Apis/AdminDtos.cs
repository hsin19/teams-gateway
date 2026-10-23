namespace TeamsGateway.Gateway.Apis;

// /admin 的請求 / 回應。管理端點只給維運用，不是對外 wire contract，所以留在 gateway、不進 Contracts。

public sealed record CreateApiKeyRequest(string Name);

public sealed record SubscribeRequest(string Chat);

public sealed record ImportedCookies(int Imported);

public sealed record AuthInfo(bool HasCookies, string? SkypeId, DateTimeOffset? TokenExpiresAt);

public sealed record TokenInfo(string SkypeId, DateTimeOffset ExpiresAt);

/// <summary>剛建好的 key；<c>ApiKey</c> 明文只會出現這一次。</summary>
public sealed record CreatedApiKey(Guid Id, string Name, string ApiKey);

public sealed record ApiKeyInfo(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

public sealed record SubscriptionInfo(string Id, string? Topic, string? LastMessageId, DateTimeOffset UpdatedAt);

public sealed record SubscribedConversation(string Id, string? Topic);

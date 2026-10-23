using System.Text.Json;
using System.Text.Json.Serialization;
using TeamsGateway.Gateway.Apis;

namespace TeamsGateway.Gateway;

/// <summary>
/// gateway 自己（非 wire contract）的 API 型別的 source-gen context，排在 <c>ContractsJsonContext</c> 後面。
/// 新增端點的 request / response 型別要加進來；functional test 關掉了 reflection 序列化，漏加會直接失敗。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(SubscribeRequest))]
[JsonSerializable(typeof(ImportedCookies))]
[JsonSerializable(typeof(AuthInfo))]
[JsonSerializable(typeof(TokenInfo))]
[JsonSerializable(typeof(CreatedApiKey))]
[JsonSerializable(typeof(IReadOnlyList<ApiKeyInfo>))]
[JsonSerializable(typeof(IReadOnlyList<SubscriptionInfo>))]
[JsonSerializable(typeof(SubscribedConversation))]
// 不是 body，但 OpenAPI 文件要替 query 參數（/v1/chats/{id}/messages 的 limit）產 schema
[JsonSerializable(typeof(int?))]
internal sealed partial class GatewayJsonContext : JsonSerializerContext;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsGateway.Contracts;

/// <summary>
/// wire DTO 的 System.Text.Json source-gen context（<see cref="JsonSerializerDefaults.Web"/>：camelCase、讀的時候不分大小寫）。
/// 服務端與 client 序列化這些型別都走它：不靠 reflection，比較快，也是之後能轉 AOT 的前提。
/// 各服務自己的型別放各自的 context，用 <c>TypeInfoResolverChain</c> 串在一起。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ConversationDto))]
[JsonSerializable(typeof(IReadOnlyList<ConversationDto>))]
[JsonSerializable(typeof(MessageDto))]
[JsonSerializable(typeof(IReadOnlyList<MessageDto>))]
[JsonSerializable(typeof(MessagePage))]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(SendMessageResult))]
[JsonSerializable(typeof(AuthStatusDto))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsGateway.Gateway.Teams;

// 送給 Teams（Trouter / registrar / chatsvc）的 body。欄位名是照網頁版流量逆向來的，送出去的 JSON 一個字都不能變：
// 用 Web 預設（camelCase）讓 C# 名字轉成線上的名字，轉不出來的（全小寫連字）用 JsonPropertyName 釘死。

/// <summary>Trouter 連線網址的 <c>tc</c> 參數。</summary>
internal sealed record TrouterClientInfo(string Cv, string Ua, string Hr, string V);

/// <summary>連上後的 <c>user.authenticate</c> 事件。</summary>
internal sealed record TrouterAuthenticate(string Name, TrouterAuthArgs[] Args);

internal sealed record TrouterAuthArgs(Dictionary<string, string> Headers);

/// <summary>回 <c>trouter.message_loss</c>：把對方的 args 原樣送回。</summary>
internal sealed record TrouterMessageLossAck(string Name, JsonElement Args);

/// <summary>推播的 ack；不 ack 的話 Trouter 會重送。</summary>
internal sealed record TrouterPushAck(long Id, int Status, Dictionary<string, string> Headers, string Body);

/// <summary>registrar 的推播註冊。</summary>
internal sealed record RegistrationRequest(
    TrouterClientDescription ClientDescription, string RegistrationId, string NodeId, RegistrationTransports Transports);

internal sealed record TrouterClientDescription(
    string AppId, string AesKey, string LanguageId, string Platform, string TemplateKey, string PlatformUIVersion, string ProductContext);

/// <summary>
/// 線上送的是小寫 <c>trouter</c>：原本程式寫 <c>TROUTER</c>，但 <c>JsonContent.Create</c> 的 Web 預設 camelCase 會把它轉成小寫，
/// 註冊一直是用這個值在運作的，維持原樣。
/// </summary>
internal sealed record RegistrationTransports(TrouterTransport[] Trouter);

internal sealed record TrouterTransport(string Context, string Path, int Ttl);

/// <summary>
/// chatsvc 發一則訊息（<c>Text</c> 或 <c>RichText/Html</c>），編輯也是同一個格式。沒有引用時不帶 <c>properties</c>；
/// 編輯不帶 <c>clientmessageid</c>：沒帶時伺服器留著原本的（spike/FINDINGS.md「編輯與刪除」），帶別的會不會蓋掉沒驗過。
/// </summary>
internal sealed record ChatsvcSendRequest(
    string Content,
    [property: JsonPropertyName("messagetype")] string MessageType,
    [property: JsonPropertyName("contenttype")] string ContentType,
    [property: JsonPropertyName("clientmessageid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientMessageId,
    [property: JsonPropertyName("imdisplayname")] string ImDisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatsvcSendProperties? Properties = null);

/// <summary>
/// 引用回覆：<c>qtdMsgs</c> 是字串化的 <see cref="QuotedMessageRef"/> 陣列。內容裡的 Reply blockquote 也要有，
/// 只有一邊 Teams 不當成回覆（spike/FINDINGS.md「發話」）。
/// </summary>
internal sealed record ChatsvcSendProperties(string QtdMsgs);

/// <summary><c>MessageId</c> 跟讀回來的訊息 id 一樣是字串（spike 驗證過這個形式；伺服器存回來時會變成數字）。</summary>
internal sealed record QuotedMessageRef(string MessageId, string Sender, long Time);

/// <summary><c>mt/beta/users/fetchShortProfile</c> 的回應（只取用得到的欄位）。</summary>
internal sealed record ShortProfiles(IReadOnlyList<ShortProfile>? Value);

internal sealed record ShortProfile(string? Mri, string? DisplayName);

/// <summary>Teams 協定 body 與登入 cookie 的 source-gen context（Web 預設）。</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TrouterClientInfo))]
[JsonSerializable(typeof(TrouterAuthenticate))]
[JsonSerializable(typeof(TrouterMessageLossAck))]
[JsonSerializable(typeof(TrouterPushAck))]
[JsonSerializable(typeof(RegistrationRequest))]
[JsonSerializable(typeof(ChatsvcSendRequest))]
[JsonSerializable(typeof(QuotedMessageRef[]))]
[JsonSerializable(typeof(List<LoginCookie>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(ShortProfiles))]
internal sealed partial class TeamsJsonContext : JsonSerializerContext;

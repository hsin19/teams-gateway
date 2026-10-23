using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TeamsGateway.Gateway.Data;

/// <summary>
/// 寫進 Postgres 前把 DateTimeOffset 轉成 UTC：<c>timestamptz</c> 存的是時間點，Npgsql 碰到 offset 不是 0 的值會直接丟例外。
/// 這個服務的時間本來就幾乎都是 UTC，這裡是保險（例如 Teams 回來的時間帶了別的 offset）；讀回來一律是 UTC。
/// </summary>
public sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    value => value.ToUniversalTime(),
    value => value);

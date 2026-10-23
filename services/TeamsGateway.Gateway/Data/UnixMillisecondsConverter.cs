using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TeamsGateway.Gateway.Data;

/// <summary>
/// DateTimeOffset ↔ Unix 毫秒（UTC）。SQLite 沒有時間型別，存數字讓比較、排序、範圍查詢都是整數運算，讀出來再在程式裡轉。
/// 一律 UTC、不存 offset（這個服務的時間全是 UTC）；精度到毫秒（Teams 的時間戳也是毫秒）。
/// 用 sqlite3 看的時候：<c>datetime(ReceivedAt / 1000, 'unixepoch')</c>。
/// </summary>
public sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.ToUnixTimeMilliseconds(),
    milliseconds => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));

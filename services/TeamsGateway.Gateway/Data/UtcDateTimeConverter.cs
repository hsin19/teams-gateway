using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TeamsGateway.Gateway.Data;

/// <summary>DateTimeOffset ↔ UTC DateTime，讓 SQLite 能排序 / 比較時間（SQLite 不支援 DateTimeOffset 的 ORDER BY）。</summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTimeOffset, DateTime>(
    offset => offset.UtcDateTime,
    utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero));

using Microsoft.EntityFrameworkCore;

namespace TeamsGateway.Gateway.Data;

/// <summary>
/// gateway 的 model。實際建的是 provider 對應的子類別（<see cref="SqliteGatewayDbContext"/> / <see cref="PostgresGatewayDbContext"/>）：
/// EF 的 migration 綁 provider，靠 context 型別分辨是哪一套（ADR 0009）。程式各處照樣用 <c>IDbContextFactory&lt;GatewayDbContext&gt;</c>。
/// </summary>
public abstract class GatewayDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite 沒有時間型別，EF 預設存帶 offset 的 TEXT，連 ORDER BY / 比較都不支援：存成 Unix 毫秒（INTEGER）。
        // Postgres 用原生的 timestamptz，但 Npgsql 只收 offset 0 的 DateTimeOffset，寫入前一律轉 UTC
        if (Database.IsSqlite())
            builder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
        else if (Database.IsNpgsql())
            builder.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
    }

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<MessageEvent> MessageEvents => Set<MessageEvent>();
    public DbSet<MessageRevision> MessageRevisions => Set<MessageRevision>();
    public DbSet<PublishedEvent> PublishedEvents => Set<PublishedEvent>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<SecureValue> SecureValues => Set<SecureValue>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Conversation>().HasKey(c => c.Id);

        b.Entity<MessageEvent>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.ConversationId, m.SequenceId });
            e.HasIndex(m => m.ReceivedAt);
            e.HasOne(m => m.Conversation).WithMany(c => c.Messages).HasForeignKey(m => m.ConversationId);
        });

        b.Entity<MessageRevision>(e =>
        {
            e.HasKey(r => new { r.MessageId, r.Version });
            e.HasOne(r => r.Message).WithMany(m => m.Revisions).HasForeignKey(r => r.MessageId);
        });

        b.Entity<PublishedEvent>(e =>
        {
            e.HasKey(p => p.Seq);
            e.HasIndex(p => p.PublishedAt); // 定期清舊事件用
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.HasIndex(k => k.KeyHash).IsUnique();
        });

        b.Entity<WebhookSubscription>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasOne(w => w.ApiKey).WithMany(k => k.Webhooks).HasForeignKey(w => w.ApiKeyId);
            e.HasIndex(w => w.ConversationId);
        });

        b.Entity<SecureValue>().HasKey(s => s.Key);
    }
}

/// <summary>SQLite 的 context（本機開發預設），migration 在 <c>Data/Migrations/Sqlite</c>。</summary>
public sealed class SqliteGatewayDbContext(DbContextOptions<SqliteGatewayDbContext> options) : GatewayDbContext(options);

/// <summary>Postgres 的 context，migration 在 <c>Data/Migrations/Postgres</c>。</summary>
public sealed class PostgresGatewayDbContext(DbContextOptions<PostgresGatewayDbContext> options) : GatewayDbContext(options);

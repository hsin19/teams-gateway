using Microsoft.EntityFrameworkCore;

namespace TeamsGateway.Gateway.Data;

public class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // 時間存成 Unix 毫秒（INTEGER）：SQLite 沒有時間型別，EF 預設存帶 offset 的 TEXT，連 ORDER BY / 比較都不支援
        builder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
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

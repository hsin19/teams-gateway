using Microsoft.EntityFrameworkCore;

namespace TeamsGateway.Gateway.Data;

public class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<MessageEvent> MessageEvents => Set<MessageEvent>();
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

using Microsoft.EntityFrameworkCore;
using LinkdUnified.Data.Entities;

namespace LinkdUnified.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<LinkedInAccountEntity> LinkedInAccounts => Set<LinkedInAccountEntity>();
    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<WebhookSubscriptionEntity> WebhookSubscriptions => Set<WebhookSubscriptionEntity>();
    public DbSet<WebhookEventLogEntity> WebhookEventLogs => Set<WebhookEventLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LinkedInAccountEntity>(entity =>
        {
            entity.HasKey(e => e.AccountId);
            entity.Property(e => e.Username).HasMaxLength(256);
        });

        modelBuilder.Entity<ConversationEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Account)
                  .WithMany(a => a.Conversations)
                  .HasForeignKey(e => e.AccountId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Conversation)
                  .WithMany(c => c.Messages)
                  .HasForeignKey(e => e.ConversationId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.ConversationId);
            entity.HasIndex(e => e.SentAt);
        });

        modelBuilder.Entity<WebhookSubscriptionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Account)
                  .WithMany(a => a.Webhooks)
                  .HasForeignKey(e => e.AccountId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WebhookEventLogEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Subscription)
                  .WithMany(s => s.EventLogs)
                  .HasForeignKey(e => e.WebhookSubscriptionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

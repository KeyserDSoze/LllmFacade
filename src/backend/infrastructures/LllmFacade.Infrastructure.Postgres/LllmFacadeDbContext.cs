using System.Text.Json;
using LllmFacade.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace LllmFacade.Infrastructure.Postgres;

public sealed class LllmFacadeDbContext(DbContextOptions<LllmFacadeDbContext> options) : DbContext(options)
{
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<ChatGptEndpoint> Endpoints => Set<ChatGptEndpoint>();
    public DbSet<TrackedProject> Projects => Set<TrackedProject>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdminUser>(user =>
        {
            user.ToTable("admin_users");
            user.Property(x => x.UserName).HasMaxLength(64);
            user.HasIndex(x => x.UserName).IsUnique();
        });

        modelBuilder.Entity<ChatGptEndpoint>(endpoint =>
        {
            endpoint.ToTable("chatgpt_endpoints");
            endpoint.Property(x => x.Name).HasMaxLength(80);
            endpoint.Property(x => x.UserAgent).HasMaxLength(512);
            endpoint.Property(x => x.Locale).HasMaxLength(35);
            endpoint.Property(x => x.TimeZone).HasMaxLength(64);
            endpoint.Property(x => x.ApiKeyHash).HasMaxLength(64);
            endpoint.Property(x => x.ApiKeyPrefix).HasMaxLength(16);
            endpoint.Property(x => x.StorageStateHash).HasMaxLength(64);
            endpoint.Property(x => x.AccountEmail).HasMaxLength(320);
            endpoint.HasIndex(x => x.ApiKeyPrefix).IsUnique();
            endpoint.Property(x => x.Version).IsRowVersion();
            JsonColumn(endpoint.Property(x => x.Models));
            JsonColumn(endpoint.Property(x => x.ThinkingEfforts));
            JsonColumn(endpoint.Property(x => x.ExtraHeaders));
        });

        modelBuilder.Entity<TrackedProject>(project =>
        {
            project.ToTable("tracked_projects");
            project.Property(x => x.ProjectId).HasMaxLength(128);
            project.Property(x => x.Name).HasMaxLength(200);
            project.Property(x => x.TargetConversationId).HasMaxLength(64);
            project.Property(x => x.CurrentConversationId).HasMaxLength(64);
            project.Property(x => x.PendingAfterNodeId).HasMaxLength(128);
            project.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            project.Property(x => x.Version).IsRowVersion();
            project.HasOne(x => x.Endpoint).WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Cascade);
            project.HasIndex(x => new { x.EndpointId, x.ProjectId }).IsUnique();
            project.HasIndex(x => new { x.AutopilotEnabled, x.NextCheckAt });
        });
    }

    private static void JsonColumn<T>(PropertyBuilder<T> property) where T : class, new()
    {
        property.HasColumnType("jsonb").HasConversion(
            value => Serialize(value),
            json => Deserialize<T>(json),
            new ValueComparer<T>(
                (left, right) => Serialize(left) == Serialize(right),
                value => Serialize(value).GetHashCode(),
                value => Deserialize<T>(Serialize(value))));
    }

    private static string Serialize<T>(T? value) => JsonSerializer.Serialize(value);

    private static T Deserialize<T>(string json) where T : new() => JsonSerializer.Deserialize<T>(json) ?? new T();
}

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LllmFacadeDbContext>
{
    public LllmFacadeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LllmFacadeDbContext>()
            .UseNpgsql("Host=localhost;Database=lllmfacade;Username=lllmfacade")
            .Options);
}

public static class PostgresServiceCollectionExtensions
{
    public static IServiceCollection AddLllmFacadePostgres(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<LllmFacadeDbContext>(options => options.UseNpgsql(connectionString));
}

using DumbMcpMultiplexer.Models;
using Microsoft.EntityFrameworkCore;

namespace DumbMcpMultiplexer.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<McpServer> Servers => Set<McpServer>();
    public DbSet<ServerCapability> ServerCapabilities => Set<ServerCapability>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<McpServer>(entity =>
        {
            entity.ToTable("servers");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Slug).HasColumnName("slug").IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.Transport).HasColumnName("transport").IsRequired().HasDefaultValue("remote_http");
            entity.Property(e => e.Enabled).HasColumnName("enabled").IsRequired().HasDefaultValue(true);
            entity.Property(e => e.Url).HasColumnName("url");
            entity.Property(e => e.Headers).HasColumnName("headers").IsRequired().HasDefaultValue("{}");
            entity.Property(e => e.Command).HasColumnName("command");
            entity.Property(e => e.Args).HasColumnName("args");
            entity.Property(e => e.Env).HasColumnName("env");
            entity.Property(e => e.ContainerImage).HasColumnName("container_image");
            entity.Property(e => e.ContainerRuntime).HasColumnName("container_runtime");
            entity.Property(e => e.ContainerPackages).HasColumnName("container_packages").IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.ContainerMounts).HasColumnName("container_mounts").IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

            entity.HasIndex(e => e.Slug).IsUnique();
        });

        modelBuilder.Entity<ServerCapability>(entity =>
        {
            entity.ToTable("server_capabilities");

            entity.HasKey(e => new { e.ServerId, e.Kind, e.Name });

            entity.Property(e => e.ServerId).HasColumnName("server_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Enabled).HasColumnName("enabled").IsRequired().HasDefaultValue(true);
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.SchemaJson).HasColumnName("schema_json");
            entity.Property(e => e.FetchedAt).HasColumnName("fetched_at").IsRequired();

            entity.HasOne(e => e.Server)
                .WithMany(s => s.Capabilities)
                .HasForeignKey(e => e.ServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.ToTable("settings");

            entity.HasKey(e => e.Key);

            entity.Property(e => e.Key).HasColumnName("key");
            entity.Property(e => e.Value).HasColumnName("value").IsRequired();
        });
    }
}

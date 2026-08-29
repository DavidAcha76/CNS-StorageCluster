using CNS.StorageCluster.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace CNS.StorageCluster.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<StorageNode> Nodes => Set<StorageNode>();
    public DbSet<MetricRecord> Metrics => Set<MetricRecord>();
    public DbSet<NodeEvent> NodeEvents => Set<NodeEvent>();
    public DbSet<CommandRecord> Commands => Set<CommandRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StorageNode>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(3).IsRequired();
            e.Property(x => x.RegionName).HasMaxLength(80).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.MachineName).HasMaxLength(200);
            e.Property(x => x.OperatingSystem).HasMaxLength(300);
            e.Property(x => x.MacAddress).HasMaxLength(50);
            e.Property(x => x.IpAddress).HasMaxLength(50);
        });

        modelBuilder.Entity<MetricRecord>(e =>
        {
            e.HasIndex(x => new { x.NodeId, x.TimestampUtc });
            e.Property(x => x.DiskName).HasMaxLength(200).IsRequired();
            e.Property(x => x.DiskType).HasMaxLength(30).IsRequired();
            e.HasOne(x => x.Node).WithMany(x => x.Metrics).HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NodeEvent>(e =>
        {
            e.HasIndex(x => new { x.NodeId, x.TimestampUtc });
            e.Property(x => x.EventType).HasMaxLength(30).IsRequired();
            e.HasOne(x => x.Node).WithMany(x => x.Events).HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommandRecord>(e =>
        {
            e.HasIndex(x => x.CommandId).IsUnique();
            e.Property(x => x.CommandId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(30).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.Payload).HasMaxLength(1000).IsRequired();
            e.HasOne(x => x.Node).WithMany(x => x.Commands).HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

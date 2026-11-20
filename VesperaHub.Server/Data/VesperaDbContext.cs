using Microsoft.EntityFrameworkCore;
using VesperaHub.Server.Models;

namespace VesperaHub.Server.Data;

public class VesperaDbContext(DbContextOptions<VesperaDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Message> Messages => Set<Message>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(e =>
        {
            e.Property(u => u.Username).HasMaxLength(20);
            e.Property(u => u.NormalizedUsername).HasMaxLength(20);
            e.HasIndex(u => u.NormalizedUsername).IsUnique();
        });

        model.Entity<Room>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(32);
            e.Property(r => r.Description).HasMaxLength(200);
            e.HasIndex(r => r.Name).IsUnique();

            var seeded = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            e.HasData(
                new Room { Id = 1, Name = "general", Description = "Talk about anything", CreatedAt = seeded },
                new Room { Id = 2, Name = "dev", Description = "Code, builds and bugs", CreatedAt = seeded },
                new Room { Id = 3, Name = "random", Description = "Off-topic and fun", CreatedAt = seeded });
        });

        model.Entity<Message>(e =>
        {
            e.HasOne(m => m.Room).WithMany().HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Sender).WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.Recipient).WithMany().HasForeignKey(m => m.RecipientId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => new { m.RoomId, m.Id });
            e.HasIndex(m => new { m.SenderId, m.RecipientId });
        });
    }
}

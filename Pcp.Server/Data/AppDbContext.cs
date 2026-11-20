using Microsoft.EntityFrameworkCore;
using Pcp.Server.Models;

namespace Pcp.Server.Data;

/// <summary>
/// Contexto de Entity Framework Core para la persistencia del sistema PCP.
/// Utiliza SQLite para desarrollo local y Fase 0, preparado para migrar a MySQL en producción.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>Conjunto de pantallas registradas en el sistema.</summary>
    public DbSet<Screen> Screens => Set<Screen>();

    /// <summary>Grupos de pantallas para control masivo.</summary>
    public DbSet<ScreenGroup> ScreenGroups => Set<ScreenGroup>();

    /// <summary>Historial de comandos y órdenes emitidas a las pantallas.</summary>
    public DbSet<Command> Commands => Set<Command>();

    /// <summary>Usuarios del panel con sus roles (Fase 3).</summary>
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configuración de la entidad Screen
        modelBuilder.Entity<Screen>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasMaxLength(50).IsRequired();
            entity.Property(s => s.Name).HasMaxLength(100).IsRequired();
            entity.Property(s => s.Token).HasMaxLength(128).IsRequired();
            entity.Property(s => s.Status).HasMaxLength(20).IsRequired();
            entity.Property(s => s.CurrentMedia).HasMaxLength(500);
            entity.Ignore(s => s.ThumbnailBase64);
            entity.HasOne(s => s.Group)
                  .WithMany(g => g.Screens)
                  .HasForeignKey(s => s.GroupId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        // Configuración de la entidad ScreenGroup
        modelBuilder.Entity<ScreenGroup>(entity =>
        {
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Name).HasMaxLength(100).IsRequired();
        });

        // Configuración de la entidad Command
        modelBuilder.Entity<Command>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.ScreenId).HasMaxLength(50).IsRequired();
            entity.Property(c => c.Type).HasMaxLength(50).IsRequired();
            entity.Property(c => c.Status).HasMaxLength(20).IsRequired();
            entity.Property(c => c.ResultDetail).HasMaxLength(1000);
        });

        // Configuración de la entidad User (Fase 3)
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Username).HasMaxLength(64).IsRequired();
            entity.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
            entity.Property(u => u.Role).HasMaxLength(20).IsRequired();
            entity.HasIndex(u => u.Username).IsUnique();
        });
    }
}

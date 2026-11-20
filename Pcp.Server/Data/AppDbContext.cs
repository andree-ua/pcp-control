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

    /// <summary>Playlists de reproducción ordenada (Bloque 4).</summary>
    public DbSet<Playlist> Playlists => Set<Playlist>();

    /// <summary>Elementos de las playlists.</summary>
    public DbSet<PlaylistItem> PlaylistItems => Set<PlaylistItem>();

    /// <summary>Horarios recurrentes que lanzan playlists.</summary>
    public DbSet<Schedule> Schedules => Set<Schedule>();

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
            entity.Property(s => s.MacAddress).HasMaxLength(17);
            entity.Property(s => s.LocalIp).HasMaxLength(45);
            entity.Property(s => s.SubnetMask).HasMaxLength(45);
            entity.Property(s => s.NetworkCidr).HasMaxLength(64);
            // Índice para localizar rápidamente agentes de la misma red (relay de Wake-on-LAN).
            entity.HasIndex(s => s.NetworkCidr);
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

        // ── Bloque 4: playlists y programación ─────────────────────────────
        modelBuilder.Entity<Playlist>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).HasMaxLength(100).IsRequired();

            // Los elementos viven y mueren con la playlist.
            entity.HasMany(p => p.Items)
                  .WithOne(i => i.Playlist)
                  .HasForeignKey(i => i.PlaylistId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlaylistItem>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.MediaPath).HasMaxLength(500).IsRequired();
            // El panel siempre lee los elementos de una playlist ordenados.
            entity.HasIndex(i => new { i.PlaylistId, i.Position });
        });

        modelBuilder.Entity<Schedule>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(100).IsRequired();
            entity.Property(s => s.ScreenId).HasMaxLength(50);
            entity.Property(s => s.DaysOfWeek).HasMaxLength(20).IsRequired();
            entity.Property(s => s.StartTime).HasMaxLength(5).IsRequired();
            entity.Property(s => s.LastTriggeredSlot).HasMaxLength(32);
            entity.HasIndex(s => s.Enabled);

            // Borrar una playlist elimina sus horarios; borrar pantalla/grupo
            // solo deja el horario sin destino (se valida al editarlo).
            entity.HasOne(s => s.Playlist)
                  .WithMany(p => p.Schedules)
                  .HasForeignKey(s => s.PlaylistId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.Screen)
                  .WithMany()
                  .HasForeignKey(s => s.ScreenId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(s => s.Group)
                  .WithMany()
                  .HasForeignKey(s => s.GroupId)
                  .OnDelete(DeleteBehavior.SetNull);
        });
    }
}

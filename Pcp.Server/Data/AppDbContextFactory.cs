using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pcp.Server.Data;

/// <summary>
/// Fábrica usada solo por las herramientas de EF Core (dotnet ef migrations / database update).
/// Fuerza el proveedor MySQL para que las migraciones generadas no dependan del entorno
/// de desarrollo local (que usa SQLite).
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = MySqlConnectionResolver.Resolve(BuildConfiguration())
            ?? "Server=localhost;Port=3306;Database=pcp_design;User ID=root;Password=root;";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;

        return new AppDbContext(options);
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Production.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }
}

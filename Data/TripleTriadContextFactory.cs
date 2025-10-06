using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TripleTriadApi.Data;

/// <summary>
/// Design-time factory for creating TripleTriadContext instances.
/// This is used by EF Core tools (like dotnet ef migrations add) at design time.
/// </summary>
public class TripleTriadContextFactory : IDesignTimeDbContextFactory<TripleTriadContext>
{
    public TripleTriadContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TripleTriadContext>();

        // Use connection string from environment variable for migrations
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException(
                "Database connection string not found. Set the 'ConnectionStrings__DefaultConnection' environment variable."
            );

        optionsBuilder.UseNpgsql(connectionString, b => b.MigrationsAssembly("TripleTriadApi"));

        return new TripleTriadContext(optionsBuilder.Options);
    }
}

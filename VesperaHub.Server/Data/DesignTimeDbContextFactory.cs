using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VesperaHub.Server.Data;

/// <summary>Used only by `dotnet ef` tooling to create migrations without starting the web host.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VesperaDbContext>
{
    public VesperaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<VesperaDbContext>()
            .UseSqlite("Data Source=vesperahub.db")
            .Options;
        return new VesperaDbContext(options);
    }
}

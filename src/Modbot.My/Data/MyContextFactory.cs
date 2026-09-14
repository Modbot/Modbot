using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modbot.My.Data;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. The connection string is never used to
/// connect; EF only needs a provider to generate SQL.
/// </summary>
public sealed class MyContextFactory : IDesignTimeDbContextFactory<MyContext>
{
    public MyContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MyContext>()
            .UseNpgsql("Host=localhost;Database=modbot_my_design_time")
            .Options);
}

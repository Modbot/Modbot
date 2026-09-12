using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modbot.Core.Data;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. The connection string is never used to
/// connect -- EF only needs a provider to generate SQL.
/// </summary>
public sealed class ModbotContextFactory : IDesignTimeDbContextFactory<ModbotContext>
{
    public ModbotContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ModbotContext>()
            .UseNpgsql("Host=localhost;Database=modbot_design_time")
            .Options;

        return new ModbotContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AutoReparos.OS.Infrastructure;

public sealed class OrdemServicoDbContextFactory : IDesignTimeDbContextFactory<OrdemServicoDbContext>
{
    public OrdemServicoDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrdemServicoDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__OrdemServico") ?? "Host=localhost;Database=autoreparos_os;Username=postgres")
            .Options;
        return new OrdemServicoDbContext(options);
    }
}

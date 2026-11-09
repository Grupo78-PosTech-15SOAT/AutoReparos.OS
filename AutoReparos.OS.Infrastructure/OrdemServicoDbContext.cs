using AutoReparos.OS.Application;
using AutoReparos.OS.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AutoReparos.OS.Infrastructure;

public sealed class OrdemServicoDbContext(DbContextOptions<OrdemServicoDbContext> options) : DbContext(options), IOrdemServicoRepository
{
    public DbSet<OrdemServico> OrdensServico => Set<OrdemServico>();
    public DbSet<MensagemPendente> MensagensPendentes => Set<MensagemPendente>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var ordem = modelBuilder.Entity<OrdemServico>();
        ordem.ToTable("ordens_servico");
        ordem.HasKey(x => x.Id);
        ordem.Property(x => x.Status).HasMaxLength(40).IsRequired();
        ordem.Property(x => x.Observacao).HasMaxLength(2000);
        ordem.Property<uint>("Version").IsRowVersion();
        ordem.HasMany(x => x.Historico).WithOne().HasForeignKey(x => x.OrdemServicoId);
        ordem.Navigation(x => x.Historico).UsePropertyAccessMode(PropertyAccessMode.Field);
        var historico = modelBuilder.Entity<HistoricoOS>();
        historico.ToTable("historico_os");
        historico.HasKey(x => x.Id);
        historico.Property(x => x.Status).HasMaxLength(40).IsRequired();
        modelBuilder.Entity<MensagemPendente>().ToTable("mensagens_pendentes").HasKey(x => x.Id);
    }

    public async Task AdicionarAsync(OrdemServico ordem, CancellationToken cancellationToken) => await OrdensServico.AddAsync(ordem, cancellationToken);
    public Task<OrdemServico?> ObterAsync(Guid id, CancellationToken cancellationToken) => OrdensServico.Include(x => x.Historico).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    public async Task SalvarAsync(CancellationToken cancellationToken)
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<OrdemServico>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
        {
            var ordem = entry.Entity;
            var id = Guid.NewGuid();
            var tipo = entry.State == EntityState.Added ? "ordem-servico.criada.v1" : "ordem-servico.atualizada.v1";
            MensagensPendentes.Add(new MensagemPendente
            {
                Id = id,
                CriadoEm = DateTimeOffset.UtcNow,
                Conteudo = JsonSerializer.Serialize(new { eventId = id, tipo, id = ordem.Id, clienteId = ordem.ClienteId, veiculoId = ordem.VeiculoId, status = ordem.Status, observacao = ordem.Observacao })
            });
        }
        await SaveChangesAsync(cancellationToken);
    }
}

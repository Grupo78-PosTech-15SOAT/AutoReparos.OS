namespace AutoReparos.OS.Domain;

public sealed class OrdemServico
{
    private readonly List<HistoricoOS> _historico = [];

    public Guid Id { get; private set; }
    public Guid ClienteId { get; private set; }
    public Guid VeiculoId { get; private set; }
    public string? Observacao { get; private set; }
    public string Status { get; private set; } = "Recebida";
    public IReadOnlyCollection<HistoricoOS> Historico => _historico.AsReadOnly();

    private OrdemServico() { }

    public OrdemServico(Guid clienteId, Guid veiculoId, string? observacao, DateTimeOffset agora)
    {
        if (clienteId == Guid.Empty) throw new ArgumentException("Cliente é obrigatório.", nameof(clienteId));
        if (veiculoId == Guid.Empty) throw new ArgumentException("Veículo é obrigatório.", nameof(veiculoId));
        Id = Guid.NewGuid();
        ClienteId = clienteId;
        VeiculoId = veiculoId;
        Observacao = observacao;
        _historico.Add(new HistoricoOS(Guid.NewGuid(), Id, Status, agora));
    }

    public void AtualizarObservacao(string? observacao) => Observacao = observacao;
}

public sealed record HistoricoOS(Guid Id, Guid OrdemServicoId, string Status, DateTimeOffset RegistradoEm);

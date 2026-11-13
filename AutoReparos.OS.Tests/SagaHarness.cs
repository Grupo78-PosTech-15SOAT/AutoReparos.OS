using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using AutoReparos.OS.Application;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;
using AutoReparos.OS.Infrastructure;
using AutoReparos.OS.Infrastructure.Saga;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

/// <summary>Containers compartilhados pelos testes da Saga: DynamoDB Local e PostgreSQL reais.</summary>
public sealed class SagaAmbiente : IAsyncLifetime
{
    private readonly IContainer dynamo = new ContainerBuilder("amazon/dynamodb-local")
        .WithPortBinding(8000, true)
        .WithCommand("-jar", "DynamoDBLocal.jar", "-inMemory")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8000))
        .Build();
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string DynamoUrl => $"http://{dynamo.Hostname}:{dynamo.GetMappedPublicPort(8000)}";
    public string PostgresConnection => postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(dynamo.StartAsync(), postgres.StartAsync());
        var options = new DbContextOptionsBuilder<OrdemServicoDbContext>().UseNpgsql(PostgresConnection).Options;
        await using var context = new OrdemServicoDbContext(options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await dynamo.DisposeAsync();
        await postgres.DisposeAsync();
    }

    /// <summary>Cliente de teste para o DynamoDB Local (valores fictícios aceitos apenas pelo emulador).</summary>
    public IAmazonDynamoDB NovoClienteDynamo() =>
        new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"), new AmazonDynamoDBConfig { ServiceURL = DynamoUrl, AuthenticationRegion = "us-east-1" });
}

[CollectionDefinition("saga")]
public sealed class SagaCollection : ICollectionFixture<SagaAmbiente>;

public sealed class Relogio : TimeProvider
{
    public DateTimeOffset Agora { get; private set; } = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Agora;
    public void Avancar(TimeSpan intervalo) => Agora += intervalo;
}

public sealed class EnvioFalso : IEnvioComandos
{
    private readonly object trava = new();
    public List<(DestinoComando Destino, string Corpo)> Enviados { get; } = [];
    public List<(DestinoComando Destino, string Corpo)> Tentativas { get; } = [];
    public List<(string Corpo, string Motivo)> Dlq { get; } = [];
    public Func<DestinoComando, string, Exception?> Falha { get; set; } = (_, _) => null;
    public Func<Exception?> FalhaDlq { get; set; } = () => null;
    public Func<DestinoComando, string, Task>? AoEnviar { get; set; }

    public async Task EnviarAsync(DestinoComando destino, string corpo, CancellationToken cancellationToken)
    {
        lock (trava) Tentativas.Add((destino, corpo));
        if (AoEnviar is not null) await AoEnviar(destino, corpo);
        if (Falha(destino, corpo) is { } erro) throw erro;
        lock (trava) Enviados.Add((destino, corpo));
    }

    public Task EnviarParaDlqAsync(string corpo, string motivo, CancellationToken cancellationToken)
    {
        if (FalhaDlq() is { } erro) throw erro;
        lock (trava) Dlq.Add((corpo, motivo));
        return Task.CompletedTask;
    }

    public IEnumerable<string> TiposEnviados => Enviados.Select(e => Json.Tipo(e.Corpo));
}

public sealed record AlertaRegistrado(string Codigo, string Mensagem, Guid? OrdemServicoId, Guid? EventId);

public sealed class AlertadorFalso : IAlertador
{
    private readonly object trava = new();
    public List<AlertaRegistrado> Alertas { get; } = [];
    public void Alertar(string codigo, string mensagem, Guid? ordemServicoId = null, Guid? eventId = null)
    {
        lock (trava) Alertas.Add(new AlertaRegistrado(codigo, mensagem, ordemServicoId, eventId));
    }

    public IReadOnlyList<string> Codigos => Alertas.Select(a => a.Codigo).ToList();
}

/// <summary>Injeta falhas na sincronização da OS e registra cada chamada (status pedido).</summary>
public sealed class ControleSincronizacao
{
    public int FalhasRestantes;
    public Func<string, bool> AplicaA { get; set; } = _ => true;
    public List<string> Chamadas { get; } = [];
}

public sealed class RepositorioOSComFalha(OrdemServicoDbContext inner, ControleSincronizacao controle) : IOrdemServicoRepository
{
    public Task AdicionarAsync(OrdemServico ordem, CancellationToken cancellationToken) => inner.AdicionarAsync(ordem, cancellationToken);
    public Task<OrdemServico?> ObterAsync(Guid id, CancellationToken cancellationToken) => inner.ObterAsync(id, cancellationToken);
    public Task SalvarAsync(CancellationToken cancellationToken) => inner.SalvarAsync(cancellationToken);
    public Task<PaginaOS> ListarAsync(string? status, int pagina, int tamanho, CancellationToken cancellationToken) => inner.ListarAsync(status, pagina, tamanho, cancellationToken);

    public Task<ResultadoSincronizacao> SincronizarStatusAsync(Guid id, string status, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        lock (controle.Chamadas) controle.Chamadas.Add(status);
        if (controle.AplicaA(status) && Interlocked.Decrement(ref controle.FalhasRestantes) >= 0)
            throw new InvalidOperationException("Postgres indisponível (falha injetada).");
        return inner.SincronizarStatusAsync(id, status, agora, cancellationToken);
    }
}

/// <summary>Decorador que faz a leitura de deduplicação falhar N vezes (falha transitória do DynamoDB).</summary>
public sealed class SagaRepositorioInstavel(ISagaRepositorio inner) : ISagaRepositorio
{
    public int FalhasRestantes;
    public int Chamadas;
    /// <summary>Faz a leitura da Saga dessa OS falhar sempre (por exemplo, documento ilegível).</summary>
    public HashSet<Guid> LeituraComFalha { get; } = [];

    public Task<SagaOS?> ObterAsync(Guid ordemServicoId, CancellationToken cancellationToken) =>
        LeituraComFalha.Contains(ordemServicoId) ? throw new InvalidOperationException("Documento da Saga ilegível (falha injetada).") : inner.ObterAsync(ordemServicoId, cancellationToken);

    public Task<bool> EventoProcessadoAsync(Guid eventId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Chamadas);
        if (Interlocked.Decrement(ref FalhasRestantes) >= 0) throw new InvalidOperationException("DynamoDB indisponível (falha injetada).");
        return inner.EventoProcessadoAsync(eventId, cancellationToken);
    }

    public Task<ResultadoGravacao> GravarAsync(SagaOS saga, EventoProcessado? evento, CancellationToken cancellationToken) => inner.GravarAsync(saga, evento, cancellationToken);

    public Task<IReadOnlyList<SagaOS>> ListarAsync(EstadoSaga? estado, DateTimeOffset? atualizadaAte, CancellationToken cancellationToken) => inner.ListarAsync(estado, atualizadaAte, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListarComTrabalhoPendenteAsync(DateTimeOffset ate, CancellationToken cancellationToken) => inner.ListarComTrabalhoPendenteAsync(ate, cancellationToken);
}

public sealed record Componentes(
    EnvioFalso Envio,
    AlertadorFalso Alertas,
    ISagaRepositorio Sagas,
    OrquestradorSaga Orquestrador,
    ProcessadorMensagemSaga Processador,
    DespachadorSaga Despachador);

public sealed record SagaIniciada(Guid OrdemServicoId, Guid OrcamentoId, Guid ClienteId, Guid VeiculoId, Guid CorrelacaoId);

public static class Json
{
    public static string Tipo(string corpo) => Ler(corpo).GetProperty("tipo").GetString()!;

    public static JsonElement Ler(string corpo)
    {
        using var documento = JsonDocument.Parse(corpo);
        return documento.RootElement.Clone();
    }

    public static string[] Chaves(string corpo) => Ler(corpo).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
}

/// <summary>Base dos testes da Saga: tabelas DynamoDB exclusivas por teste, Postgres compartilhado, relógio e aleatoriedade fixos.</summary>
[Collection("saga")]
public abstract class SagaTestBase(SagaAmbiente ambiente) : IAsyncLifetime
{
    protected readonly Relogio Relogio = new();
    protected readonly ControleSincronizacao Controle = new();
    protected readonly List<TimeSpan> Esperas = [];
    protected IAmazonDynamoDB Dynamo = null!;
    protected OpcoesDynamoDb TabelasDynamo = null!;
    protected ServiceProvider Servicos = null!;
    protected Componentes Atual = null!;

    protected static readonly Guid SemCorrelacao = Guid.Empty;

    public async Task InitializeAsync()
    {
        Dynamo = ambiente.NovoClienteDynamo();
        var sufixo = Guid.NewGuid().ToString("N")[..12];
        TabelasDynamo = new OpcoesDynamoDb { TabelaSaga = $"saga-{sufixo}", TabelaEventos = $"eventos-{sufixo}" };
        await new DynamoDbInicializador(Dynamo, TabelasDynamo).GarantirTabelasAsync(CancellationToken.None);
        Servicos = new ServiceCollection()
            .AddDbContext<OrdemServicoDbContext>(o => o.UseNpgsql(ambiente.PostgresConnection))
            .AddScoped<IOrdemServicoRepository>(sp => new RepositorioOSComFalha(sp.GetRequiredService<OrdemServicoDbContext>(), Controle))
            .BuildServiceProvider();
        Atual = NovosComponentes();
    }

    public async Task DisposeAsync()
    {
        await Servicos.DisposeAsync();
        Dynamo.Dispose();
    }

    /// <summary>Instâncias novas sobre as mesmas tabelas: simula o reinício do processo.</summary>
    protected Componentes NovosComponentes(Func<ISagaRepositorio, ISagaRepositorio>? decorar = null, EnvioFalso? envio = null, TimeSpan? prazoCompensacao = null, int maxReenvios = 0, TimeSpan? prazoReserva = null, TimeSpan? prazoLiberacao = null)
    {
        envio ??= new EnvioFalso();
        var alertas = new AlertadorFalso();
        var opcoes = new OpcoesSaga
        {
            Aleatorio = () => 0.5,
            Aguardar = (atraso, _) => { lock (Esperas) Esperas.Add(atraso); return Task.CompletedTask; },
            PrazoCompensacao = prazoCompensacao ?? TimeSpan.FromMinutes(15),
            MaxReenvios = maxReenvios,
            PrazoReserva = prazoReserva ?? TimeSpan.FromMinutes(2),
            PrazoLiberacao = prazoLiberacao ?? TimeSpan.FromMinutes(2)
        };
        ISagaRepositorio sagas = new DynamoDbSagaRepositorio(Dynamo, TabelasDynamo, Relogio);
        if (decorar is not null) sagas = decorar(sagas);
        var sincronizador = new SincronizadorOrdemServico(Servicos.GetRequiredService<IServiceScopeFactory>());
        var orquestrador = new OrquestradorSaga(sagas, sincronizador, envio, alertas, Relogio, opcoes);
        return new Componentes(envio, alertas, sagas, orquestrador, new ProcessadorMensagemSaga(orquestrador, envio, alertas, opcoes), new DespachadorSaga(sagas, orquestrador, alertas, Relogio));
    }

    protected async Task<(Guid Id, Guid ClienteId, Guid VeiculoId)> CriarOSAsync()
    {
        using var scope = Servicos.CreateScope();
        var contexto = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        var ordem = new OrdemServico(Guid.NewGuid(), Guid.NewGuid(), "teste da saga", Relogio.Agora);
        await contexto.AdicionarAsync(ordem, CancellationToken.None);
        await contexto.SalvarAsync(CancellationToken.None);
        return (ordem.Id, ordem.ClienteId, ordem.VeiculoId);
    }

    protected (string Corpo, Guid EventId) Mensagem(string tipo, Guid ordemServicoId, Guid? correlacaoId = null, Guid? eventId = null, params (string Chave, object? Valor)[] extras)
    {
        var id = eventId ?? Guid.NewGuid();
        var campos = new Dictionary<string, object?> { ["eventId"] = id, ["tipo"] = tipo, ["ordemServicoId"] = ordemServicoId, ["ocorreuEm"] = "2026-11-10T11:59:00Z" };
        if (correlacaoId is not null) campos["correlacaoId"] = correlacaoId;
        foreach (var (chave, valor) in extras) campos[chave] = valor;
        return (JsonSerializer.Serialize(campos), id);
    }

    protected async Task<ResultadoProcessamento> ReceberAsync(string tipo, Guid ordemServicoId, Componentes? em = null, Guid? eventId = null, params (string Chave, object? Valor)[] extras)
    {
        var (corpo, _) = Mensagem(tipo, ordemServicoId, null, eventId, extras);
        return await (em ?? Atual).Processador.ProcessarAsync(corpo, CancellationToken.None);
    }

    /// <summary>Cria a OS e entrega <c>orcamento.aprovado.v1</c>; com tudo saudável a Saga chega a ExecucaoLiberada e o comando é publicado.</summary>
    protected async Task<SagaIniciada> IniciarSagaAsync(Componentes? em = null, Guid? correlacaoId = null)
    {
        var (id, clienteId, veiculoId) = await CriarOSAsync();
        var orcamentoId = Guid.NewGuid();
        var correlacao = correlacaoId ?? Guid.NewGuid();
        var (corpo, _) = Mensagem(TiposMensagem.OrcamentoAprovado, id, correlacao, null, ("orcamentoId", orcamentoId), ("clienteId", Guid.NewGuid()), ("itens", Array.Empty<object>()));
        var resultado = await (em ?? Atual).Processador.ProcessarAsync(corpo, CancellationToken.None);
        Assert.Equal(DesfechoMensagem.Processada, resultado.Desfecho);
        return new SagaIniciada(id, orcamentoId, clienteId, veiculoId, correlacao);
    }

    protected async Task<SagaOS> SagaAsync(Guid ordemServicoId, Componentes? em = null) =>
        (await (em ?? Atual).Sagas.ObterAsync(ordemServicoId, CancellationToken.None))!;

    protected async Task<(string Status, string[] Historico)> OSAsync(Guid id)
    {
        using var scope = Servicos.CreateScope();
        var ordem = await scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>().OrdensServico.AsNoTracking().Include(o => o.Historico).SingleAsync(o => o.Id == id);
        return (ordem.Status, ordem.Historico.Select(h => h.Status).Order(StringComparer.Ordinal).ToArray());
    }

    protected async Task AssertOSAsync(Guid id, string status, params string[] historicoOrdenado)
    {
        var (atual, historico) = await OSAsync(id);
        Assert.Equal(status, atual);
        Assert.Equal(historicoOrdenado, historico);
    }

    protected async Task<int> EventosDeOSAsync(Guid id)
    {
        using var scope = Servicos.CreateScope();
        var contexto = scope.ServiceProvider.GetRequiredService<OrdemServicoDbContext>();
        return await contexto.MensagensPendentes.CountAsync(m => m.Conteudo.Contains(id.ToString()) && m.Conteudo.Contains("ordem-servico.atualizada.v1"));
    }

    protected async Task<Dictionary<string, AttributeValue>> ItemBrutoAsync(Guid ordemServicoId) =>
        (await Dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = TabelasDynamo.TabelaSaga,
            ConsistentRead = true,
            Key = new Dictionary<string, AttributeValue> { ["ordemServicoId"] = new() { S = ordemServicoId.ToString() } }
        })).Item;

    protected static string[] Sincronizacoes(SagaOS saga) =>
        saga.Registros.Where(r => r.Evento == "os.sincronizada").Select(r => r.Descricao).ToArray();
}

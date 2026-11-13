using AutoReparos.OS.Application;
using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;
using AutoReparos.OS.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Amazon.SQS;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var serviceName = builder.Configuration["Telemetry:ServiceName"] ?? "autoreparos-os";
var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"] ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName)
        .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint)));
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;
    logging.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
});
builder.Services.AddOpenApi();
builder.Services.AddDbContext<OrdemServicoDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("OrdemServico")));
builder.Services.AddScoped<IOrdemServicoRepository>(services => services.GetRequiredService<OrdemServicoDbContext>());
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:Authority"];
    options.Audience = builder.Configuration["Authentication:Audience"];
    options.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("OperarOS", policy => policy.RequireRole("Administrador", "Atendente", "Mecanico", "ServicoOS"));
    options.AddPolicy("ListarOS", policy => policy.RequireRole("Administrador", "Atendente", "Mecanico"));
    options.AddPolicy("CancelarOS", policy => policy.RequireRole("Administrador", "Atendente"));
    options.AddPolicy("AdministrarSagas", policy => policy.RequireRole("Administrador"));
});
builder.Services.Configure<OpcoesCadastros>(builder.Configuration.GetSection(OpcoesCadastros.Secao));
builder.Services.AddHttpClient<ICadastros, CadastrosHttp>(cliente => cliente.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<IAmazonSQS, AmazonSQSClient>();
builder.Services.AddHostedService<PublicadorSqs>();
builder.Services.AddSagaOS(builder.Configuration);
var app = builder.Build();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
var ordens = app.MapGroup("/api/v1/ordens-servico").RequireAuthorization("OperarOS");
ordens.MapPost("/", async (HttpRequest http, IOrdemServicoRepository repository, ICadastros cadastros, CancellationToken ct) =>
{
    var corpo = await CorpoJson.LerObjetoAsync(http, ct);
    if (corpo is null
        || !CorpoJson.TentarLerGuid(corpo.Value, "clienteId", out var clienteId) || clienteId == Guid.Empty
        || !CorpoJson.TentarLerGuid(corpo.Value, "veiculoId", out var veiculoId) || veiculoId == Guid.Empty
        || !CorpoJson.TentarLerObservacao(corpo.Value, out _, out var observacao))
        return CorpoJson.Invalido();
    // Cliente e veículo são do Cadastros: o JWT de quem abre a OS é repassado e 404 impede a abertura.
    // Contas de serviço (ServicoOS) não têm papel de oficina no Cadastros: sem usuário humano, usam a chave interna.
    var autorizacao = http.HttpContext.User.IsInRole("ServicoOS") ? string.Empty : http.Headers.Authorization.ToString();
    ResultadoValidacaoCadastro validacao;
    try
    {
        validacao = await cadastros.ValidarAsync(clienteId, veiculoId, string.IsNullOrWhiteSpace(autorizacao) ? null : autorizacao, ct);
    }
    catch (CadastrosIndisponivelException)
    {
        return Results.Json(new { codigo = "cadastros_indisponivel" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    switch (validacao)
    {
        case ResultadoValidacaoCadastro.ClienteInexistente: return Results.NotFound(new { codigo = "cliente_nao_encontrado" });
        case ResultadoValidacaoCadastro.VeiculoInexistente: return Results.NotFound(new { codigo = "veiculo_nao_encontrado" });
        case ResultadoValidacaoCadastro.VeiculoDeOutroCliente: return Results.UnprocessableEntity(new { codigo = "veiculo_de_outro_cliente" });
    }
    var ordem = new OrdemServico(clienteId, veiculoId, observacao, DateTimeOffset.UtcNow);
    await repository.AdicionarAsync(ordem, ct);
    await repository.SalvarAsync(ct);
    return Results.Created($"/api/v1/ordens-servico/{ordem.Id}", new { ordem.Id, ordem.Status });
});
ordens.MapGet("/", async (string? status, string? pagina, string? tamanho, IOrdemServicoRepository repository, CancellationToken ct) =>
{
    var filtro = string.IsNullOrEmpty(status) ? null : status;
    if ((filtro is not null && !StatusOS.Todos.Contains(filtro))
        || !CorpoJson.TentarLerPagina(pagina, 1, 1_000_000, 1, out var numeroPagina)
        || !CorpoJson.TentarLerPagina(tamanho, 1, 100, 20, out var tamanhoPagina))
        return Results.UnprocessableEntity(new { codigo = "filtro_invalido" });
    var resultado = await repository.ListarAsync(filtro, numeroPagina, tamanhoPagina, ct);
    return Results.Ok(new { resultado.Total, Itens = resultado.Itens.Select(i => new { i.Id, i.ClienteId, i.VeiculoId, i.Status, i.AtualizadaEm }) });
}).RequireAuthorization("ListarOS");
ordens.MapGet("/{id:guid}", async (Guid id, IOrdemServicoRepository repository, CancellationToken ct) =>
{
    var ordem = await repository.ObterAsync(id, ct);
    return ordem is null ? Results.NotFound() : Results.Ok(new { ordem.Id, ordem.ClienteId, ordem.VeiculoId, ordem.Observacao, ordem.Status, Historico = ordem.Historico.OrderBy(h => h.RegistradoEm).ThenBy(h => h.Id) });
});
ordens.MapPatch("/{id:guid}", async (Guid id, HttpRequest http, IOrdemServicoRepository repository, CancellationToken ct) =>
{
    var corpo = await CorpoJson.LerObjetoAsync(http, ct);
    if (corpo is null || !CorpoJson.TentarLerObservacao(corpo.Value, out var presente, out var observacao))
        return CorpoJson.Invalido();
    var ordem = await repository.ObterAsync(id, ct);
    if (ordem is null) return Results.NotFound();
    if (presente)
    {
        ordem.AtualizarObservacao(observacao);
        try
        {
            await repository.SalvarAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { codigo = "ordem_servico_conflito" });
        }
    }
    return Results.Ok(new { ordem.Id, ordem.Status, ordem.Observacao });
});
ordens.MapPost("/{id:guid}/cancelamento", async (Guid id, HttpRequest http, CancelamentoOrdemServico cancelamento, CancellationToken ct) =>
{
    var corpo = await CorpoJson.LerObjetoAsync(http, ct);
    if (corpo is null || !CorpoJson.TentarLerMotivo(corpo.Value, out var motivo)) return CorpoJson.Invalido();
    var resposta = await cancelamento.CancelarAsync(id, motivo, ct);
    return resposta.Resultado switch
    {
        ResultadoCancelamento.NaoEncontrada => Results.NotFound(),
        ResultadoCancelamento.Indisponivel => Results.Conflict(new { codigo = "cancelamento_indisponivel" }),
        ResultadoCancelamento.EmAndamento => Results.Conflict(new { codigo = "saga_em_andamento" }),
        _ => Results.Accepted(value: new { ordemServicoId = resposta.OrdemServicoId, correlacaoId = resposta.CorrelacaoId, estado = resposta.Estado })
    };
}).RequireAuthorization("CancelarOS");
// Consulta pública: só status e histórico, sem cliente nem veículo; o id imprevisível (GUID) é o único controle de acesso.
app.MapGet("/api/v1/ordens-servico/{id:guid}/consulta", async (Guid id, IOrdemServicoRepository repository, CancellationToken ct) =>
{
    var ordem = await repository.ObterAsync(id, ct);
    return ordem is null ? Results.NotFound() : Results.Ok(new { ordem.Id, ordem.Status, Historico = ordem.Historico.OrderBy(h => h.RegistradoEm).ThenBy(h => h.Id).Select(h => new { h.Status, Em = h.RegistradoEm }) });
}).AllowAnonymous();
var admin = app.MapGroup("/api/v1/admin/sagas").RequireAuthorization("AdministrarSagas");
admin.MapGet("/", async (string? estado, int? paradaHaMinutos, ConsoleSagas console, CancellationToken ct) =>
{
    if (estado is not null && (!Enum.TryParse<EstadoSaga>(estado, ignoreCase: false, out var estadoLido) || !Enum.IsDefined(estadoLido) || int.TryParse(estado, out _)) || paradaHaMinutos is < 0) return CorpoJson.Invalido();
    var lista = await console.ListarAsync(estado is null ? null : Enum.Parse<EstadoSaga>(estado), paradaHaMinutos, ct);
    return Results.Ok(lista.Select(s => new { s.OrdemServicoId, estado = s.Estado.ToString(), s.AtualizadoEm, s.OrcamentoId }));
});
admin.MapGet("/{ordemServicoId:guid}", async (Guid ordemServicoId, ConsoleSagas console, CancellationToken ct) =>
{
    var saga = await console.ObterAsync(ordemServicoId, ct);
    return saga is null ? Results.NotFound() : Results.Ok(new
    {
        saga.OrdemServicoId,
        saga.CorrelacaoId,
        saga.OrcamentoId,
        estado = saga.Estado.ToString(),
        saga.MotivoCompensacao,
        saga.AtualizadoEm,
        saga.Itens,
        etapas = saga.Etapas.Select(e => new { e.Numero, e.Nome, situacao = e.Situacao.ToString(), e.ResultadoCompensacao, e.ErroCompensacao }),
        comandos = saga.Comandos.Select(c => new { c.Tipo, destino = c.Destino.ToString(), c.EnviadoEm, c.Tentativas, c.UltimoErro }),
        saga.Registros
    });
});
admin.MapPost("/{ordemServicoId:guid}/retentar", async (Guid ordemServicoId, HttpContext http, ConsoleSagas console, CancellationToken ct) =>
{
    var corpo = await CorpoJson.LerObjetoAsync(http.Request, ct);
    if (corpo is null || !CorpoJson.TentarLerMotivo(corpo.Value, out var motivo)) return CorpoJson.Invalido();
    var autor = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? http.User.Identity?.Name ?? "desconhecido";
    return await console.RetentarAsync(ordemServicoId, autor, motivo, ct) switch
    {
        ResultadoRetentativa.NaoEncontrada => Results.NotFound(),
        ResultadoRetentativa.EmAndamento => Results.Conflict(new { codigo = "saga_em_andamento" }),
        ResultadoRetentativa.Indisponivel => Results.Conflict(new { codigo = "retentativa_indisponivel" }),
        _ => Results.Accepted(value: new { ordemServicoId, passo = "reserva-de-insumos" })
    };
});
app.MapPost("/api/v1/admin/dlq/reprocessar", async (HttpContext http, ReprocessadorDlq reprocessador, CancellationToken ct) =>
{
    var corpo = await CorpoJson.LerObjetoAsync(http.Request, ct);
    if (corpo is null || !CorpoJson.TentarLerQuantidade(corpo.Value, ReprocessadorDlq.QuantidadeMaxima, out var quantidade)) return Results.UnprocessableEntity(new { codigo = "quantidade_invalida" });
    var autor = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? http.User.Identity?.Name ?? "desconhecido";
    return Results.Accepted(value: await reprocessador.ReprocessarAsync(quantidade, autor, ct));
}).RequireAuthorization("AdministrarSagas");
app.Run();
public partial class Program;

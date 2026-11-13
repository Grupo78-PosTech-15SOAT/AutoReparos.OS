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
    options.AddPolicy("CancelarOS", policy => policy.RequireRole("Administrador", "Atendente"));
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
        _ => Results.Accepted(value: new { ordemServicoId = resposta.OrdemServicoId, correlacaoId = resposta.CorrelacaoId, estado = resposta.Estado })
    };
}).RequireAuthorization("CancelarOS");
app.Run();
public partial class Program;

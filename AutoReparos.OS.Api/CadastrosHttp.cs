using System.Net;
using System.Text.Json;
using AutoReparos.OS.Application;

/// <summary>Configuração do Cadastros (<c>Cadastros:BaseUrl</c> e <c>Cadastros:ChaveInterna</c>, esta vinda de um Secret do K8s).</summary>
public sealed class OpcoesCadastros
{
    public const string Secao = "Cadastros";
    public string BaseUrl { get; set; } = string.Empty;
    public string ChaveInterna { get; set; } = string.Empty;
}

public sealed class CadastrosHttp(HttpClient http, Microsoft.Extensions.Options.IOptions<OpcoesCadastros> opcoes) : ICadastros
{
    private const string CabecalhoChaveInterna = "X-Chave-Interna";

    public async Task<ResultadoValidacaoCadastro> ValidarAsync(Guid clienteId, Guid veiculoId, string? authorization, CancellationToken cancellationToken)
    {
        if (await ObterAsync($"/api/v1/clientes/{clienteId}", authorization, cancellationToken) is null)
            return ResultadoValidacaoCadastro.ClienteInexistente;

        var veiculo = await ObterAsync($"/api/v1/veiculos/{veiculoId}", authorization, cancellationToken);
        if (veiculo is null)
            return ResultadoValidacaoCadastro.VeiculoInexistente;

        return veiculo.Value.TryGetProperty("clienteId", out var dono) && dono.TryGetGuid(out var donoId) && donoId == clienteId
            ? ResultadoValidacaoCadastro.Valido
            : ResultadoValidacaoCadastro.VeiculoDeOutroCliente;
    }

    private async Task<JsonElement?> ObterAsync(string caminho, string? authorization, CancellationToken cancellationToken)
    {
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(opcoes.Value.BaseUrl.TrimEnd('/') + "/"), caminho.TrimStart('/')));
        if (!string.IsNullOrWhiteSpace(authorization))
            requisicao.Headers.TryAddWithoutValidation("Authorization", authorization);
        else
            requisicao.Headers.TryAddWithoutValidation(CabecalhoChaveInterna, opcoes.Value.ChaveInterna);

        HttpResponseMessage resposta;
        try
        {
            resposta = await http.SendAsync(requisicao, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new CadastrosIndisponivelException("Falha de comunicação com o Cadastros.", ex);
        }

        using (resposta)
        {
            if (resposta.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resposta.IsSuccessStatusCode)
                throw new CadastrosIndisponivelException($"O Cadastros respondeu {(int)resposta.StatusCode} a {caminho}.");

            using var documento = await JsonDocument.ParseAsync(await resposta.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return documento.RootElement.Clone();
        }
    }
}

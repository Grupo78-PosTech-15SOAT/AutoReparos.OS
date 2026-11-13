using System.Text.Json;
using AutoReparos.OS.Application.Saga;

internal static class CorpoJson
{
    public const int LimiteObservacao = 2000;

    public static IResult Invalido() => Results.UnprocessableEntity(new { codigo = "ordem_servico_invalida" });

    public static async Task<JsonElement?> LerObjetoAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType()) return null;
        try
        {
            using var documento = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            return documento.RootElement.ValueKind == JsonValueKind.Object ? documento.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TentarLerGuid(JsonElement corpo, string nome, out Guid valor)
    {
        valor = Guid.Empty;
        return Propriedade(corpo, nome, out var elemento)
            && elemento.ValueKind == JsonValueKind.String
            && TentarLerTexto(elemento, out var texto)
            && Guid.TryParse(texto, out valor);
    }

    // GetString lança InvalidOperationException para surrogate isolado em strings JSON.
    private static bool TentarLerTexto(JsonElement elemento, out string? texto)
    {
        try
        {
            texto = elemento.GetString();
            return true;
        }
        catch (InvalidOperationException)
        {
            texto = null;
            return false;
        }
    }

    public static bool TentarLerObservacao(JsonElement corpo, out bool presente, out string? valor)
    {
        valor = null;
        presente = Propriedade(corpo, "observacao", out var elemento);
        if (!presente || elemento.ValueKind == JsonValueKind.Null) return true;
        if (elemento.ValueKind != JsonValueKind.String) return false;
        if (!TentarLerTexto(elemento, out valor)) return false;
        // O PostgreSQL rejeita o caractere NUL em text (erro 22021).
        return valor is null || (valor.Length <= LimiteObservacao && !valor.Contains((char)0));
    }

    /// <summary>O motivo do cancelamento é obrigatório: não vazio, até 2000 caracteres, sem NUL nem surrogate isolado.</summary>
    public static bool TentarLerMotivo(JsonElement corpo, out string motivo)
    {
        motivo = string.Empty;
        if (!Propriedade(corpo, "motivo", out var elemento) || elemento.ValueKind != JsonValueKind.String) return false;
        if (!TentarLerTexto(elemento, out var texto) || !MensagemSaga.MotivoValido(texto)) return false;
        motivo = texto!;
        return true;
    }

    /// <summary>Quantidade inteira entre 1 e <paramref name="maximo"/>.</summary>
    public static bool TentarLerQuantidade(JsonElement corpo, int maximo, out int quantidade)
    {
        quantidade = 0;
        return Propriedade(corpo, "quantidade", out var elemento) && elemento.ValueKind == JsonValueKind.Number
            && elemento.TryGetInt32(out quantidade) && quantidade >= 1 && quantidade <= maximo;
    }

    /// <summary>Inteiro da query string entre <paramref name="minimo"/> e <paramref name="maximo"/>; ausente vale <paramref name="padrao"/>.</summary>
    public static bool TentarLerPagina(string? texto, int minimo, int maximo, int padrao, out int valor)
    {
        valor = padrao;
        if (string.IsNullOrEmpty(texto)) return true;
        return int.TryParse(texto, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out valor) && valor >= minimo && valor <= maximo;
    }

    private static bool Propriedade(JsonElement corpo, string nome, out JsonElement elemento)
    {
        foreach (var propriedade in corpo.EnumerateObject())
        {
            if (!string.Equals(propriedade.Name, nome, StringComparison.OrdinalIgnoreCase)) continue;
            elemento = propriedade.Value;
            return true;
        }
        elemento = default;
        return false;
    }
}

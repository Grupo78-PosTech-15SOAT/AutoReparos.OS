using System.Text.Json;

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
            && Guid.TryParse(elemento.GetString(), out valor);
    }

    public static bool TentarLerObservacao(JsonElement corpo, out bool presente, out string? valor)
    {
        valor = null;
        presente = Propriedade(corpo, "observacao", out var elemento);
        if (!presente || elemento.ValueKind == JsonValueKind.Null) return true;
        if (elemento.ValueKind != JsonValueKind.String) return false;
        valor = elemento.GetString();
        return valor is null || valor.Length <= LimiteObservacao;
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

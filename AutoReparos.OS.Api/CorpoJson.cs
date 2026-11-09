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

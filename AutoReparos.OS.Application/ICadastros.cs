namespace AutoReparos.OS.Application;

public enum ResultadoValidacaoCadastro
{
    Valido,
    ClienteInexistente,
    VeiculoInexistente,
    VeiculoDeOutroCliente
}

/// <summary>O Cadastros não respondeu de forma utilizável (rede, 5xx, 401 ou 403); a OS não deve ser aberta.</summary>
public sealed class CadastrosIndisponivelException(string mensagem, Exception? interna = null) : Exception(mensagem, interna);

/// <summary>Consulta REST do OS ao Cadastros, dono de clientes e veículos.</summary>
public interface ICadastros
{
    /// <summary>
    /// Confere que o cliente e o veículo existem e que o veículo pertence ao cliente. Com <paramref name="authorization"/>
    /// (cabeçalho <c>Authorization</c> do usuário que abre a OS) repassa o JWT do usuário; sem ele (origem em evento) usa a chave interna.
    /// </summary>
    /// <exception cref="CadastrosIndisponivelException">O Cadastros não respondeu de forma utilizável.</exception>
    Task<ResultadoValidacaoCadastro> ValidarAsync(Guid clienteId, Guid veiculoId, string? authorization, CancellationToken cancellationToken);
}

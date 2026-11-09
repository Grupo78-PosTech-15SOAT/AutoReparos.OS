namespace AutoReparos.OS.Infrastructure;

public sealed class MensagemPendente
{
    public Guid Id { get; set; }
    public string Conteudo { get; set; } = string.Empty;
    public DateTimeOffset CriadoEm { get; set; }
    public DateTimeOffset? EnviadoEm { get; set; }
    public int Tentativas { get; set; }
    public DateTimeOffset? ProximaTentativaEm { get; set; }
    public bool EncaminhadoParaDlq { get; set; }
}

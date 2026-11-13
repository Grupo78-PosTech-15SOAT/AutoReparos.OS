using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoReparos.OS.Domain;

/// <summary>
/// Estado orquestrado da Saga de uma OS. Um item por <c>ordemServicoId</c>. Todas as mudanças passam por métodos que
/// acumulam, no próprio objeto, os comandos a publicar e as sincronizações da OS pendentes; a persistência grava tudo
/// de uma vez (escrita condicional por versão), garantindo persistir antes de publicar.
/// </summary>
/// <summary>Item da reserva: vem do <c>orcamento.aprovado.v1</c> e segue no comando <c>execucao.reservar-insumos.v1</c> (D-32).</summary>
public sealed record ItemReservaSaga(Guid InsumoId, int Quantidade);

public sealed class SagaOS
{
    /// <summary>Intervalo entre os alertas enquanto a Saga aguarda insumos (D-20).</summary>
    public static readonly TimeSpan IntervaloAlertaInsumos = TimeSpan.FromDays(1);

    /// <summary>Intervalo entre os lembretes ao cliente enquanto a Saga aguarda o pagamento (prazo de negócio: nunca expira nem cancela).</summary>
    public static readonly TimeSpan IntervaloLembretePagamento = TimeSpan.FromHours(24);

    /// <summary>Limite de caracteres do motivo (o Execution rejeita mais que isto); comandos de compensação o respeitam.</summary>
    public const int MotivoMaximo = 2000;

    private static readonly JsonSerializerOptions JsonComando = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [JsonInclude] public Guid OrdemServicoId { get; private set; }
    [JsonInclude] public Guid CorrelacaoId { get; private set; }
    [JsonInclude] public Guid OrcamentoId { get; private set; }
    [JsonInclude] public EstadoSaga Estado { get; private set; }
    [JsonInclude] public DateTimeOffset CriadoEm { get; private set; }
    [JsonInclude] public DateTimeOffset AtualizadoEm { get; private set; }
    [JsonInclude] public string? MotivoCompensacao { get; private set; }
    /// <summary>Id da execução informado pelo Execution (concluída ou falhou); as confirmações devem coincidir.</summary>
    [JsonInclude] public Guid? ExecucaoId { get; private set; }
    /// <summary>Quando o comando de compensação em andamento foi emitido (reinicia a cada etapa compensada).</summary>
    [JsonInclude] public DateTimeOffset? CompensacaoEmitidaEm { get; private set; }
    /// <summary>Prazo para a confirmação da compensação em andamento; vencido, a Saga vai a <c>CompensacaoFalhou</c>.</summary>
    [JsonInclude] public DateTimeOffset? PrazoCompensacaoEm { get; private set; }
    [JsonInclude] public List<EtapaSaga> Etapas { get; private set; } = [];
    [JsonInclude] public List<ComandoSaga> Comandos { get; private set; } = [];
    /// <summary>Status da OS ainda não sincronizados no Postgres, em ordem. A sincronização nunca desfaz pagamento.</summary>
    [JsonInclude] public List<string> SincronizacoesPendentes { get; private set; } = [];
    [JsonInclude] public int TentativasSincronizacao { get; private set; }
    [JsonInclude] public DateTimeOffset? ProximaSincronizacaoEm { get; private set; }
    [JsonInclude] public string? UltimoErroSincronizacao { get; private set; }
    [JsonInclude] public List<RegistroSaga> Registros { get; private set; } = [];
    /// <summary>Insumos do orçamento aprovado; vazio quando o orçamento só tem serviços (nada a reservar).</summary>
    [JsonInclude] public List<ItemReservaSaga> Itens { get; private set; } = [];
    [JsonInclude] public Guid ClienteId { get; private set; }
    [JsonInclude] public Guid VeiculoId { get; private set; }
    /// <summary>Próximo alerta diário <c>saga.aguardando-insumos</c>; nulo fora de <c>AguardandoInsumos</c>.</summary>
    [JsonInclude] public DateTimeOffset? ProximoAlertaInsumosEm { get; private set; }
    /// <summary>Início do passo que espera resposta (reserva) ou entrega (liberação); reinicia a cada reenvio.</summary>
    [JsonInclude] public DateTimeOffset? PassoIniciadoEm { get; private set; }
    [JsonInclude] public int ReenviosPasso { get; private set; }
    /// <summary>Prazo técnico do passo em andamento (<c>PrazoReserva</c> ou <c>PrazoLiberacao</c>); nulo quando nenhum passo espera.</summary>
    [JsonInclude] public DateTimeOffset? PrazoPassoEm { get; private set; }
    [JsonInclude] public int ReenviosCompensacao { get; private set; }
    /// <summary>Próximo lembrete de pagamento; só vale em <c>ExecucaoConcluida</c> (aguardando pagamento).</summary>
    [JsonInclude] public DateTimeOffset? ProximoLembretePagamentoEm { get; private set; }

    /// <summary>Versão persistida (0 = ainda não gravada). Controlada pela persistência.</summary>
    [JsonIgnore] public long Versao { get; private set; }

    [JsonIgnore] public bool Terminal => Estado is EstadoSaga.PagamentoCapturado or EstadoSaga.Compensada or EstadoSaga.CompensacaoFalhou;
    [JsonIgnore] public bool AguardaPrazoCompensacao => Estado == EstadoSaga.Compensando && PrazoCompensacaoEm is not null;
    [JsonIgnore] public bool AguardaAlertaInsumos => Estado == EstadoSaga.AguardandoInsumos && ProximoAlertaInsumosEm is not null;
    [JsonIgnore] public bool AguardaLembretePagamento => Estado == EstadoSaga.ExecucaoConcluida && ProximoLembretePagamentoEm is not null;
    [JsonIgnore] public bool AguardaPrazoPasso => PrazoPassoEm is not null;
    [JsonIgnore] public bool TemTrabalhoPendente => SincronizacoesPendentes.Count > 0 || Comandos.Any(c => c.Pendente) || AguardaPrazoCompensacao || AguardaPrazoPasso || AguardaAlertaInsumos || AguardaLembretePagamento;

    [JsonConstructor]
    private SagaOS() { }

    public static SagaOS Iniciar(Guid ordemServicoId, Guid orcamentoId, Guid? correlacaoId, Guid eventId, DateTimeOffset agora, IReadOnlyList<ItemReservaSaga>? itens = null)
    {
        var saga = new SagaOS
        {
            OrdemServicoId = ordemServicoId,
            OrcamentoId = orcamentoId,
            CorrelacaoId = correlacaoId ?? Guid.NewGuid(),
            Estado = EstadoSaga.OrcamentoAprovado,
            Itens = [.. itens ?? []],
            CriadoEm = agora,
            AtualizadoEm = agora,
            Etapas =
            [
                new EtapaSaga(1, "Orçamento aprovado", TiposMensagem.CobrancaCancelarOrcamento),
                new EtapaSaga(2, "Execução liberada", TiposMensagem.ExecucaoCancelar),
                new EtapaSaga(3, "Execução concluída", null),
                new EtapaSaga(4, "Pagamento capturado", null)
            ]
        };
        // A etapa da reserva só existe quando o orçamento tem insumos: sem insumos a Saga mantém as quatro etapas de antes.
        if (saga.Itens.Count > 0) saga.Etapas.Add(new EtapaSaga(5, "Insumos reservados", TiposMensagem.ExecucaoCancelarReserva));
        saga.Etapa(1).Marcar(SituacaoEtapa.Concluida, agora);
        saga.SincronizacoesPendentes.Add(StatusOS.OrcamentoAprovado);
        saga.Registrar(agora, TiposMensagem.OrcamentoAprovado, eventId, "Saga criada; orçamento aprovado.");
        return saga;
    }

    /// <summary>
    /// <c>orcamento.aprovado.v1</c> que chega com a OS já <c>Cancelada</c> (cancelada pela rota enquanto não havia Saga): a Saga
    /// nasce equivalente a um <c>ordem-servico.cancelar.v1</c> imediato. A OS não é sincronizada para <c>OrcamentoAprovado</c>
    /// (não é reativada) e a etapa 1 é compensada de imediato com <c>billing.orcamento.cancelar.v1</c>.
    /// </summary>
    public static SagaOS IniciarJaCancelada(Guid ordemServicoId, Guid orcamentoId, Guid? correlacaoId, Guid eventId, DateTimeOffset agora, IReadOnlyList<ItemReservaSaga>? itens = null)
    {
        var saga = Iniciar(ordemServicoId, orcamentoId, correlacaoId, eventId, agora, itens);
        saga.SincronizacoesPendentes.Clear();
        const string motivo = "orçamento aprovado após o cancelamento da OS";
        saga.Registrar(agora, TiposMensagem.OrdemServicoCancelar, eventId, $"Cancelamento aplicado na criação da Saga: {motivo}.");
        saga.IniciarCompensacao(agora, motivo);
        return saga;
    }

    public void AplicarVersao(long versao) => Versao = versao;

    public EtapaSaga Etapa(int numero) => Etapas.Single(e => e.Numero == numero);

    // ---- Mensagens de entrada ----

    public Transicao AplicarOrcamentoAprovadoRepetido(Guid eventId, DateTimeOffset agora) =>
        Rejeitar(agora, TiposMensagem.OrcamentoAprovado, eventId, "Saga já existe para a OS.");

    /// <summary><c>insumos.reservados.v1</c>: a reserva existe, então a etapa 5 passa a ter o que compensar e a execução é liberada.</summary>
    public Transicao AplicarInsumosReservados(Guid eventId, DateTimeOffset agora)
    {
        if (Estado != EstadoSaga.ReservandoInsumos)
            return Rejeitar(agora, TiposMensagem.InsumosReservados, eventId, $"Transição inválida a partir de {Estado}.");
        Etapa(5).Marcar(SituacaoEtapa.Concluida, agora);
        Registrar(agora, TiposMensagem.InsumosReservados, eventId, "Insumos reservados.");
        LiberarExecucao(agora);
        return Transicao.Ok;
    }

    /// <summary><c>insumos.reserva-falhou.v1</c> (D-20): nada foi reservado; a Saga espera saldo sem compensar e alerta já e depois a cada dia.</summary>
    public Transicao AplicarReservaFalhou(Guid eventId, string? motivo, DateTimeOffset agora)
    {
        if (Estado != EstadoSaga.ReservandoInsumos)
            return Rejeitar(agora, TiposMensagem.InsumosReservaFalhou, eventId, $"Transição inválida a partir de {Estado}.");
        Estado = EstadoSaga.AguardandoInsumos;
        ProximoAlertaInsumosEm = agora + IntervaloAlertaInsumos;
        Registrar(agora, TiposMensagem.InsumosReservaFalhou, eventId, $"Sem saldo de insumos; aguardando. Motivo: {motivo ?? "não informado"}.");
        return new Transicao(true, $"Saga da OS {OrdemServicoId} aguarda insumos: {motivo ?? "sem saldo"}.", "saga.aguardando-insumos");
    }

    public bool AlertaInsumosDevido(DateTimeOffset agora) => AguardaAlertaInsumos && ProximoAlertaInsumosEm <= agora;

    public void RegistrarAlertaInsumos(DateTimeOffset agora)
    {
        ProximoAlertaInsumosEm = agora + IntervaloAlertaInsumos;
        Registrar(agora, "saga.aguardando-insumos", null, "Alerta diário: a Saga segue aguardando insumos.");
        EmitirLembrete(agora, "aguardando-insumos");
    }

    public bool LembretePagamentoDevido(DateTimeOffset agora) => AguardaLembretePagamento && ProximoLembretePagamentoEm <= agora;

    /// <summary>Lembrete de pagamento: só publica <c>saga.lembrete.v1</c> e agenda o próximo; a Saga nunca expira nem cancela por falta de pagamento.</summary>
    public void RegistrarLembretePagamento(DateTimeOffset agora)
    {
        ProximoLembretePagamentoEm = agora + IntervaloLembretePagamento;
        EmitirLembrete(agora, "aguardando-pagamento");
    }

    private void EmitirLembrete(DateTimeOffset agora, string motivo)
    {
        var id = Guid.NewGuid();
        var corpo = JsonSerializer.Serialize(new
        {
            eventId = id,
            tipo = TiposMensagem.SagaLembrete,
            correlacaoId = CorrelacaoId,
            ordemServicoId = OrdemServicoId,
            clienteId = ClienteId,
            motivo,
            ocorreuEm = Formatar(agora)
        }, JsonComando);
        Comandos.Add(new ComandoSaga(id, TiposMensagem.SagaLembrete, DestinoComando.Cadastros, null, corpo, agora));
        Registrar(agora, "saga.lembrete", id, $"Lembrete {motivo} persistido para o Cadastros.");
    }

    /// <summary>Retentar a reserva (pessoa autorizada): só vale em <c>AguardandoInsumos</c>; novo comando com novo <c>eventId</c>.</summary>
    public Transicao Retentar(Guid eventId, string autor, string motivo, DateTimeOffset agora)
    {
        if (Estado != EstadoSaga.AguardandoInsumos)
            return Rejeitar(agora, "saga.retentada", eventId, $"Retentativa recusada: a Saga está em {Estado}.");
        Registrar(agora, "saga.retentada", eventId, $"Retentativa por {autor}: {motivo}.");
        IniciarReserva(agora);
        return Transicao.Ok;
    }

    public Transicao AplicarExecucaoConcluida(Guid eventId, DateTimeOffset agora)
    {
        if (Estado is not (EstadoSaga.ExecucaoLiberada or EstadoSaga.IntervencaoRequerida))
            return Rejeitar(agora, TiposMensagem.ExecucaoConcluida, eventId, $"Transição inválida a partir de {Estado}.");
        Estado = EstadoSaga.ExecucaoConcluida;
        Etapa(3).Marcar(SituacaoEtapa.Concluida, agora);
        ProximoLembretePagamentoEm = agora + IntervaloLembretePagamento;
        SincronizacoesPendentes.Add(StatusOS.Concluida);
        return Aceitar(agora, TiposMensagem.ExecucaoConcluida, eventId, "Execução concluída; aguardando pagamento.");
    }

    public Transicao AplicarExecucaoFalhou(Guid eventId, string? motivo, DateTimeOffset agora)
    {
        if (Estado != EstadoSaga.ExecucaoLiberada)
            return Rejeitar(agora, TiposMensagem.ExecucaoFalhou, eventId, $"Transição inválida a partir de {Estado}.");
        Estado = EstadoSaga.IntervencaoRequerida;
        SincronizacoesPendentes.Add(StatusOS.IntervencaoRequerida);
        return Aceitar(agora, TiposMensagem.ExecucaoFalhou, eventId, $"Execução falhou; intervenção requerida (sem compensação). Motivo: {motivo ?? "não informado"}.");
    }

    public Transicao AplicarLiberacaoRecusada(Guid eventId, string? motivo, DateTimeOffset agora)
    {
        if (Estado != EstadoSaga.ExecucaoLiberada)
            return Rejeitar(agora, TiposMensagem.ExecucaoLiberacaoRecusada, eventId, $"Transição inválida a partir de {Estado}.");
        Etapa(2).Marcar(SituacaoEtapa.Falhou, agora);
        var texto = $"Liberação recusada pelo Execution: {motivo ?? "não informado"}.";
        Registrar(agora, TiposMensagem.ExecucaoLiberacaoRecusada, eventId, texto);
        IniciarCompensacao(agora, texto);
        return Transicao.Ok;
    }

    public Transicao AplicarPagamento(Guid eventId, Guid pagamentoId, StatusPagamento status, string? referencia, DateTimeOffset agora)
    {
        var detalhe = $"Pagamento {pagamentoId} {status} (referência {referencia ?? "n/d"}).";
        if (status != StatusPagamento.Capturado)
            return Aceitar(agora, TiposMensagem.PagamentoResultado, eventId, detalhe);
        if (Estado == EstadoSaga.ExecucaoConcluida)
        {
            Estado = EstadoSaga.PagamentoCapturado;
            Etapa(4).Marcar(SituacaoEtapa.Concluida, agora);
            SincronizacoesPendentes.Add(StatusOS.Paga);
            return Aceitar(agora, TiposMensagem.PagamentoResultado, eventId, detalhe);
        }
        var alerta = Estado == EstadoSaga.PagamentoCapturado ? null : "saga.pagamento-capturado-fora-de-ordem";
        return Rejeitar(agora, TiposMensagem.PagamentoResultado, eventId, $"{detalhe} Transição inválida a partir de {Estado}; nenhum estorno é executado.", alerta);
    }

    public Transicao AplicarCancelamento(Guid eventId, string? motivo, DateTimeOffset agora)
    {
        if (Estado is EstadoSaga.ExecucaoConcluida or EstadoSaga.PagamentoCapturado)
            return Rejeitar(agora, TiposMensagem.OrdemServicoCancelar, eventId,
                $"Cancelamento recusado: execução já concluída ({Estado}); nenhuma compensação nem estorno é executado.", "saga.cancelamento-recusado");
        // D-33: sem confirmação da reserva não há o que cancelar com segurança; o comando espera (o cliente tenta de novo).
        if (Estado == EstadoSaga.ReservandoInsumos)
            return Rejeitar(agora, TiposMensagem.OrdemServicoCancelar, eventId, "saga_em_andamento: reserva de insumos sem confirmação; cancelamento aguarda.");
        if (Estado is EstadoSaga.Compensando or EstadoSaga.Compensada or EstadoSaga.CompensacaoFalhou)
            return Rejeitar(agora, TiposMensagem.OrdemServicoCancelar, eventId, $"Cancelamento ignorado: Saga já está em {Estado}.");
        Registrar(agora, TiposMensagem.OrdemServicoCancelar, eventId, $"Cancelamento solicitado: {motivo ?? "não informado"}.");
        DescartarLiberadaPendente(agora);
        IniciarCompensacao(agora, motivo ?? "cancelamento solicitado");
        return Transicao.Ok;
    }

    /// <summary>Confirmação de compensação (<c>orcamento.cancelado.v1</c> = etapa 1, <c>execucao.cancelada.v1</c> = etapa 2).</summary>
    public Transicao AplicarCompensacaoConfirmada(string tipo, int numeroEtapa, Guid eventId, DateTimeOffset agora)
    {
        var etapa = Etapa(numeroEtapa);
        if (Estado != EstadoSaga.Compensando || etapa.Situacao != SituacaoEtapa.Compensando)
            return Rejeitar(agora, tipo, eventId, $"Confirmação inesperada da etapa {numeroEtapa} em {Estado}/{etapa.Situacao}.");
        etapa.ConfirmarCompensacao("confirmada", agora);
        Registrar(agora, tipo, eventId, $"Compensação da etapa {numeroEtapa} confirmada.");
        if (!EmitirProximaCompensacao(agora, MotivoCompensacao ?? "cancelamento"))
        {
            Estado = EstadoSaga.Compensada;
            SincronizacoesPendentes.Add(StatusOS.Cancelada);
            Registrar(agora, tipo, eventId, "Todas as compensações confirmadas; Saga compensada.");
        }
        return Transicao.Ok;
    }

    public Transicao AplicarCompensacaoRecusada(string tipo, int numeroEtapa, Guid eventId, string? motivo, DateTimeOffset agora)
    {
        var etapa = Etapa(numeroEtapa);
        if (Estado != EstadoSaga.Compensando || etapa.Situacao != SituacaoEtapa.Compensando)
            return Rejeitar(agora, tipo, eventId, $"Recusa inesperada da etapa {numeroEtapa} em {Estado}/{etapa.Situacao}.");
        FalharCompensacao(numeroEtapa, eventId, tipo, motivo ?? "compensação recusada", agora);
        return new Transicao(true, null, "saga.compensacao-falhou");
    }

    /// <summary>Mensagem cujos ids (<c>correlacaoId</c>, <c>orcamentoId</c>, <c>execucaoId</c>) não coincidem com os da Saga: rejeitada com alerta.</summary>
    public Transicao RejeitarDivergencia(string tipo, Guid eventId, string motivo, DateTimeOffset agora) =>
        Rejeitar(agora, tipo, eventId, motivo, "saga.mensagem-divergente");

    /// <summary>Guarda o id da execução na primeira vez em que o Execution o informa (concluída ou falhou).</summary>
    public void DefinirExecucaoId(Guid? execucaoId)
    {
        if (execucaoId is { } id && ExecucaoId is null) ExecucaoId = id;
    }

    // ---- Prazo da compensação ----

    /// <summary>Recalcula o prazo da compensação em andamento (nulo fora de <c>Compensando</c>); chamado antes de cada gravação.</summary>
    public void ArmarPrazoCompensacao(TimeSpan prazo) =>
        PrazoCompensacaoEm = Estado == EstadoSaga.Compensando && CompensacaoEmitidaEm is { } emitida ? emitida + prazo : null;

    public bool CompensacaoSemResposta(DateTimeOffset agora) => AguardaPrazoCompensacao && PrazoCompensacaoEm <= agora;

    /// <summary>Sem confirmação no prazo: a etapa em compensação falha e a Saga vai a <c>CompensacaoFalhou</c>. Devolve a etapa e o prazo vencido.</summary>
    public (int Etapa, string Compensacao, DateTimeOffset Prazo) ExpirarCompensacao(DateTimeOffset agora)
    {
        var etapa = Etapas.Single(e => e.Situacao == SituacaoEtapa.Compensando);
        var prazo = PrazoCompensacaoEm!.Value;
        var comando = Comandos.LastOrDefault(c => c.EtapaCompensada == etapa.Numero);
        FalharCompensacao(etapa.Numero, comando?.EventId ?? Guid.Empty, "saga.compensacao-sem-resposta",
            $"Sem confirmação do participante até {Formatar(prazo)} (prazo vencido).", agora);
        PrazoCompensacaoEm = null;
        return (etapa.Numero, etapa.Compensacao!, prazo);
    }

    /// <summary>
    /// Reenvia a compensação sem confirmação (idempotente no participante) e reinicia o prazo; vale até <paramref name="maxReenvios"/> vezes
    /// por compensação. Esgotados, o chamador usa <see cref="ExpirarCompensacao"/>.
    /// </summary>
    public bool ReenviarCompensacao(DateTimeOffset agora, int maxReenvios)
    {
        if (ReenviosCompensacao >= maxReenvios) return false;
        var etapa = Etapas.Single(e => e.Situacao == SituacaoEtapa.Compensando);
        var anterior = Comandos.Last(c => c.EtapaCompensada == etapa.Numero);
        if (anterior.Pendente) anterior.MarcarDescartado(agora);
        var novo = anterior.Reenvio(Guid.NewGuid(), agora);
        Comandos.Add(novo);
        ReenviosCompensacao++;
        CompensacaoEmitidaEm = agora;
        Registrar(agora, "saga.compensacao-reenviada", novo.EventId, $"Compensação da etapa {etapa.Numero} sem confirmação; reenvio {ReenviosCompensacao}.");
        return true;
    }

    // ---- Prazo técnico do passo (reserva e liberação) ----

    public const string MotivoSemResposta = "liberacao_sem_resposta";

    /// <summary>
    /// Arma o prazo do passo que espera: a reserva espera <c>insumos.reservados.v1</c> ou <c>insumos.reserva-falhou.v1</c>; a liberação
    /// só tem prazo enquanto o comando não foi entregue, porque o contrato não traz confirmação positiva de <c>ordem-servico.liberada.v1</c>.
    /// </summary>
    public void ArmarPrazoPasso(TimeSpan prazoReserva, TimeSpan prazoLiberacao) =>
        PrazoPassoEm = (Estado, PassoIniciadoEm) switch
        {
            (EstadoSaga.ReservandoInsumos, { } inicio) => inicio + prazoReserva,
            (EstadoSaga.ExecucaoLiberada, { } inicio) when LiberacaoNaoEntregue() => inicio + prazoLiberacao,
            _ => null
        };

    public bool PassoSemResposta(DateTimeOffset agora) => AguardaPrazoPasso && PrazoPassoEm <= agora;

    /// <summary>
    /// Prazo vencido: reenvia o comando (novo <c>eventId</c>; a Execução é idempotente por OS) até <paramref name="maxReenvios"/> vezes e,
    /// esgotados, compensa com o motivo <c>liberacao_sem_resposta</c>. A reserva sem resposta é tratada como possivelmente feita
    /// (compensação <c>cancelar-reserva</c> forçada); a liberação não entregue é abandonada e a etapa 2 não é compensada.
    /// </summary>
    public (string Comando, bool Reenviado, DateTimeOffset Prazo) ExpirarPasso(DateTimeOffset agora, int maxReenvios)
    {
        var prazo = PrazoPassoEm!.Value;
        var reserva = Estado == EstadoSaga.ReservandoInsumos;
        var tipo = reserva ? TiposMensagem.ExecucaoReservarInsumos : TiposMensagem.OrdemServicoLiberada;
        PrazoPassoEm = null;
        if (ReenviosPasso < maxReenvios)
        {
            var anterior = Comandos.Last(c => c.Tipo == tipo);
            if (anterior.Pendente) anterior.MarcarDescartado(agora);
            var novo = anterior.Reenvio(Guid.NewGuid(), agora);
            Comandos.Add(novo);
            ReenviosPasso++;
            PassoIniciadoEm = agora;
            Registrar(agora, "saga.passo-reenviado", novo.EventId, $"Sem resposta de {tipo} até {Formatar(prazo)}; reenvio {ReenviosPasso}.");
            return (tipo, true, prazo);
        }
        Registrar(agora, "saga.passo-sem-resposta", null, $"Sem resposta de {tipo} até {Formatar(prazo)} depois de {ReenviosPasso} reenvios; compensando.");
        if (reserva) Etapa(5).Marcar(SituacaoEtapa.Concluida, agora);
        else AbandonarLiberacaoNaoEntregue(agora);
        PassoIniciadoEm = null;
        IniciarCompensacao(agora, MotivoSemResposta);
        return (tipo, false, prazo);
    }

    private bool LiberacaoNaoEntregue() => Comandos.Any(c => c.Tipo == TiposMensagem.OrdemServicoLiberada && c.EnviadoEm is null && c.DescartadoEm is null);

    /// <summary>Liberação que nunca foi entregue: o comando é descartado e a OS não vai a <c>EmExecucao</c>; a etapa 2 volta a pendente (nada a compensar).</summary>
    private void AbandonarLiberacaoNaoEntregue(DateTimeOffset agora)
    {
        foreach (var comando in Comandos.Where(c => c.Pendente && c.Tipo == TiposMensagem.OrdemServicoLiberada)) comando.MarcarDescartado(agora);
        Etapa(2).Marcar(SituacaoEtapa.Pendente, agora);
        var indice = SincronizacoesPendentes.IndexOf(StatusOS.EmExecucao);
        if (indice < 0) return;
        SincronizacoesPendentes.RemoveAt(indice);
        if (indice != 0) return;
        TentativasSincronizacao = 0; ProximaSincronizacaoEm = null; UltimoErroSincronizacao = null;
    }

    // ---- Progressão interna (sincronização da OS e despacho de comandos) ----

    public bool SincronizacaoDevida(DateTimeOffset agora) =>
        SincronizacoesPendentes.Count > 0 && (ProximaSincronizacaoEm is null || ProximaSincronizacaoEm <= agora);

    /// <summary>Comandos saem em ordem de criação: só o primeiro pendente pode ser devido (nenhum ultrapassa outro em backoff).</summary>
    public ComandoSaga? ProximoComandoDevido(DateTimeOffset agora)
    {
        if (Comandos.FirstOrDefault(c => c.Pendente) is not { } primeiro || (primeiro.ProximaTentativaEm is not null && primeiro.ProximaTentativaEm > agora))
            return null;
        primeiro.NormalizarTipo();
        return primeiro;
    }

    /// <summary>Menor instante em que há trabalho a fazer (sincronização, primeiro comando pendente ou prazo da compensação); nulo se não há pendência.</summary>
    public DateTimeOffset? ProximoTrabalhoEm()
    {
        DateTimeOffset? menor = null;
        void Considerar(DateTimeOffset quando)
        {
            if (menor is null || quando < menor) menor = quando;
        }
        if (SincronizacoesPendentes.Count > 0) Considerar(ProximaSincronizacaoEm ?? DateTimeOffset.MinValue);
        if (Comandos.FirstOrDefault(c => c.Pendente) is { } primeiro) Considerar(primeiro.ProximaTentativaEm ?? DateTimeOffset.MinValue);
        if (AguardaPrazoCompensacao) Considerar(PrazoCompensacaoEm!.Value);
        if (AguardaPrazoPasso) Considerar(PrazoPassoEm!.Value);
        if (AguardaAlertaInsumos) Considerar(ProximoAlertaInsumosEm!.Value);
        if (AguardaLembretePagamento) Considerar(ProximoLembretePagamentoEm!.Value);
        return menor;
    }

    public void ConcluirSincronizacao(DateTimeOffset agora)
    {
        var status = SincronizacoesPendentes[0];
        SincronizacoesPendentes.RemoveAt(0);
        TentativasSincronizacao = 0;
        ProximaSincronizacaoEm = null;
        UltimoErroSincronizacao = null;
        Registrar(agora, "os.sincronizada", null, $"OS sincronizada para {status}.");
    }

    public void FalharSincronizacao(DateTimeOffset proximaEm, string erro, DateTimeOffset agora)
    {
        TentativasSincronizacao++;
        ProximaSincronizacaoEm = proximaEm;
        UltimoErroSincronizacao = erro;
        AtualizadoEm = agora;
    }

    /// <summary>Após sincronizar <c>OrcamentoAprovado</c>: libera a execução publicando <c>ordem-servico.liberada.v1</c>.</summary>
    public bool AvancarParaExecucaoLiberada(Guid clienteId, Guid veiculoId, DateTimeOffset agora)
    {
        if (Estado != EstadoSaga.OrcamentoAprovado || SincronizacoesPendentes.Count > 0) return false;
        ClienteId = clienteId;
        VeiculoId = veiculoId;
        // Com insumos, a execução só é liberada depois de `insumos.reservados.v1`; sem insumos não há o que reservar.
        if (Itens.Count > 0) IniciarReserva(agora);
        else LiberarExecucao(agora);
        return true;
    }

    private void IniciarReserva(DateTimeOffset agora)
    {
        Estado = EstadoSaga.ReservandoInsumos;
        ProximoAlertaInsumosEm = null;
        PassoIniciadoEm = agora;
        ReenviosPasso = 0;
        var id = Guid.NewGuid();
        var corpo = JsonSerializer.Serialize(new
        {
            eventId = id,
            tipo = TiposMensagem.ExecucaoReservarInsumos,
            correlacaoId = CorrelacaoId,
            ordemServicoId = OrdemServicoId,
            ocorreuEm = Formatar(agora),
            itens = Itens.Select(i => new { insumoId = i.InsumoId, quantidade = i.Quantidade })
        }, JsonComando);
        Comandos.Add(new ComandoSaga(id, TiposMensagem.ExecucaoReservarInsumos, DestinoComando.Execucao, null, corpo, agora));
        Registrar(agora, "saga.avancou", id, "Reservando insumos; comando execucao.reservar-insumos.v1 persistido.");
    }

    private void LiberarExecucao(DateTimeOffset agora)
    {
        var clienteId = ClienteId;
        var veiculoId = VeiculoId;
        Estado = EstadoSaga.ExecucaoLiberada;
        PassoIniciadoEm = agora;
        ReenviosPasso = 0;
        Etapa(2).Marcar(SituacaoEtapa.Concluida, agora);
        var id = Guid.NewGuid();
        var corpo = JsonSerializer.Serialize(new
        {
            eventId = id,
            tipo = TiposMensagem.OrdemServicoLiberada,
            correlacaoId = CorrelacaoId,
            ordemServicoId = OrdemServicoId,
            id = OrdemServicoId,
            clienteId,
            veiculoId,
            ocorreuEm = Formatar(agora)
        }, JsonComando);
        Comandos.Add(new ComandoSaga(id, TiposMensagem.OrdemServicoLiberada, DestinoComando.Execucao, null, corpo, agora));
        SincronizacoesPendentes.Add(StatusOS.EmExecucao);
        Registrar(agora, "saga.avancou", null, "Execução liberada; comando ordem-servico.liberada.v1 persistido.");
    }

    public void RegistrarEnvio(Guid eventIdComando, DateTimeOffset agora)
    {
        var comando = Comandos.Single(c => c.EventId == eventIdComando);
        comando.MarcarEnviado(agora);
        Registrar(agora, "comando.enviado", eventIdComando, $"Comando {comando.Tipo} publicado.");
    }

    public void RegistrarFalhaEnvio(Guid eventIdComando, DateTimeOffset proximaEm, string erro, DateTimeOffset agora)
    {
        Comandos.Single(c => c.EventId == eventIdComando).RegistrarFalha(proximaEm, erro);
        AtualizadoEm = agora;
    }

    /// <summary>Esgotou as tentativas na fila principal e o encaminhamento à DLQ também falhou: mantém pendente.</summary>
    public void RegistrarFalhaDlq(Guid eventIdComando, DateTimeOffset proximaEm, string erro, DateTimeOffset agora)
    {
        Comandos.Single(c => c.EventId == eventIdComando).RegistrarFalhaDlq(proximaEm, erro);
        AtualizadoEm = agora;
    }

    /// <summary>Comando esgotado e enviado à DLQ. Se era uma compensação, a Saga vai a <c>CompensacaoFalhou</c>.</summary>
    public bool RegistrarComandoNaDlq(Guid eventIdComando, string erro, DateTimeOffset agora)
    {
        var comando = Comandos.Single(c => c.EventId == eventIdComando);
        comando.MarcarEncaminhadoParaDlq(agora, erro);
        Registrar(agora, "comando.dlq", eventIdComando, $"Comando {comando.Tipo} esgotou as tentativas e foi para a DLQ: {erro}");
        if (comando.EtapaCompensada is not { } etapa || Estado != EstadoSaga.Compensando) return false;
        FalharCompensacao(etapa, eventIdComando, "comando.dlq", $"Comando de compensação esgotado: {erro}", agora);
        return true;
    }

    // ---- Internos ----

    private void FalharCompensacao(int numeroEtapa, Guid eventId, string evento, string erro, DateTimeOffset agora)
    {
        Etapa(numeroEtapa).FalharCompensacao(erro, agora);
        Estado = EstadoSaga.CompensacaoFalhou;
        Registrar(agora, evento, eventId, $"Compensação da etapa {numeroEtapa} falhou: {erro}");
    }

    /// <summary>O comando de liberação ainda não enviado (a execução não o recebeu): é descartado em vez de compensado.</summary>
    private void DescartarLiberadaPendente(DateTimeOffset agora)
    {
        if (Estado != EstadoSaga.ExecucaoLiberada) return;
        var liberada = Comandos.FirstOrDefault(c => c.Tipo == TiposMensagem.OrdemServicoLiberada && c.Pendente);
        if (liberada is null) return;
        liberada.MarcarDescartado(agora);
        Etapa(2).Marcar(SituacaoEtapa.Pendente, agora);
        var indice = SincronizacoesPendentes.IndexOf(StatusOS.EmExecucao);
        if (indice >= 0)
        {
            SincronizacoesPendentes.RemoveAt(indice);
            if (indice == 0)
            {
                TentativasSincronizacao = 0;
                ProximaSincronizacaoEm = null;
                UltimoErroSincronizacao = null;
            }
        }
        Registrar(agora, "comando.descartado", liberada.EventId, "Cancelamento antes da entrega: o comando ordem-servico.liberada.v1 pendente foi descartado; a etapa 2 não é compensada.");
    }

    private static string Limitar(string texto)
    {
        if (texto.Length <= MotivoMaximo) return texto;
        var corte = MotivoMaximo - 1;
        if (char.IsHighSurrogate(texto[corte - 1])) corte--;
        return texto[..corte] + "…";
    }

    private void IniciarCompensacao(DateTimeOffset agora, string motivo)
    {
        motivo = Limitar(motivo);
        Estado = EstadoSaga.Compensando;
        ProximoAlertaInsumosEm = null;
        MotivoCompensacao = motivo;
        if (!EmitirProximaCompensacao(agora, motivo))
        {
            Estado = EstadoSaga.Compensada;
            SincronizacoesPendentes.Add(StatusOS.Cancelada);
        }
    }

    /// <summary>Em ordem inversa: a etapa concluída de maior número com compensação declarada.</summary>
    private bool EmitirProximaCompensacao(DateTimeOffset agora, string motivo)
    {
        var etapa = Etapas.OrderByDescending(e => OrdemCompensacao(e.Numero)).FirstOrDefault(e => e.Situacao == SituacaoEtapa.Concluida && e.Compensacao is not null);
        if (etapa is null) return false;
        var compensacao = etapa.NormalizarCompensacao();
        etapa.Marcar(SituacaoEtapa.Compensando, agora);
        CompensacaoEmitidaEm = agora;
        ReenviosCompensacao = 0;
        var id = Guid.NewGuid();
        var porExecucao = etapa.Numero is 2 or 5;
        var corpo = porExecucao
            ? JsonSerializer.Serialize(new { eventId = id, tipo = compensacao, correlacaoId = CorrelacaoId, ordemServicoId = OrdemServicoId, ocorreuEm = Formatar(agora), motivo }, JsonComando)
            : JsonSerializer.Serialize(new { eventId = id, tipo = compensacao, correlacaoId = CorrelacaoId, ordemServicoId = OrdemServicoId, ocorreuEm = Formatar(agora), orcamentoId = OrcamentoId, motivo }, JsonComando);
        Comandos.Add(new ComandoSaga(id, compensacao, porExecucao ? DestinoComando.Execucao : DestinoComando.Billing, etapa.Numero, corpo, agora));
        Registrar(agora, "saga.compensando", id, $"Compensação da etapa {etapa.Numero} ({compensacao}) persistida.");
        return true;
    }

    /// <summary>A reserva (etapa 5) acontece entre o orçamento (1) e a liberação (2); a compensação sai na ordem inversa: 2, 5, 1.</summary>
    private static double OrdemCompensacao(int numero) => numero == 5 ? 1.5 : numero;

    private Transicao Aceitar(DateTimeOffset agora, string evento, Guid eventId, string descricao)
    {
        Registrar(agora, evento, eventId, descricao);
        return Transicao.Ok;
    }

    private Transicao Rejeitar(DateTimeOffset agora, string evento, Guid eventId, string motivo, string? alerta = null)
    {
        Registrar(agora, evento, eventId, $"Transição rejeitada: {motivo}");
        return new Transicao(false, motivo, alerta);
    }

    private void Registrar(DateTimeOffset agora, string evento, Guid? eventId, string descricao)
    {
        AtualizadoEm = agora;
        Registros.Add(new RegistroSaga(agora, evento, eventId, descricao));
    }

    private static string Formatar(DateTimeOffset agora) => agora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

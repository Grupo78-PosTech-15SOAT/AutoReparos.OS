using AutoReparos.OS.Application.Saga;
using AutoReparos.OS.Domain;
using Reqnroll;

/// <summary>Passos dos cenários da Saga do OS: o ator é a mensagem que Cobrança ou Execução enviaria.</summary>
[Binding]
public sealed class PassosSaga(ContextoSaga saga)
{
    [Given("uma ordem de serviço com orçamento aprovado e insumos a reservar")]
    public Task DadoOrcamentoAprovadoComInsumos() => saga.AprovarOrcamentoComInsumosAsync();

    [Given("uma ordem de serviço com orçamento aprovado sem insumos")]
    public Task DadoOrcamentoAprovadoSemInsumos() => saga.AprovarOrcamentoSemInsumosAsync();

    [Given("o Execução confirma a reserva dos insumos")]
    [When("o Execução confirma a reserva dos insumos")]
    public Task QuandoReservaConfirmada() => saga.ReceberInsumosReservadosAsync();

    [When("o Execução informa que não há saldo dos insumos")]
    public Task QuandoSemSaldo() => saga.ReceberReservaFalhouAsync();

    [When("o Execução recusa a liberação da ordem de serviço")]
    public Task QuandoLiberacaoRecusada() => saga.ReceberAsync(TiposMensagem.ExecucaoLiberacaoRecusada, ("motivo", "oficina lotada"));

    [Given("o Execução conclui a execução")]
    [When("o Execução conclui a execução")]
    public Task QuandoExecucaoConcluida() => saga.ReceberAsync(TiposMensagem.ExecucaoConcluida);

    [When("a Cobrança informa o pagamento {string}")]
    public Task QuandoPagamento(string status) => saga.ReceberPagamentoAsync(status);

    [When("o atendente cancela a ordem de serviço")]
    public Task QuandoCancelamento() => saga.ReceberAsync(TiposMensagem.OrdemServicoCancelar, ("motivo", "sem previsão de estoque"));

    [When("o Execução confirma o cancelamento da execução")]
    public Task QuandoExecucaoCancelada() => saga.ReceberAsync(TiposMensagem.ExecucaoCancelada, ("execucaoId", Guid.NewGuid()));

    [When("o Execução confirma o cancelamento da reserva dos insumos")]
    public Task QuandoReservaCancelada() => saga.ReceberReservaCanceladaAsync();

    [When("a Cobrança confirma o cancelamento do orçamento")]
    public Task QuandoOrcamentoCancelado() => saga.ReceberOrcamentoCanceladoAsync();

    [Then("a Saga está no estado {string}")]
    public async Task EntaoEstado(string estado) => Assert.Equal(Enum.Parse<EstadoSaga>(estado), await saga.EstadoAsync());

    [Then("a ordem de serviço está com o status {string}")]
    public async Task EntaoStatus(string status) => Assert.Equal(status, await saga.StatusOSAsync());

    [Then("a Saga enviou, nesta ordem, os comandos {string}")]
    public void EntaoComandos(string comandos) => Assert.Equal(comandos.Split(',', StringSplitOptions.TrimEntries), saga.Enviados);

    [Then("a Saga enviou, nesta ordem, os comandos de compensação {string}")]
    public void EntaoCompensacoes(string comandos) => Assert.Equal(comandos.Split(',', StringSplitOptions.TrimEntries), saga.Compensacoes);

    [Then("nenhum comando de compensação foi enviado")]
    public void EntaoSemCompensacao() => Assert.Empty(saga.Compensacoes);

    [Then("a Saga alertou {string}")]
    public void EntaoAlerta(string codigo) => Assert.Contains(codigo, saga.Alertas);
}

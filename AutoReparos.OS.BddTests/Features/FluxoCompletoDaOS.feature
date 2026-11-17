# language: pt
Funcionalidade: Fluxo completo da ordem de serviço na Saga
  A Saga orquestrada do OS conduz o orçamento aprovado até o pagamento, falando com a Cobrança e o Execução.

  Cenário: Pagamento aprovado conclui a Saga e marca a OS como paga
    Dado uma ordem de serviço com orçamento aprovado e insumos a reservar
    Então a Saga está no estado "ReservandoInsumos"
    Quando o Execução confirma a reserva dos insumos
    Então a Saga está no estado "ExecucaoLiberada"
    E a ordem de serviço está com o status "EmExecucao"
    Quando o Execução conclui a execução
    Então a Saga está no estado "ExecucaoConcluida"
    E a ordem de serviço está com o status "Concluida"
    Quando a Cobrança informa o pagamento "Capturado"
    Então a Saga está no estado "PagamentoCapturado"
    E a ordem de serviço está com o status "Paga"
    E a Saga enviou, nesta ordem, os comandos "execucao.reservar-insumos.v1, ordem-servico.liberada.v1"

  Cenário: OS só de mão de obra pula a reserva e libera a execução direto
    Dado uma ordem de serviço com orçamento aprovado sem insumos
    Então a Saga está no estado "ExecucaoLiberada"
    E a ordem de serviço está com o status "EmExecucao"
    E a Saga enviou, nesta ordem, os comandos "ordem-servico.liberada.v1"
    Quando o Execução conclui a execução
    Então a Saga está no estado "ExecucaoConcluida"
    E a ordem de serviço está com o status "Concluida"
    Quando a Cobrança informa o pagamento "Capturado"
    Então a Saga está no estado "PagamentoCapturado"
    E a ordem de serviço está com o status "Paga"
    E a Saga enviou, nesta ordem, os comandos "ordem-servico.liberada.v1"

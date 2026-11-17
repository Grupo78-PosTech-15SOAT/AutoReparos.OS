# language: pt
Funcionalidade: Compensações da Saga do OS
  Quando uma etapa falha, a Saga só desfaz o que já foi concluído, em ordem inversa, ou espera a intervenção.

  Cenário: Pagamento recusado não dispara compensação e deixa a Saga aguardando
    Dado uma ordem de serviço com orçamento aprovado e insumos a reservar
    E o Execução confirma a reserva dos insumos
    E o Execução conclui a execução
    Quando a Cobrança informa o pagamento "Rejeitado"
    Então a Saga está no estado "ExecucaoConcluida"
    E a ordem de serviço está com o status "Concluida"
    E o histórico da Saga registra o pagamento "Rejeitado"
    E nenhum comando de compensação foi enviado
    E a Saga enviou, nesta ordem, os comandos "execucao.reservar-insumos.v1, ordem-servico.liberada.v1"

  Cenário: Falha na reserva de insumos aguarda saldo e o cancelamento compensa só o orçamento
    Dado uma ordem de serviço com orçamento aprovado e insumos a reservar
    Quando o Execução informa que não há saldo dos insumos
    Então a Saga está no estado "AguardandoInsumos"
    E a Saga alertou "saga.aguardando-insumos"
    E nenhum comando de compensação foi enviado
    Quando o atendente cancela a ordem de serviço
    Então a Saga está no estado "Compensando"
    E a Saga enviou, nesta ordem, os comandos "execucao.reservar-insumos.v1, cobranca.cancelar-orcamento.v1"
    Quando a Cobrança confirma o cancelamento do orçamento
    Então a Saga está no estado "Compensada"
    E a ordem de serviço está com o status "Cancelada"

  Cenário: Liberação recusada pelo Execução compensa a reserva e o orçamento em ordem inversa
    Dado uma ordem de serviço com orçamento aprovado e insumos a reservar
    E o Execução confirma a reserva dos insumos
    Quando o Execução recusa a liberação da ordem de serviço
    Então a Saga está no estado "Compensando"
    E a Saga enviou, nesta ordem, os comandos de compensação "execucao.cancelar-reserva.v1"
    Quando o Execução confirma o cancelamento da reserva dos insumos
    Então a Saga enviou, nesta ordem, os comandos de compensação "execucao.cancelar-reserva.v1, cobranca.cancelar-orcamento.v1"
    Quando a Cobrança confirma o cancelamento do orçamento
    Então a Saga está no estado "Compensada"
    E a ordem de serviço está com o status "Cancelada"
    E a Saga enviou, nesta ordem, os comandos de compensação "execucao.cancelar-reserva.v1, cobranca.cancelar-orcamento.v1"

  Cenário: Cancelamento pelo atendente com insumos reservados compensa em ordem inversa
    Dado uma ordem de serviço com orçamento aprovado e insumos a reservar
    E o Execução confirma a reserva dos insumos
    Então a Saga está no estado "ExecucaoLiberada"
    Quando o atendente cancela a ordem de serviço
    Então a Saga está no estado "Compensando"
    E a Saga enviou, nesta ordem, os comandos de compensação "execucao.cancelar.v1"
    Quando o Execução confirma o cancelamento da execução
    Então a Saga enviou, nesta ordem, os comandos de compensação "execucao.cancelar.v1, execucao.cancelar-reserva.v1"
    Quando o Execução confirma o cancelamento da reserva dos insumos
    Então a Saga enviou, nesta ordem, os comandos de compensação "execucao.cancelar.v1, execucao.cancelar-reserva.v1, cobranca.cancelar-orcamento.v1"
    Quando a Cobrança confirma o cancelamento do orçamento
    Então a Saga está no estado "Compensada"
    E a ordem de serviço está com o status "Cancelada"
    E a Saga enviou, nesta ordem, os comandos de compensação "execucao.cancelar.v1, execucao.cancelar-reserva.v1, cobranca.cancelar-orcamento.v1"

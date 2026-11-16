{{/* Configuração específica do OS. */}}
{{- define "servico.env" -}}
- name: Authentication__Authority
  value: {{ .Values.autenticacao.emissor | quote }}
{{- with .Values.autenticacao.audiencia }}
- name: Authentication__Audience
  value: {{ . | quote }}
{{- end }}
- name: Telemetry__ServiceName
  value: {{ .Values.telemetria.nomeServico | quote }}
- name: Telemetry__OtlpEndpoint
  value: {{ .Values.telemetria.otlpEndpoint | quote }}
- name: Sqs__SagaQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.saga) | quote }}
- name: Sqs__SagaDeadLetterQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.sagaDlq) | quote }}
- name: Sqs__ExecucaoQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.execucao) | quote }}
- name: Sqs__BillingQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.cobranca) | quote }}
- name: Sqs__CadastrosQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.cadastros) | quote }}
- name: DynamoDb__CriarTabelas
  value: "false"
- name: DynamoDb__TabelaSaga
  value: {{ .Values.dynamodb.tabelaSaga | quote }}
- name: DynamoDb__TabelaEventos
  value: {{ .Values.dynamodb.tabelaEventos | quote }}
- name: Cadastros__BaseUrl
  value: {{ .Values.cadastros.baseUrl | quote }}
- name: Cadastros__ChaveInterna
  valueFrom:
    secretKeyRef:
      name: {{ include "servico.nome" . }}-segredos
      key: Cadastros__ChaveInterna
{{- end }}

# NeoReports — Spec do MVP (v1)

Define **o que a v1 entrega, de forma testável**. A arquitetura está em `NeoReports-Decisoes.md`; a ordem de construção em `plan.md` (raiz).

## Objetivo

Um desenvolvedor .NET registra um report em código, fortemente tipado, que lê de um banco SQL por paginação keyset, escreve em CSV e XLSX com memória constante, e faz upload para um destino (Local ou S3) — síncrono (stream direto) ou assíncrono (job em background com worker único). Falhas de batch passam por retry (Polly) e por uma estratégia de falha (Abort ou SkipBatchAndLog).

## O que a v1 faz (resumo funcional)

- Registrar reports tipados via DI: `services.AddReport<TRow>("nome", b => ...)`.
- Ler de SQL com paginação keyset (cursor `string?`), abrindo/fechando conexão por página.
- `Map` para um tipo de saída, `Filter` com delegate C# tipado.
- Declarar o schema de colunas (nome, tipo, formato, cultura) para a projeção de saída.
- Escrever CSV (delimitador, encoding, cabeçalho) e XLSX (nome da aba, auto-filtro) na mesma passada.
- Upload para Local (path template) e S3 (bucket/key template).
- Disparar via API: assíncrono (retorna `jobId`) ou síncrono (stream no response).
- Consultar status do job e baixar o resultado.
- Retry com Polly + `IFailureStrategy` (Abort / SkipBatchAndLog) + thresholds (consecutivas / total / razão).

## Critérios de aceite (cada um vira teste)

1. **Registro tipado.** `AddReport<Venda>("vendas", b => b.From(sql).Map(...).To(Csv).UploadTo(Local))` registra e o report aparece no `IReportRegistry`.
2. **Leitura keyset.** A SQL source lê todas as páginas em ordem por uma coluna-chave, sem pular nem repetir registros; conexão é aberta/fechada por página.
3. **Memória constante.** Gerar um report de 1.000.000 de linhas mantém alocação ~constante (BenchmarkDotNet `MemoryDiagnoser`); não há crescimento proporcional ao total de linhas.
4. **CSV correto.** Saída CSV bate byte-a-byte com golden file: delimitador e encoding configurados, cabeçalho com `DisplayName`, valores formatados por cultura/format do schema.
5. **XLSX correto.** Arquivo abre no Excel/ClosedXML, aba nomeada, auto-filtro ativo, tipos nativos (número/data) preservados.
6. **Multi-output numa passada.** CSV + XLSX gerados lendo a source **uma única vez**.
7. **Upload Local.** Arquivo aparece no path resolvido (tokens `{date}`, `{name}` expandidos).
8. **Upload S3.** Objeto criado no bucket/key resolvido; upload tudo-ou-nada (sem objeto parcial em falha).
9. **Disparo assíncrono.** `POST /api/reports/{nome}/run` cria job `queued`, retorna `jobId`; worker processa; status caminha `queued → running → completed`.
10. **Disparo síncrono.** `POST /api/reports/{nome}/run?mode=sync` faz stream do único formato no response com `Content-Disposition` correto; multi-output em sync retorna `400`.
11. **Retry transitório.** Source que falha 2x com erro transitório e recupera na 3ª completa o report sem perda de dados.
12. **FailureStrategy Abort.** Com `AbortReport()`, falha definitiva de um batch aborta o report (status `failed`, motivo registrado).
13. **FailureStrategy Skip.** Com `SkipBatchAndLog()`, batch que falha definitivamente é pulado, warning estruturado é logado, report conclui marcado como **parcial**.
14. **Threshold.** Com `SkipBatchAndLog().AbortIf(t => t.ConsecutiveFailures(3))`, 3 falhas consecutivas abortam mesmo em modo skip.
15. **Cancelamento.** `POST /api/jobs/{id}/cancel` faz o job parar cooperativamente (status `cancelled`) em tempo razoável.
16. **Restart idempotente.** Job interrompido (processo morto) reinicia do zero sem corromper destino (sem arquivo parcial publicado).

## API REST da v1

```
POST   /api/reports/{nome}/run            # async  → 202 { jobId }
POST   /api/reports/{nome}/run?mode=sync  # sync   → 200 stream (single-output)
GET    /api/reports                        # lista reports registrados
GET    /api/jobs/{id}                       # status + stats
POST   /api/jobs/{id}/cancel                # cancela
GET    /api/jobs/{id}/download              # baixa o resultado quando completo
```

Parâmetros do report vão no body do `run` (`{ "parameters": { "inicio": "2026-01-01" } }`). Auth herda do host (sem auth chain na v1).

## Report de referência (alvo do primeiro end-to-end)

```csharp
public sealed record Venda(long Id, string Cliente, decimal Valor, DateTime Data);

services.AddReport<Venda>("vendas-mensal", b => b
    .From(Source.Sql("vendas-db",
        "SELECT Id, Cliente, Valor, Data FROM Vendas WHERE Data >= @inicio AND Id > @cursor ORDER BY Id")
        .Keyset(v => v.Id, pageSize: 1000))
    .Filter(v => v.Valor > 0)
    .Columns(
        Col(v => v.Id,      "ID Venda"),
        Col(v => v.Cliente, "Cliente"),
        Col(v => v.Valor,   "Valor",     format: "C2", culture: "pt-BR"),
        Col(v => v.Data,    "Data Venda", format: "yyyy-MM-dd"))
    .To(Format.Csv(o => o.Delimiter(';').Encoding(Encoding.UTF8)))
    .To(Format.Xlsx(o => o.SheetName("Vendas").AutoFilter()))
    .UploadTo(Destination.Local("./out/{name}-{date:yyyy-MM-dd}.{ext}"))
    .Retry(r => r.MaxAttempts(5).Exponential(baseDelay: TimeSpan.FromSeconds(2)).WithJitter())
    .OnFailure(f => f.SkipBatchAndLog().AbortIf(t => t.ConsecutiveFailures(3))));
```

Esse exemplo deve compilar e rodar end-to-end — é a definição prática de "MVP pronto".

## Não-objetivos da v1

Caminho dinâmico/config JSON, UI, variants, multi-worker, resume mid-job, PDF, SharePoint, auth chain, expressões dinâmicas. Ver lista completa em `CLAUDE.md` e no ADR.

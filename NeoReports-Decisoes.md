# NeoReports — Registro de Decisões (ADR consolidado)

> Documento para iniciar a próxima conversa com as decisões cravadas.
> Contexto de entrada: **v1 só code-first tipado · worker único (escala vertical) · MVP enxuto · solo founder (tempo parcial)**.

## Princípio que resolve a tensão

**O contrato (`NeoReports.Abstractions`) é desenhado para não fechar porta nenhuma — caminho dinâmico, multi-worker e UI são todos possíveis sem rework. A implementação da v1 entrega só o mínimo demonstrável.** Abstração estável e pequena; implementação enxuta.

Corolário para solo founder: cada interface pública em `Abstractions` é um passivo (trava SemVer, quebra plugins externos se mudar). Toda interface que o MVP não usa **sai** da v1.

---

## D1 — Registro tipado: pipeline genérica sobre `T`, projeção só na borda do writer

**Decisão.** v1 é **exclusivamente code-first tipado**. A pipeline é genérica sobre `T`; o registro *é* o POCO. Não existe `ReportRecord` posicional nem dicionário por linha na v1.

- **Leitura e processamento** (`IBatchSource<T>`, `map`, `filter`): tudo opera em `T`. Zero boxing durante o processamento.
- **Projeção para colunas** acontece **só na borda do writer**: o Core compila `Func<T, object?>` por coluna (a partir do `ReportSchema` declarado no builder) e projeta cada linha para `object?[]` na ordem do schema, imediatamente antes de escrever. Boxing só aqui, e é inevitável (CSV/XLSX são saídas fracamente tipadas).
- **Writers ficam não-genéricos**: consomem `(object?[] linha, ReportSchema)`. Plugin de formato não precisa saber de `T`.

**Por quê.** Dicionário por linha mata a memória constante. Processar tipado e projetar só na saída dá perf máxima no caminho quente e mantém os writers simples. O caminho dinâmico (`ReportRecord` posicional + filtro JsonLogic) **volta pós-MVP** sem quebrar o writer (a borda já fala `object?[]` + schema).

---

## D2 — Worker único (escala vertical)

**Decisão.**
- **Um processo worker**, escala vertical. Sem fila distribuída, sem coordenação multi-máquina na v1.
- Default: **Hangfire single-server** (storage SQL/SQLite) — ganha persistência de estado de job entre restarts e dashboard de graça, com um servidor só. `InMemory` para dev/testes.
- Job é **unidade atômica** que roda inteiro nesse worker. Se o processo cai no meio, o job **reinicia do zero** (re-execução idempotente). Saída em temp local, upload no fim, marca `completed`.
- `ICheckpointStore` **existe como contrato** mas é no-op na v1.

**Por quê.** Worker vertical é a forma mais simples e atende o MVP. Hangfire single-server custa quase nada e já abre caminho pra multi-server depois (é só subir mais instâncias) sem trocar o contrato. Resume mid-job e multi-worker ficam pós-MVP — o contrato (`IJobStore`, `ICheckpointStore`) já está pronto pra ambos.

---

## D3 — Cursor é token opaco serializável, não `object?`

**Decisão.** O cursor de paginação é um **token opaco serializável** (`string?`, codificado pela própria source). Nada de `object? Cursor`.

**Por quê.** Mesmo com worker único, o cursor é o mecanismo de paginação keyset (abre/fecha conexão por página). `string?` opaco é o tipo certo e custa zero — e já deixa checkpoint/multi-worker viáveis no futuro sem rework.

**Muda no código.** `BatchResult<T>.NextCursor`, `BatchContext.Cursor` e `Checkpoint.LastCursor` são `string?`. A source é dona do encode/decode do cursor tipado interno.

---

## D4 — Batch é o modelo canônico; streaming é adaptado

**Decisão.** `IBatchSource<T>` é o contrato primário e o modelo interno da pipeline. `IStreamingSource<T>` (`IAsyncEnumerable<T>`) existe como opção de autoria, mas é fatiado em batches por um `StreamingToBatchAdapter` (tamanho configurável).

**Por quê.** Retry, threshold e escrita operam em batch. Um modelo só pra raciocinar.

---

## D5 — Variants e coalescência: fora da v1

**Decisão.** Cortar variants, herança de config e coalescência do MVP. Reports independentes evoluem pra pipeline+variants depois sem quebrar contrato.

---

## D6 — Não re-abstrair o Polly

**Decisão.** Usar `Polly v8` (`ResiliencePipeline`) direto no loop de leitura de batch. Remover `IRetryPolicy` e `IExceptionClassifier` de `Abstractions`. Manter só `IFailureStrategy` (decisão após esgotar tentativas) + monitor de threshold.

**Muda no código.** Config de retry compila para um `ResiliencePipeline`. `IFailureStrategy` na v1: só `AbortReport()` e `SkipBatchAndLog()`.

---

## D7 — Renomear `ExecutionContext` → `ReportExecutionContext`

**Decisão.** Renomear. Colide com `System.Threading.ExecutionContext`.

---

## D8 — Disparo de report e modo `sync`

**Decisão.** Sem endpoint de config dinâmica na v1. Reports são registrados em código (`.AddReport("nome", b => b.From<T>()...)`). O endpoint dispara um report **registrado por nome** com parâmetros:

```
POST /api/reports/{nome}/run        # async → jobId
POST /api/reports/{nome}/run?mode=sync   # streaming direto no response
```

`mode=sync` é **single-output** (um formato, response body, sem compressão de múltiplos arquivos). Compiler valida e rejeita multi-output em sync com `400`.

---

## D9 — `Abstractions` mínimo e congelado (typed-only)

**Decisão.** SemVer estrito; tratar como ABI. Superfície da v1:

```
Schema/        ColumnType · ReportColumn · ReportSchema
Data/          ReportBatch<T>
Execution/     ReportExecutionContext · JobPriority
Sources/       IReportSource · IBatchSource<T> · IStreamingSource<T>
               BatchContext · BatchResult<T>     (Cursor = string?)
Formats/       IReportWriter · WriterContext     (writer não-genérico; recebe object?[] + schema)
Destinations/  IReportDestination · ReportFile · DestinationContext · UploadResult
Resilience/    IFailureStrategy · BatchFailureContext · FailureDecision · FailureAction
Jobs/          IReportJobScheduler · IJobStore · ReportJob · ReportJobRequest
               ReportJobStatus · JobStats · ICheckpointStore (contrato, no-op v1)
Extensibility/ ISourceFactory · IWriterFactory · IDestinationFactory
Exceptions/    NeoReportsException · BatchFailedException · SourceFailedException
               · ThresholdExceededException · ConfigurationException
```

Removido da v1 vs. Cap. 16: `IRetryPolicy`, `IExceptionClassifier`, `IAuthProvider*` (host auth basta), `IReportConfigParser` + DTOs de config/variant, `ReportRecord` posicional, `JobEvent`/`JobEventType` completo, `IPaginationStrategy` público (interno por ora).

---

## D10 — Filtro/transform tipados; sem expressões dinâmicas na v1

**Decisão.** Filtro e transform são **delegates C# tipados** (`Func<T,bool>`, `Func<T,T>`) declarados no builder. JsonLogic e DynamicLinq saem da v1 (eram do caminho dinâmico, cortado em D1).

**Por quê.** Code-first não precisa de avaliador de expressão — o filtro *é* código C# compilado, rápido e seguro. Expressões dinâmicas voltam junto com o caminho dinâmico, pós-MVP.

---

## Escopo concreto da v1 (o que entra)

| Camada | Entra na v1 | Fica pra depois |
|---|---|---|
| Paradigma | Code-first tipado (`.AddReport` + `.From<T>`) | Endpoint de config dinâmica, builder visual, UI |
| Sources | SQL (`IBatchSource<T>`, keyset) | HTTP, File, Mongo, Custom |
| Formats | CSV, XLSX | PDF, JSON, XML, Parquet |
| Destinations | Local, S3 | SharePoint, Azure, GDrive, FTP, Email, Webhook |
| Jobs | Hangfire single-server, InMemory | Multi-worker, Quartz, MassTransit, Azure Functions |
| Resiliência | Polly + Abort/SkipAndLog + threshold | Pause/Review, FallbackToCache, dead-letter |
| Auth | Herda do host | Filter chain, signed URLs, per-area/action |
| Estrutura | Reports independentes | Pipeline + variants + coalescência |
| Checkpoint | Contrato no-op; restart-do-zero | Resume mid-job; multi-worker |
| Filtro | Delegates C# tipados | JsonLogic / DynamicLinq (caminho dinâmico) |

**Não entra na v1:** UI Blazor, caminho dinâmico, variants/coalescência, multi-worker, auth chain, SharePoint, templates `dotnet new`, PDF, config YAML/TOML, dashboard de métricas.

---

## Handoff do Claude Design (já feito) → Claude Code

**Estado:** o design das telas já está pronto no projeto Claude Design (Claude Design System — Anthropic Sans, CSS variables, paleta oficial, ícones Tabler outline, flat). **A UI continua pós-MVP** — este handoff é preparação para a fase de UI, não para a v1.

**Stack-alvo da UI:** Blazor Server + MudBlazor (+ ApexCharts).

**Pedir ao projeto de design que exporte, nesta ordem de prioridade:**

1. **`tokens.css`** — todos os tokens do Design System (cores, tipografia, espaçamento, raios, sombras) como CSS custom properties nomeadas, arquivo único. Vira tema MudBlazor.
2. **`components.html`** — catálogo de cada componente reutilizável em **todas as variantes e estados** (default/hover/active/disabled/loading/empty/error). Mínimo: MetricCard, StatusBadge (queued/running/completed/failed/paused/retrying/cancelled), ProgressBar, PhaseStepper, WizardStepper, FilterBar, ReportCard, SourceCard, DestinationCard, FormatCard, DataGrid (header+rows), Timeline/EventRow, EmptyState, Banner/Alert, NavBar, SubNav, Chip/Tag, Switch. Classes nomeadas e estáveis, **sem estilo inline**.
3. **Um `.html` por tela** (as 17) — markup semântico que **referencia** as classes do catálogo (não recopia estilo); só layout/composição/grid.
4. **`handoff.md`** — tabela `tela → rota → componentes usados → endpoint que alimenta → estados a tratar`, mais breakpoints responsivos e lista de ícones Tabler.

**Regras de formato (minimizam trabalho no Claude Code):** CSS externo só, zero inline style; classes que mapeiam 1:1 pra nome de componente; HTML semântico (`button`/`table`/`nav`/headings, sem div-soup); nenhuma suposição de comportamento JS (interatividade é do Blazor). **Evitar:** screenshots/PNG, Figma, HTML gigante com estilo inline.

---

## Sequência sugerida (solo, tempo parcial)

1. `Abstractions` mínimo (D9) + `Core` (builder fluente genérico `<T>` + pipeline batch + projeção compilada + Polly).
2. `Sources.Sql` (keyset) + `Formats.Csv` + `Destinations.Local`. **Primeiro report tipado end-to-end rodando.**
3. `Formats.Xlsx` (ClosedXML) + `Destinations.S3` (upload tudo-ou-nada).
4. `Jobs.Hangfire` (single-server) + `Jobs.InMemory` + `IJobStore`.
5. `AspNetCore`: endpoints de disparo async/sync de reports registrados. **MVP demonstrável.**
6. Validar com usuários reais antes de UI / caminho dinâmico / variants / multi-worker.

---

## Tabela-resumo das decisões

| # | Tema | Decisão |
|---|---|---|
| D1 | Registro | Pipeline genérica `<T>` tipada; projeção pra `object?[]` só na borda do writer; sem dicionário; dinâmico pós-MVP |
| D2 | Worker | Único / vertical; Hangfire single-server; job atômico; restart-do-zero; multi-worker e resume pós-MVP |
| D3 | Cursor | Token opaco serializável (`string?`) |
| D4 | Stream vs Batch | Batch canônico; streaming adaptado |
| D5 | Variants/coalescência | Fora da v1 |
| D6 | Resiliência | Polly direto; só `IFailureStrategy` + threshold como abstração própria |
| D7 | Naming | `ExecutionContext` → `ReportExecutionContext` |
| D8 | Disparo/sync | Reports registrados por nome; sem config dinâmica; sync = single-output |
| D9 | Abstractions | Mínimo typed-only, congelado, SemVer estrito |
| D10 | Filtro | Delegates C# tipados; JsonLogic/DynamicLinq pós-MVP |
| — | Design | Já feito no Claude Design; exportar conforme handoff; UI pós-MVP |

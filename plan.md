# NeoReports — Plano de Implementação (v1)

PRs pequenos e independentes, em ordem. Cada um fecha com testes verdes e fecha um critério de aceite (CA-n) da `docs/MVP-Spec.md`. Marque o checkbox ao concluir.

## PR 0 — Bootstrap do repositório
- [ ] `global.json`, `build/Directory.Build.props`, `build/Directory.Packages.props`, `.editorconfig`, `.gitignore`.
- [ ] `NeoReports.sln` com solution folders espelhando `src/ tests/ benchmarks/ samples/`.
- [ ] CI mínimo (`dotnet build` + `dotnet test` + `dotnet format --verify-no-changes`).
- **Aceite:** `dotnet build` e `dotnet test` passam num repo vazio.

## PR 1 — NeoReports.Abstractions
- [ ] Tipos e interfaces typed-only conforme D9 (já esqueletados em `src/NeoReports.Abstractions/`).
- [ ] XML docs em inglês em tudo que é público.
- **Aceite:** compila multi-target (net8/net9), sem dependências além de `Logging.Abstractions`.
- **Depende de:** PR 0.

## PR 2 — NeoReports.Core: builder + pipeline batch
- [ ] Fluent builder genérico `ReportBuilder<TRow>` (`From/Map/Filter/Columns/To/UploadTo/Retry/OnFailure`).
- [ ] `IReportRegistry` + `AddReport<TRow>(...)` (DI).
- [ ] `ReportPipeline`: loop de batches, `StreamingToBatchAdapter`, projeção compilada `T → object?[]` na borda do writer (Expression-compiled getters por coluna).
- [ ] Integração Polly v8 (`ResiliencePipeline`) no read de batch.
- [ ] `IFailureStrategy`: `AbortReport`, `SkipBatchAndLog`; `ThresholdMonitor` (consecutivas/total/razão).
- **Aceite:** CA-1, CA-11, CA-12, CA-13, CA-14. Pipeline testado com source fake em memória.
- **Depende de:** PR 1.

## PR 3 — Sources.Sql + Formats.Csv + Destinations.Local (primeiro end-to-end)
- [ ] `Source.Sql(...).Keyset(key, pageSize)` — `IBatchSource<T>`, conexão por página, cursor `string?`, parâmetros parametrizados.
- [ ] `Format.Csv(...)` — writer não-genérico (delimitador, encoding, cabeçalho via `DisplayName`, formatação por cultura).
- [ ] `Destination.Local(pathTemplate)` — tokens `{name}/{date}/{ext}`.
- [ ] Sample `01-sql-to-csv-local`.
- **Aceite:** CA-2, CA-4, CA-7. Report de referência roda fim-a-fim em CSV+Local. SQL testado com Testcontainers.
- **Depende de:** PR 2.

## PR 4 — Formats.Xlsx + Destinations.S3
- [ ] `Format.Xlsx(...)` com ClosedXML (aba, auto-filtro, tipos nativos).
- [ ] Multi-output numa passada (CSV + XLSX lendo a source uma vez).
- [ ] `Destination.S3(bucket, keyTemplate)` — upload tudo-ou-nada (sem objeto parcial em falha).
- [ ] Sample `02-sql-to-xlsx-s3`.
- **Aceite:** CA-5, CA-6, CA-8.
- **Depende de:** PR 3.

## PR 5 — Memória constante (validação)
- [ ] `NeoReports.Benchmarks` com `MemoryDiagnoser`: source sintética de 1M linhas → CSV/XLSX.
- [ ] Ajustes de buffering/flush se o benchmark mostrar crescimento.
- **Aceite:** CA-3 (alocação ~constante).
- **Depende de:** PR 4.

## PR 6 — Jobs: worker único
- [ ] `IJobStore` + `ICheckpointStore` (no-op) + `Jobs.InMemory`.
- [ ] `Jobs.Hangfire` single-server (storage SQL/SQLite); `IReportJobScheduler`.
- [ ] Cancelamento cooperativo (`CancellationToken` + flag); restart idempotente (temp local + publicação atômica no fim).
- **Aceite:** CA-15, CA-16; status `queued→running→completed`.
- **Depende de:** PR 2.

## PR 7 — Integrations.AspNetCore: endpoints de disparo
- [ ] `MapNeoReports("/api")`: `run` (async/sync), `GET /reports`, `GET /jobs/{id}`, `cancel`, `download`.
- [ ] Validação: sync rejeita multi-output (`400`); auth herda do host.
- [ ] Sample `03-async-job-hangfire`.
- **Aceite:** CA-9, CA-10. **MVP demonstrável.**
- **Depende de:** PR 6, PR 4.

## PR 8 — Polimento de release OSS
- [ ] README, LICENSE (MIT), CHANGELOG, empacotamento NuGet (symbols/snupkg), README por pacote.
- **Aceite:** `dotnet pack` gera todos os pacotes; samples documentados.
- **Depende de:** PR 7.

---

## Pós-MVP (não começar antes de validar com usuários)

Ordem provável quando houver tração: caminho dinâmico (config + JsonLogic) → **UI Blazor a partir do handoff do Claude Design** → variants/pipeline → multi-worker + resume mid-job → demais sources/formatos/destinos. Lembrete: qualquer trabalho de UI parte sempre dos entregáveis do Claude Design, nunca de design inventado.

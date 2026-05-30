# NeoReports — Plano de Implementação (v1)

PRs pequenos e independentes, em ordem. Cada um fecha com testes verdes e fecha um critério de aceite (CA-n) da `docs/MVP-Spec.md`. Marque o checkbox ao concluir.

## PR 0 — Bootstrap do repositório
- [x] `global.json`, `build/Directory.Build.props`, `build/Directory.Packages.props`, `.editorconfig`, `.gitignore`.
- [x] `NeoReports.sln` com solution folders espelhando `src/ tests/ benchmarks/ samples/`.
- [x] CI mínimo (`dotnet build` + `dotnet test` + `dotnet format --verify-no-changes`).
- **Aceite:** `dotnet build` e `dotnet test` passam num repo vazio.

## PR 1 — NeoReports.Abstractions
- [x] Tipos e interfaces typed-only conforme D9 (já esqueletados em `src/NeoReports.Abstractions/`).
- [x] XML docs em inglês em tudo que é público.
- **Aceite:** compila multi-target (net8/net9), sem dependências além de `Logging.Abstractions`.
- **Depende de:** PR 0.

## PR 2 — NeoReports.Core: builder + pipeline batch
- [x] Fluent builder genérico `ReportBuilder<TRow>` (`From`/`Filter`/`Columns`/`Column`/`To`/`UploadTo`/`Retry`/`OnFailure`; mapeamento via `From(source, map)` — ver D12).
- [x] `IReportRegistry` + `AddReport<TRow>(...)` (DI).
- [x] `ReportRunner`/pipeline: loop de batches, `TypedBatchReader` (adapta streaming → batches), projeção `T → object?[]` na borda do writer.
- [x] Integração Polly v8 (`ResiliencePipeline`) no read de batch.
- [x] `IFailureStrategy`: `AbortReport`, `SkipBatchAndLog`; threshold (consecutivas/total/razão) via `AbortIf` (ver D11).
- **Aceite:** CA-1, CA-11, CA-12, CA-13, CA-14. Pipeline testado com source fake em memória. ✅ 13 testes verdes.
- **Depende de:** PR 1.

## PR 3 — Sources.Sql + Formats.Csv + Destinations.Local (primeiro end-to-end)
- [x] `Source.Sql(...).Keyset(key, pageSize)` — `IBatchSource<T>`, conexão por página, cursor `string?`, parâmetros parametrizados (bind automático só do que a query referencia).
- [x] `Format.Csv(...)` — writer não-genérico (delimitador, encoding, cabeçalho via `DisplayName`, formatação por cultura/format, escaping RFC 4180, CRLF, UTF-8 sem BOM).
- [x] `Destination.Local(pathTemplate)` — tokens `{name}/{date[:fmt]}/{ext}` + parâmetros; publicação atômica (temp + move).
- [x] Sample `01-sql-to-csv-local`.
- **Aceite:** CA-2, CA-4, CA-7. Report de referência roda fim-a-fim em CSV+Local. SQL testado com Testcontainers. ✅ 26 testes verdes (13 Core + 4 CSV + 6 Local + 3 SQL/E2E).
- **Depende de:** PR 2.

## PR 4 — Formats.Xlsx + Destinations.S3
- [x] `Format.Xlsx(...)` com ClosedXML (aba, auto-filtro, tipos nativos; formato/data por coluna). Memória cresce com as linhas — ver D14.
- [x] Multi-output numa passada (CSV + XLSX lendo a source uma vez) — provado no E2E `Csv_and_xlsx_are_generated_reading_the_source_once`.
- [x] `Destination.S3(bucket, keyTemplate)` — upload tudo-ou-nada via `PutObject` (sem objeto parcial em falha) — ver D15.
- [x] Sample `02-sql-to-xlsx-s3`.
- **Aceite:** CA-5, CA-6, CA-8. ✅ Total acumulado de testes verdes: 34 (13 Core + 4 CSV + 6 Local + 4 Xlsx + 3 S3 + 4 SQL/E2E).
- **Depende de:** PR 3.

## PR 5 — Memória constante (validação)
- [x] `NeoReports.Benchmarks` com `MemoryDiagnoser`: source sintética (lazy, página a página) de 100k e 1M linhas → CSV/XLSX.
- [x] Nenhum ajuste de buffering necessário: alocação por linha já constante (~446 B/linha @100k vs ~461 B/linha @1M — linear, não super-linear).
- **Aceite:** CA-3 (alocação ~constante). ✅ comprovado. CSV é streaming; XLSX cresce com o volume por design do ClosedXML (D14).
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

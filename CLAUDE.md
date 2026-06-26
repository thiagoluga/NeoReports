# CLAUDE.md — NeoReports

Guia de trabalho lido automaticamente a cada sessão. Leia também `NeoReports-Decisoes.md` (decisões cravadas) e `docs/MVP-Spec.md` (o que a v1 entrega).

## Visão

NeoReports é uma biblioteca .NET OSS (MIT) para geração de relatórios a partir de fontes de dados, com fluent API, streaming de memória constante, resiliência e upload para destinos. A v1 é um MVP enxuto, code-first tipado, construído por um único mantenedor.

## Escopo da v1 (não expandir sem decisão registrada)

**Dentro:** code-first tipado · source SQL (keyset) · formatos CSV e XLSX · destinos Local e S3 · jobs com worker único (Hangfire single-server + InMemory) · resiliência Polly + `IFailureStrategy` (Abort / SkipBatchAndLog) · endpoints de disparo de reports registrados (async/sync).

**Fora (pós-MVP, não implementar):** caminho dinâmico (config JSON/UI), avaliação de expressões (JsonLogic/DynamicLinq), variants/coalescência, multi-worker e resume mid-job, UI Blazor, auth chain, SharePoint, PDF, templates `dotnet new`, config YAML/TOML, dashboard de métricas.

Se algo fora de escopo parecer necessário, **pare e registre uma decisão** em `NeoReports-Decisoes.md` antes de codar.

## Regras de arquitetura inegociáveis

1. **Typed-only.** A pipeline é genérica sobre `T`. O registro é o próprio POCO. **Nunca** usar `IDictionary<string,object?>` como tipo de linha.
2. **Batch é o modelo canônico.** Tudo a jusante consome `ReportBatch<T>`. `IStreamingSource<T>` é adaptado em batches, não tem caminho de execução próprio.
3. **Projeção só na borda do writer.** Leitura/map/filter operam em `T` sem boxing. A conversão para `object?[]` (ordem do schema) acontece imediatamente antes de escrever. Writers são **não-genéricos** e recebem `(object?[] linha, ReportSchema)`.
4. **Cursor é `string?` opaco serializável.** A source codifica/decodifica seu cursor interno. Nunca `object?`.
5. **Polly direto.** Resiliência usa `Polly v8` (`ResiliencePipeline`). Não criar `IRetryPolicy`/`IExceptionClassifier`. A única abstração própria é `IFailureStrategy` (decisão após esgotar tentativas) + threshold.
6. **Worker único / vertical.** Job é unidade atômica; se cair, reinicia do zero (idempotente). `ICheckpointStore` existe como contrato, mas é no-op na v1.
7. **`Abstractions` é congelado.** Tratar `NeoReports.Abstractions` como ABI: SemVer estrito, superfície mínima. Toda interface ali é um passivo — não adicionar nada que o MVP não use.
8. **Memória constante.** Nada de materializar o report inteiro em memória. Streaming source → batch → writer → stream de saída.

## Convenções de código

- **.NET 8 e 9** (multi-target no Core e Abstractions). `LangVersion=latest`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`.
- **Identificadores e XML doc comments em inglês** (é uma lib OSS pública). Discussão interna/docs de processo podem ser em PT.
- `file-scoped namespaces`, `sealed` por padrão em classes não desenhadas para herança, `record` para DTOs imutáveis, `init`-only properties.
- Async em tudo que faz I/O, sempre com `CancellationToken` como último parâmetro.
- Sem dependências externas em `Abstractions` além de `Microsoft.Extensions.Logging.Abstractions`.
- Central Package Management: versões em `build/Directory.Packages.props`, nunca inline no `.csproj`.

## Estrutura de pastas

```
build/        Directory.Build.props · Directory.Packages.props · .editorconfig (na raiz)
src/          NeoReports.Abstractions · NeoReports.Core · Sources/* · Formats/* · Destinations/* · Jobs/* · Integrations/*
tests/        *.UnitTests · *.IntegrationTests · NeoReports.TestKit
benchmarks/   NeoReports.Benchmarks
samples/      01-sql-to-csv-local · 02-sql-to-xlsx-s3 · 03-async-job-hangfire
docs/         MVP-Spec.md
plan.md                  (plano de PRs, na raiz)
NeoReports-Decisoes.md   (ADR)
global.json
```

## Comandos

```bash
dotnet build                       # build da solution
dotnet test                        # todos os testes
dotnet test tests/NeoReports.Core.UnitTests
dotnet format                      # aplica .editorconfig
dotnet run --project benchmarks/NeoReports.Benchmarks -c Release
```

## Estratégia de testes (inclua em cada PR)

- **xUnit + NSubstitute.** Asserções com Shouldly (MIT; FluentAssertions saiu na v8 por virar licença comercial — ver `NeoReports-Decisoes.md`).
- **Writers: golden-file tests.** Saída comparada byte-a-byte / linha-a-linha com arquivo de referência versionado.
- **SQL source: Testcontainers** (SQL Server/Postgres efêmero), não mock de banco.
- **Memória: BenchmarkDotNet com `MemoryDiagnoser`** num report de 1M linhas — provar alocação ~constante (critério de aceite do MVP).
- **Resiliência:** source que falha N vezes e depois recupera; cobrir Abort e SkipBatchAndLog + thresholds.
- Cada PR só fecha com testes passando. Nunca marcar tarefa como concluída com teste vermelho.

## Glossário de domínio

- **Report** — definição de uma extração (source + map + filtro + outputs + destinos), registrada em código por nome.
- **Pipeline** — execução de um report: lê batches, processa, escreve, faz upload.
- **Batch** — página de registros tipados (`ReportBatch<T>`); unidade de retry e progresso.
- **Cursor** — token opaco (`string?`) de paginação keyset.
- **Source** — origem de dados (`IBatchSource<T>` / `IStreamingSource<T>`).
- **Writer** — serializador de formato (CSV, XLSX); não-genérico, recebe `object?[]` + schema.
- **Destination** — destino de upload do arquivo final (Local, S3).
- **Job** — instância agendada/enfileirada de execução, com status persistido.
- **FailureStrategy** — o que fazer depois que o retry de um batch esgota (Abort / SkipBatchAndLog).

## Design / UI — regra permanente

A UI é **pós-MVP** e não deve ser implementada na v1. **Quando chegar a hora de criar qualquer coisa de design/UI, baseie-se sempre no que já foi produzido no projeto Claude Design** (Claude Design System: Anthropic Sans, CSS variables, paleta oficial, ícones Tabler outline, estética flat). Não inventar design novo nem divergir dos tokens/componentes de lá.

O handoff esperado do Claude Design são quatro entregáveis (`tokens.css`, `components.html`, um `.html` por tela, `handoff.md`) — detalhe na seção "Handoff do Claude Design" do ADR. Stack-alvo: Blazor Server + MudBlazor. Consumir esses arquivos como fonte da verdade visual; o código apenas traduz para componentes Blazor.

## Como trabalhar

- Siga `plan.md` em ordem; um PR por item, pequeno e independente.
- Todo PR fecha um critério de aceite da spec e vem com testes.
- Mudou uma decisão? Atualize `NeoReports-Decisoes.md` no mesmo PR.

## Permissões permanentes do agente (concedidas pelo mantenedor)

O agente tem permissão **permanente** (tem permissão pra tudo no ciclo abaixo, sem pedir confirmação a cada vez):

### Resumo — o que faço sozinho vs. o que precisa de você

| Faço com autonomia | Preciso da sua confirmação | Nunca faço (mesmo se pedir) |
|---|---|---|
| Ler/buscar código, explorar o repo | **Merge em `master`** | Inserir credenciais/senhas/tokens/cartão |
| Criar branch, editar arquivos | **Publicar pacote** (NuGet) / tag de release | Mexer em controle de acesso/permissões de terceiros |
| `build` / `test` / `format` / scan Sonar / rodar sample | Apagar branch remota, force-push, reescrever history | Apagar dados permanentemente |
| `commit` + `push` na branch da tarefa | Ações destrutivas/irreversíveis (drop de schema, `reset --hard` em remoto, mudar visibilidade do repo) | Transações financeiras |
| Abrir PR (`gh pr create`) | Adicionar **nova dependência** (vai no CPM) | Resolver CAPTCHA, mudar settings de segurança |
| Acompanhar CI, ler/responder comentários do PR | Qualquer coisa "para fora" além do PR | — |
| Atualizar docs (plan/ADR/CHANGELOG) via PR | — | — |

> O **merge é sempre seu** — eu nunca mergeio. O app do Claude Code ainda pode te mostrar um prompt do harness para autorizar alguns comandos (push, etc.); isso é a UI de permissões, separada deste acordo de fluxo.

Detalhe dos itens autônomos:

- **Leitura irrestrita.** Qualquer comando de leitura/inspeção em qualquer parte do sistema: ler/listar/buscar arquivos, `git` de leitura (`status`, `log`, `diff`, `show`, `branch`, `ls-files`, …), inspecionar build/testes, etc.
- **Inspecionar arquivos, rodar quaisquer testes e analisar os resultados.**
- **Executar e acompanhar os checks/CI.** Disparar/aguardar o CI, ler os resultados (`gh run`, `gh pr checks`) e diagnosticar falhas.
- **Ler e responder comentários do PR no GitHub** (`gh pr view`/`comment`, review comments).
- **Criar branch e escrever o código necessário.**
- **Commit, push e abrir PR** (`git commit`, `git push`, `gh pr create`).
- **Esperar o CI, corrigir o que for necessário, commitar e fazer push de novo** — iterar até o PR ficar verde.

### Ciclo de cada task (executar autonomamente, sem pedir confirmação)

Para cada item do `plan.md` (ou tarefa equivalente), seguir este ciclo de ponta a ponta:

1. **Criar a branch** a partir do `master` atualizado (`git branch --show-current` p/ confirmar que não está no master antes de codar/commitar).
2. **Escrever todo o código necessário** (e os testes).
3. **Clean build / rebuild quando necessário** — na dúvida sobre cache, apagar `obj/bin` ou usar `--no-incremental`.
4. **Criar e executar todos os testes**; ler a linha real `Passed!/Failed!` e `Build succeeded/FAILED`.
5. **Se tudo estiver verde** (0 falhas, build ok): **commit, push e abrir o PR**.
6. **Após abrir o PR, acompanhar os check-runs até concluírem** (`check runs until done`) — confirmando que o `headRefOid` do PR == último commit.
7. **Se precisar refazer algum passo** (CI vermelho, conflito, etc.): corrigir, commitar e push de novo, e repetir 3–6 até o PR ficar verde. Tudo isso é permitido sem nova confirmação.

Só **parar e pedir confirmação** no final (merge) e nas exceções abaixo.

Ainda exigem confirmação explícita: **merge de PR**, deletar branch remota de terceiros, force-push, e ações destrutivas/irreversíveis (`reset --hard` em remoto, mudar visibilidade do repo, dropar schema, etc.).

## Lições operacionais (erros já cometidos — não repetir)

1. **Confirme a branch ANTES de commitar.** Sempre `git branch --show-current` antes de `git add`/`commit`. Já commitei direto no `master` por engano (só não quebrou porque sou admin). Todo trabalho em branch própria.
2. **NUNCA commite/pushe com build ou teste vermelho.** Antes de `git commit`, leia a linha real `Build succeeded`/`Passed!`/`Failed!` do escopo alterado. Se houver `Failed: N>0` ou `Build FAILED`, não commite. Já afirmei "verde" sem verificar e mergeei PR vermelho — inaceitável.
3. **Build local incremental MENTE.** `obj/bin` em cache já mascarou erro real de compilação (CS0246) que só apareceu no CI. Na dúvida, `--no-incremental` ou apague `obj/bin` e rebuilde do zero antes de confiar no verde.
4. **Edit que falha com "file modified since read" NÃO foi aplicado.** Re-leia e refaça; nunca assuma que entrou. Docs (ADR/plan) já não entraram em commits por isso.
5. **Um comando por vez ao diagnosticar.** Lotes grandes em paralelo cancelam em cascata no primeiro erro e corrompem a visão de estado.
6. **CI tem cache de status.** `gh pr checks` / `commits/<sha>/check-runs` pode mostrar run antigo. Confirme `headRefOid` do PR == seu último commit antes de confiar no resultado.
7. **Resultados podem chegar fora de ordem.** Ao notar incoerência, PARE e rode uma checagem sequencial `git branch/status/log` antes de agir.
8. **Um PR por vez.** Não abrir vários simultâneos sem o mantenedor pedir; fechar (mergear) o atual antes do próximo.
9. **Novas dependências (inclusive de teste) vão no CPM** (`build/Directory.Packages.props`). `NU1010` = `PackageVersion` faltando.
10. **`dotnet format --verify-no-changes` dá falso-positivo local** por autocrlf (CRLF no working tree vs LF no repo); o CI faz checkout LF e passa. Não persiga `ENDOFLINE` local; confie no step de format do CI.

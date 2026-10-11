# JobMatch Ghana

A WhatsApp-based conversational AI platform that matches informal-sector workers in Ghana to job
opportunities through NLP-driven skills elicitation. MSc Computer Science capstone artefact
(Design Science Research).

> **Research question:** how effective are conversational AI interfaces for skills-based job
> matching in Ghana's informal/gig economy?

## Status

| Slice | Scope | State |
|-------|-------|-------|
| Phase 1 | Webhook + HMAC + bounded queue + `BackgroundService` + EF model + idempotency | ✅ built |
| Slice 1 | Deterministic FSM + Redis sessions replacing the echo orchestrator (NLP stubbed) | ✅ built |
| Slice 2 | Python FastAPI NLP service (intent + skill extraction) wired to the gateway | ⏳ pending |
| Slice 3 | Embedding-based matching engine + utility-template notifications | ⏳ pending |
| Slice 4 | Vue 3 admin SPA | ⏳ pending |
| Staging | Windows/IIS VPS at `jobmatch.intellite.net` ([guide](docs/vps-deployment.md), DL-012) | ⏸️ on hold until slices are done (scripts written) |
| Slice 5 | Hardening, seed taxonomy, Azure deployment | ⏳ pending |

## Solution layout

```
src/
  JobMatch.Domain          Entities, enums, conversation types, the pure FSM, ports
  JobMatch.Infrastructure  EF Core (SQL Server), Redis session store, WhatsApp + NLU clients, orchestrator
  JobMatch.Api             Webhook endpoints, signature validation, bounded queue, background consumer
tests/
  JobMatch.Tests           Signature, FSM, idempotency, and full webhook-pipeline tests
docs/
  decision-log.md               DL-001 … DL-012
  whatsapp-cloud-api-setup.md   Meta setup, diagnostics, troubleshooting, daily checklist
  vps-deployment.md             Windows/IIS VPS staging: server setup, releases, rollback
deploy/
  Publish.ps1                   Build a release zip (tests + publish + idempotent migrate.sql)
  server/                       VPS scripts: Setup-Server, Set-AppSettings, Deploy-Release, SQL
```

The conversation brain is split deliberately:
- **`JobMatch.Domain/Conversation/ConversationFsm.cs`** — a **pure**, I/O-free transition
  function. This is the research-critical, unit-tested core.
- **`JobMatch.Infrastructure/Orchestration/FsmConversationOrchestrator.cs`** — the impure shell:
  idempotency, session load/save, worker/skill persistence (consent-gated), instrumentation,
  and outbound sends.

## Conversation flow

```
NEW → CONSENT → LANGUAGE → PROFILE(name, location)
    → SKILLS_ELICITATION (loop: utterance → NLU extract → confirm/refine)
    → AVAILABILITY → ACTIVE
```

Global commands in any state: `help`, `stop` (opt-out), `skills` (update), `matches`.
Unrecognised input → a logged repair prompt (a research metric).

## Prerequisites

- .NET 8 SDK (the repo pins it via `global.json`)
- SQL Server 2022 (or LocalDB) — for persistence
- Redis — for conversation sessions

## Configure secrets (local)

WhatsApp credentials are **not** required for Slice 1: with an empty `AccessToken` the gateway
uses a logging (dry-run) WhatsApp client and the keyword NLU stub, so the whole flow runs locally.

```bash
cd src/JobMatch.Api
dotnet user-secrets init
dotnet user-secrets set "WhatsApp:AppSecret"     "<meta-app-secret>"
dotnet user-secrets set "WhatsApp:VerifyToken"   "<your-verify-token>"
dotnet user-secrets set "WhatsApp:PhoneNumberId" "<phone-number-id>"
dotnet user-secrets set "WhatsApp:AccessToken"   "<graph-access-token>"
```

Connection strings default to `localhost`; override `ConnectionStrings:SqlServer` and
`Redis:ConnectionString` as needed.

## Build, test, run

```bash
dotnet build
dotnet test                                   # 29 tests: signature, FSM, idempotency, pipeline
dotnet ef database update \                   # apply migrations to SQL Server
  --project src/JobMatch.Infrastructure --startup-project src/JobMatch.Api
dotnet run --project src/JobMatch.Api         # webhook on /webhook, health on /health
```

## Local end-to-end demo (no WhatsApp account needed)

With `WhatsApp:AppSecret` set locally, POST a signed payload to `/webhook` and watch the
`[WhatsApp:DRYRUN]` log lines show the bot's replies as it walks the FSM. The
`WebhookPipelineTests` integration test demonstrates the same path against a faked Graph API.

## Non-functional guarantees

- Webhook ACKs in < 2s; all processing is async (DL-002).
- Every Meta POST is signature-validated over the raw body (DL-003).
- At-least-once delivery is deduped idempotently (DL-004).
- No worker profile is persisted before explicit consent; opt-out deactivates the worker (DL-010).
- Every turn is instrumented (state, intent, NLU confidence, repair flag) for the evaluation dataset (DL-009).

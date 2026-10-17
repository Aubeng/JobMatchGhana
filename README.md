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
| Slice 2b | Dialogue aligned with the S&D HSM: S0–S5, buttons/lists, 3-strike handoff, update/delete ([design](docs/conversation-design.md), DL-015) | ✅ built |
| Slice 2 | NLU service: fine-tuned XLM-R intents + BERT+CRF entities, taxonomy/district resolution ([nlp/](nlp/README.md), DL-013/014) | ✅ built (data pending researcher validation) |
| Slice 3 | Matching engine: district filter + MiniLM ranking, S6 delivery, S7 feedback, new-job alerts (DL-016) | ✅ built (synthetic jobs) |
| Slice 4 | Admin SPA at `/admin`: jobs, follow-ups, metrics dashboard, annotation, skills supply, exports ([admin/](admin/README.md), DL-017) | ✅ built |
| Staging | Windows/IIS VPS at `jobmatch.intellite.net` ([guide](docs/vps-deployment.md), DL-012) | ⏸️ on hold until slices are done (scripts written) |
| Slice 5 | Hardening, seed taxonomy, Azure deployment | ⏳ pending |

## Solution layout

```
src/
  JobMatch.Domain          Entities, enums, conversation types, the pure FSM, ports
  JobMatch.Infrastructure  EF Core (SQL Server), Redis session store, WhatsApp + NLU clients, orchestrator
  JobMatch.Api             Webhook endpoints, signature validation, bounded queue, background consumer
tests/
  JobMatch.Tests           Signature, FSM, idempotency, NLU contract, and full webhook-pipeline tests
nlp/                       Python NLU service (FastAPI): models, D4 taxonomy + D5 training data, pytest
admin/                     Vue 3 admin SPA (served by the API at /admin)
docs/
  decision-log.md               DL-001 … DL-017
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

The S&D hierarchical state machine (DL-015; full design in [docs/conversation-design.md](docs/conversation-design.md)):

```
S0 WELCOME (consent) → S1 ELICIT_TRADE → S2 ELICIT_SKILLS → S3 ELICIT_EXPERIENCE (years, availability)
   → S4 ELICIT_LOCATION (district) → S5 CONFIRM_PROFILE → profile saved → S6 DELIVER_MATCH → S7 FEEDBACK
```

- Buttons and lists everywhere, with free text understood by the NLU. Slots given early are skipped.
- Below 0.75 confidence the bot asks back. 3 failed turns in a state → human handoff.
- From the summary, any slot can be changed; the profile is saved only when confirmed.
- Commands in any state: `help`, `stop`, `update`, `skills`, `matches`, `delete` (removes all data).

## Prerequisites

- .NET 8 SDK (the repo pins it via `global.json`)
- SQL Server 2022 (or LocalDB) — for persistence
- Redis — for conversation sessions
- Python 3.11+ — only to run the real NLU service (`nlp/`); without it the gateway uses the keyword stub

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
dotnet test                                   # 104 tests: signature, FSM, matching, admin API, contracts, pipeline
dotnet ef database update \                   # apply migrations to SQL Server
  --project src/JobMatch.Infrastructure --startup-project src/JobMatch.Api
dotnet run --project src/JobMatch.Api         # webhook on /webhook, health on /health
dotnet run --project src/JobMatch.Api -- seed-jobs        # load the 32 synthetic demo jobs
dotnet run --project src/JobMatch.Api -- alert-job 7      # send new-job alerts for job 7
dotnet run --project src/JobMatch.Api -- create-admin you@example.org   # admin SPA account
```

The admin SPA is at `/admin` once built (`cd admin && npm install && npm run build`); see
[admin/README.md](admin/README.md). Set `WhatsApp:DryRun=true` for demos: nothing is sent to
WhatsApp, whatever token is configured.

To use the real NLU models instead of the keyword stub, train and start the service
([nlp/README.md](nlp/README.md)), then run the gateway with `Nlu:UseStub=false`:

```bash
cd nlp && .venv/Scripts/python -m uvicorn jobmatch_nlp.app:app --host 127.0.0.1 --port 8000
dotnet run --project src/JobMatch.Api -- --Nlu:UseStub=false
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
- Every turn is instrumented (state, intent, NLU confidence, repair flag) for the evaluation dataset (DL-009);
  post-consent turns also log the NLU's own intent, entities, model version and latency (DL-014).
- If the NLU service is down or slow, the turn degrades to the keyword stub instead of failing (DL-013).

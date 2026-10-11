# JobMatch Ghana — Decision Log

Architecture Decision Records for the capstone artefact. Each entry records the decision,
the alternatives considered, and the rationale, so the Design Science Research narrative is
auditable. DL-001…DL-007 cover the Phase 1 walking skeleton; DL-008+ cover the delivery slices.

---

## DL-001 — Direct WhatsApp Cloud API integration (no BSP)
**Decision:** Integrate the Meta WhatsApp Cloud API / Graph API (v21.0) directly rather than
through a Business Solution Provider (BSP) such as Twilio or 360dialog.
**Alternatives:** BSP with a higher-level SDK; a third-party chatbot platform.
**Rationale:** Removes per-message BSP markup (cost discipline for a student pilot), keeps the
message contract transparent for the research write-up, and avoids vendor lock-in. Cost is the
direct WhatsApp conversation pricing only.

## DL-002 — Fast ACK + async processing via a bounded Channel + BackgroundService
**Decision:** The webhook validates and enqueues onto a bounded `System.Threading.Channels`
queue, then returns `200 OK` immediately (< 2s). A `BackgroundService` drains the queue and
runs the orchestrator in a per-message DI scope.
**Alternatives:** Synchronous processing in the request; an external broker (Azure Service Bus).
**Rationale:** Meta requires a sub-2s ACK or it retries. In-process Channels are zero-infra and
sufficient for a < 50-user pilot; an external broker is out of scope (no horizontal scale-out).

## DL-003 — Raw-body HMAC-SHA256 signature validation (constant-time)
**Decision:** Validate `X-Hub-Signature-256` by computing HMAC-SHA256 over the **raw** request
bytes with the app secret and comparing in constant time (`CryptographicOperations.FixedTimeEquals`).
**Alternatives:** Trust the source IP; re-serialise the parsed JSON before hashing.
**Rationale:** Re-serialisation changes bytes and breaks the HMAC; the raw body is authoritative.
Constant-time compare avoids timing side channels. Fails closed on any malformed input.

## DL-004 — Idempotency via a unique index on `MessageLog.WaMessageId`
**Decision:** Every inbound Meta message id is recorded; a filtered unique index
(`WHERE [WaMessageId] IS NOT NULL`) enforces once-only processing. A fast-path existence check
handles common redelivery; the unique index is the hard guarantee against races.
**Alternatives:** A dedicated dedupe table; Redis SETNX.
**Rationale:** At-least-once delivery means duplicates are expected. Reusing the audit table the
study needs anyway keeps the schema minimal. The filter lets multiple outbound rows carry a null id.

## DL-005 — `IConversationOrchestrator` abstraction (echo first)
**Decision:** The message pipeline depends on an `IConversationOrchestrator` port. Phase 1 shipped
an echo implementation to prove the pipeline end-to-end before dialogue logic existed.
**Alternatives:** Bake dialogue logic straight into the consumer.
**Rationale:** Lets each slice swap the brain without touching ingestion, and keeps the pipeline
testable with a trivial implementation. Slice 1 swaps in the FSM orchestrator (DL-008).

## DL-006 — EF Core code-first on SQL Server 2022
**Decision:** Relational persistence via EF Core code-first migrations on SQL Server 2022
(Azure SQL in production).
**Alternatives:** Dapper; a document store.
**Rationale:** The research data is relational (workers ↔ skills ↔ matches) and benefits from
constraints and unique indexes. Code-first keeps the schema versioned alongside the code.

## DL-007 — Secrets via user-secrets / App Service configuration
**Decision:** App secret, verify token, access token, and connection strings come from
user-secrets locally and App Service configuration in Azure. `appsettings.json` holds only
non-secret defaults and empty placeholders.
**Alternatives:** Commit secrets to config; a bespoke key file.
**Rationale:** Research ethics + basic security: credentials must never enter source control.

---

## DL-008 — Deterministic FSM + Redis session state (Slice 1)
**Decision:** Replace the echo orchestrator with a deterministic finite state machine
(`NEW → CONSENT → LANGUAGE → PROFILE → SKILLS_ELICITATION → AVAILABILITY → ACTIVE`). The
transition function (`ConversationFsm.Step`) is **pure** — no I/O — and returns the next session
plus the replies to send. Session state lives in Redis keyed by `wa_id` with a 24h TTL matching
the WhatsApp service window; the gateway owns all state and the NLP service stays stateless.
**Alternatives:** An LLM-driven free-form dialogue; storing session state in SQL; a workflow engine.
**Rationale:** A deterministic FSM is reproducible and directly measurable — essential for the
research question and for auditing consent. Purity makes every transition unit-testable in
isolation. Redis gives fast, expiring session storage without polluting the relational store.
A free-form LLM would be non-deterministic, costlier, and harder to evaluate.

## DL-009 — First-class instrumentation and repair-event logging
**Decision:** Every turn writes a `MessageLog` row capturing FSM state, detected intent, NLU
confidence, and an `IsRepair` flag. Unrecognised input produces a graceful repair prompt and is
logged as a repair event.
**Alternatives:** Log only errors; sample turns.
**Rationale:** These rows are the quantitative dataset for the mixed-methods evaluation
(completion rates, repair rates, confidence distributions). Losing them would invalidate the
study, so logging is a first-class, non-optional side effect of processing.

## DL-010 — Consent gate enforced at persistence, not just in dialogue
**Decision:** No `Worker` row (profile) is written until explicit consent is captured; message
audit logging (which stores only the `wa_id` string) is permitted pre-consent. Opt-out ("stop")
sets `Worker.IsActive = false` and moves the session to a terminal `OPTED_OUT` state.
**Alternatives:** Create the worker on first contact and set a consent flag later.
**Rationale:** Research ethics condition — "no profiling before consent". Gating at the
persistence boundary (not merely the UI flow) makes the guarantee structural and testable.

## DL-011 — Deterministic keyword NLU stub behind `INluClient` (Slice 1)
**Decision:** Slice 1 ships a keyword-based `StubNluClient` (encoding a Ghana informal-sector
skills lexicon) behind the same `INluClient` port the real FastAPI service will implement.
**Alternatives:** Block Slice 1 on the Python service; hardcode skills in the FSM.
**Rationale:** Lets the full conversation be demonstrated end-to-end now, exercises the
confirm/refine/repair paths deterministically in tests, and makes the Slice 2 swap a
one-line configuration change (`Nlu:UseStub = false`).

---

## DL-012 — Staging on the existing Windows/IIS VPS before Azure App Service
**Decision:** Before Slice 5's Azure deployment, host the gateway on the existing shared Intellite
VPS (Windows Server 2022, IIS 10, SQL Server 2022 Web, Redis for Windows) at
`https://jobmatch.intellite.net`. The app runs in-process under its own IIS app pool; settings and
secrets are environment variables on that app pool; SQL access is Windows authentication as the
app pool identity with read/write on its own database only; HTTPS is a Let's Encrypt certificate
via win-acme. Releases are immutable folders the site is re-pointed at, with idempotent
EF migration scripts applied first (`deploy/`, `docs/vps-deployment.md`).
**Alternatives:** Keep testing through an ephemeral dev tunnel; a Linux VPS with Docker Compose;
go straight to Azure App Service.
**Rationale:** Meta needs a stable public HTTPS callback URL. The dev tunnel's changing host name and
dropped sessions were the main obstacle to end-to-end testing with real phones. The VPS already
exists at no extra cost, and the whole stack already runs on Windows, so there's no
containerisation work. App Service on Windows is also IIS underneath, so the publish output,
`web.config` and migration approach carry over to Slice 5 unchanged; app-pool variables map to
App Service application settings (consistent with DL-007).
**Accepted risks:** (1) The in-memory queue (DL-002) loses unprocessed messages when IIS recycles or
a release is switched. Mitigated by setting the app pool to `AlwaysRunning`, no idle time-out, and
no periodic recycle, and by deploying at quiet times. (2) A shared server: isolated by a dedicated
app pool, a host-name/SNI binding, a least-privilege DB login, and a Redis key prefix. (3) SQL
Server was found unpatched (RTM). The server is shared and managed by the hosting provider, so
patching is theirs; confirm the patch status with them before pilot participants' data is stored.
**Status (2026-10-11):** paused. Deployment work resumes after Slices 2–5 are built; the scripts in
`deploy/` are written but have not yet been run on the server.

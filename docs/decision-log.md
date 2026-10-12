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

---

## DL-013 — NLU service: fine-tuned XLM-RoBERTa intents + BERT+CRF entities, as specified (Slice 2)
**Decision:** Implement L4 as the S&D report specifies: a fine-tuned XLM-RoBERTa classifier for the
9 intents (0.75 confidence threshold) and a BERT+CRF recogniser for the 4 entity types. Entity spans
are resolved to the ILO-aligned taxonomy (D4) and to official districts by exact-then-fuzzy matching.
It is a stateless FastAPI service (`nlp/`) that the gateway calls over HTTP; CPU-only training and
inference. The paraphrase-MiniLM-L6-v2 embeddings in the brief belong to the L5 matching engine
(Slice 3), not to L4.
**Alternatives:** Embedding nearest-neighbour intents with taxonomy lookup and no fine-tuning (cheaper,
but departs from the submitted design); a staged baseline-then-fine-tune approach; a hosted LLM
(non-deterministic, costly, and harder to evaluate).
**Rationale:** Matches the design examiners approved (SQ2). The research question concerns
transformer-based NLU on informal Ghanaian English. A word-level CRF with BIO-constrained
transitions always yields well-formed spans. Fuzzy resolution handles WhatsApp spelling variation.
CPU suffices for about 1.6k short utterances.
**Data (D4/D5):** taxonomy v1 (12 categories, 120 skills, ISCO-08), a locality → district gazetteer,
and 1,635 annotated utterances (150–270 per intent) were **drafted by Claude as synthetic data
pending researcher validation**: source (i) in Design §6.3.1. See `nlp/docs/data-review-checklist.md`.
Splits are assigned by text hash, so the test set stays fixed as data grows. Test scores on
synthetic data are an upper bound; the study metrics must be measured on annotated pilot messages.
**Graceful degradation:** if the service errors or times out (`Nlu:TimeoutSeconds`, 5s), the gateway
uses the keyword stub for that turn and logs it as `stub-fallback`.
**v1 results (synthetic test split, n=166; upper bounds):** intent accuracy 88.6% (target ≥85%);
skill extraction precision 90.6% on resolved skills (target ≥80%); entity span F1 0.853; NLU
latency p95 92 ms. Entity confidence uses CRF marginals. Emission softmax was uncalibrated, but
even the marginals don't separate right from wrong spans on synthetic data (see `nlp/README.md`).
**Dialogue changes that came with it:** a bare trade ("I'm a carpenter") now prompts for skills in
that trade instead of counting as a repair (S2 ELICIT_SKILLS). Every skill in one message is queued
and confirmed in turn: the one-pending-skill design silently dropped the second skill of "I do
roofing and make furniture". Bare menu digits are not sent to NLU.

## DL-014 — Log the NLU's own prediction separately from the FSM's decision
**Decision:** `MessageLog` gains `NluIntent`, `NluIntentConfidence`, `NluEntitiesJson`, `NluModelVersion`
and `NluLatencyMs` (migration `AddNluTelemetry`). Every free-text turn **after consent** is analysed
and logged, not only the turns where the FSM needs NLU. `IntentDetected` keeps the FSM's own
decision.
**Alternatives:** Call NLU only where the FSM consumes it (no accuracy data for other turns);
overwrite `IntentDetected` with the NLU label (loses the dialogue decision).
**Rationale:** The S&D metrics "Intent Recognition Accuracy" and "Entity Extraction Precision" need
the model's raw prediction for every message, so pilot messages can be annotated afterwards and
compared. The model version makes results attributable to a specific model (v1 synthetic vs v2
pilot-trained, D11). The consent gate (DL-010) is kept: nothing is sent for analysis before consent.

---

## DL-015 — Align the dialogue with the S&D hierarchical state machine
**Decision:** Replace the Slice 1 flow (consent → language → name → free location → skills loop →
availability) with the S&D design (Design §6.2, Figure 3): S0 WELCOME → S1 ELICIT_TRADE → S2
ELICIT_SKILLS → S3 ELICIT_EXPERIENCE (years + availability sub-machine) → S4 ELICIT_LOCATION
(district) → S5 CONFIRM_PROFILE, with correction paths from S5 back to any slot, the 0.75
confidence threshold, the 3-strike clarification loop ending in human handoff, WhatsApp Quick
Reply buttons and List Messages (NFR-07), 72-hour sessions (FR-09), profile update (FR-15),
"delete my data" (FR-16), and a 500-character cap per message (NFR-05). The profile is persisted
only on S5 confirmation (FR-10) via new `Worker` columns (migration `AlignDialogueWithSdHsm`).
Dropped skills are soft-removed (`WorkerSkill.RemovedUtc`). S6/S7 are defined and arrive with
matching in Slice 3. Details: `docs/conversation-design.md`.
**Alternatives:** Keep the Slice 1 flow and record the difference as a design iteration; align
later, after matching.
**Rationale:** The artefact evaluated in the pilot should be the one the S&D describes and the
examiners approved. Aligning before matching means Slice 3 builds on the final profile shape:
trade, district and experience are what L5's geographic filter and ranking consume.
**Decisions within it:**
- The language and name steps are removed (English-only per FR-03; name optional per NFR-09).
- Slots volunteered early are filled and skipped (mixed initiative), so a profile takes about 9 turns.
- The menus come from the same taxonomy and gazetteer files the NLU resolves against
  (`trade_menu`, `location_menu`).
- The gateway's offline NLU stub now matches exact taxonomy phrases, so the fallback produces
  canonical names.
- NLU EXIT is confirmed before opting out, because the data labels pauses ("later") as EXIT.
- Taps and bare menu numbers aren't sent to NLU.
- Sessions in retired states are restarted.
**Accepted cost:** "delete my data" also removes that worker's `MessageLog` rows, as FR-16
requires; the consented research dataset shrinks accordingly.

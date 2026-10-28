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

---

## DL-016 — Matching engine (Slice 3): SQL geographic filter + MiniLM ranking, S6/S7 delivery and feedback
**Decision:** Implement S&D L5 as specified. **Stage 1** runs in the gateway's SQL query: open,
unexpired jobs in the worker's district or an adjacent one (`nlp/data/district_adjacency_v1.json`,
made symmetric), with compatible availability, not already shown in conversation. **Stage 2** runs in
the Python service (`POST /match`): cosine similarity between paraphrase-MiniLM-L6-v2 embeddings of
the profile text (trade + skills) and each job text, top 3 at or above `Matching:MinScore` (0.55, from
the brief). Each match stores its full breakdown in `Match.ScoreBreakdownJson`: ranker, score,
closest skill, explicit overlap, same or adjacent district, availabilities, synthetic flag.
S6 sends each job as its own message (FR-12: type, area, pay, availability) with a plain-language
reason (FR-13) and an "I'm interested" button. S7 asks a 1–5 rating (FR-14) and an optional comment,
stored on every delivered match. Migration `MatchingEngine`.
**Alternatives:** Do all matching in .NET with embeddings stored per job; a weighted score mixing
skills, distance and experience; per-job ratings.
**Rationale:**
- The S&D places the matching microservice in Python and ranks by cosine alone. Keeping that makes
  the evaluated algorithm the documented one.
- Stage 1 belongs where the jobs are (SQL), and stage 2 where the model is.
- Rating the delivered set once, rather than each job, keeps S7 to one tap for low-literacy users;
  the rating is recorded on each match row for the Matching Relevance metric.
**Further decisions:**
- *Availability* is a stage 1 filter: a full-time worker can take any job, part-time fits part-time
  or weekend jobs, weekends-only fits weekend jobs, and "Flexible" fits everyone. Matching uses only
  skills, location and availability (NFR-13); nothing else about the worker reaches the ranker.
- *Contact:* employers' numbers are never sent. "I'm interested" marks the match `Interested` and
  the BOOST team connects the two people. This is safer for both sides and avoids unverified
  numbers in chats.
- *Graceful degradation:* if `/match` fails, an offline ranker uses exact skill overlap. It is
  stricter (at least one shared skill) and is labelled in the breakdown.
- *New-job alerts:* `JobAlertService` runs the same two stages in reverse for one new job and sends
  each suitable confirmed worker the utility template `job_match_alert` ("{{1}} in {{2}}", with a quick
  reply that sends "matches"). At most one alert per worker per 24h; these templates are the only
  business-initiated messages. It is triggered by `alert-job <id>` until the admin SPA (Slice 4).
  The template must be created and approved in Meta before use.
- *Data:* 32 **synthetic** jobs (`Seed/jobs_synthetic_v1.json`, loaded by `seed-jobs`, flagged
  `IsSynthetic`) and the adjacency map are drafts for researcher validation. Matches against
  synthetic jobs are never study results.

---

## DL-017 — Admin SPA (Slice 4): same-origin Vue app, Identity cookie, research tooling built in
**Decision:** S&D layer L6 is a Vue 3 + Vite + TypeScript SPA (`admin/`), built into the API's
`wwwroot/admin` and served at `/admin` by the same ASP.NET Core app, not by Azure Static Web Apps.
Admins sign in with ASP.NET Core Identity:
- a session cookie (HttpOnly, SameSite=Strict, Secure outside development, 8-hour sliding expiry);
- lockout after 5 failures;
- 12+ character passphrases, with no composition rules (NIST SP 800-63B);
- a custom-header CSRF guard on every write;
- accounts created with `create-admin`.

Pages:
- job CRUD with publish (which opens the job for matching and sends alerts) and close;
- follow-ups: "I'm interested" connections and human-handoff cases (new `HandoffCase` table);
- the S&D §4.2 metrics dashboard;
- FR-17 skills supply;
- a blind annotation tool;
- pseudonymised CSV exports.

Migration `AdminAndResearch`.
**Alternatives:** A separate static-hosted SPA with token (JWT) auth and CORS; a server-rendered
admin; computing the metrics offline in Python notebooks.
**Rationale:**
- Same-origin hosting allows the strictest cookie policy with no CORS surface, and suits both the
  VPS and App Service (Azure Static Web Apps remains possible later).
- Building the metrics into the artefact means the dissertation's numbers come from one
  definition, with their pre-specified targets beside them.
- The annotation tool implements the Specification's method (manual coding of a 20% sample) blind
  to the model's prediction, which removes confirmation bias from the accuracy metric.
**Privacy decisions:**
- Phone numbers appear only on Follow-ups, where staff need them to make contact.
- Analytics are aggregates; supply counts under 3 are suppressed.
- Exports use HMAC-SHA256 pseudonyms (`Research:PseudonymKey`) and omit message text (NFR-12).
- Annotation shows the message text to the coder, because coding requires it, but never the phone number.
**Metric definitions** (all in `ResearchService`):
- *task completion*: consented workers who received at least one conversation match;
- *efficiency*: worker messages up to the first profile confirmation;
- *intent accuracy*: blind-coded model-analysed messages, with "none of these" reported but not scored;
- *entity precision*: correct over extracted skills, as judged by the coder;
- *relevance*: ratings of 4 or 5 on real jobs only (synthetic jobs reported separately);
- *latency*: 95th percentile from an inbound message to the first logged reply.
**Incident found during verification and fixed:** a preview run meant to be dry-run used the real
WhatsApp token, because assigning an empty string in PowerShell deletes the variable instead of
overriding the user-secret. One alert template was sent to an invented test number; Meta rejected
it (the app is in test mode), so nothing was delivered. Fixes:
- `WhatsApp:DryRun` forces the logging client whatever token is configured;
- startup logs whether the WhatsApp client is LIVE or DRY RUN;
- a failed alert no longer aborts publishing: it is logged, its match row is removed, and the
  other workers are still alerted.

---

## DL-018 — Several work areas per worker (S4 becomes multi-valued)
**Decision:** A worker can name up to **3 areas** in S4. After the first area the bot asks
"Can you also work in another area?" with the area list (chosen areas removed) and **Done** on
top. Several areas can come in one message ("madina and adenta", or "2, 3"). Reaching 3 ends the
step. The first area is the main one: it stays in `Worker.LocationDistrict` and is what the
supply view and follow-ups show. All areas are stored in a new `WorkerArea` table (migration
`WorkerAreas`, which copies each existing worker's area in as their main area).
**Matching:** stage 1 searches the union of every area's neighbourhood (each district plus its
adjacent districts). A job counts as "in your area" when its district is any of the worker's
areas. New-job alerts reach a worker through any of their areas. The score breakdown now also
records `workerDistricts`.
**Corrections:** "no, my area is Tema" from S5 replaces the areas. "I can also work in Tema" adds
one. "Change something → My areas" clears them and asks again.
**Alternatives:** keep one area and rely on adjacency alone; allow unlimited areas; ask for a
travel radius instead of areas.
**Rationale:** informal workers often take jobs in more than one part of the city (home area,
a market, a junction where they wait for work). One district plus its neighbours missed real
options. A cap of 3 keeps matching local, keeps the list within WhatsApp's 10 rows (Done + 9),
and limits the extra length of the chat.
**Accepted cost:** S4 takes one more turn for everyone (the "Done" tap), which slightly raises
the turns-to-profile efficiency metric compared with sessions before this change. WhatsApp lists
allow only one choice per tap, so multiple choice is a loop, as for skills in S2.
**Research data:** sessions saved before this change keep their single area (upgraded on the
next message). `Intent.FinishAreas` is logged for the "done" turn.

---

## DL-019 — Optional CV builder (Slice 6): a WhatsApp sub-flow that sends a PDF CV
**Decision:** After the profile is confirmed, a worker can get a free one-page CV. It is offered
once (after the first rating, or when no jobs are found) and on the `cv` command at any time.
A sub-machine C0–C8 runs from Active and returns there:
- C0 separate CV consent;
- C1 name;
- C2 phone on the CV (their WhatsApp number, another number, or none);
- C3 up to 3 past jobs;
- C4 up to 3 training lines;
- C5 highest school;
- C6 languages;
- C7 "about" line;
- C8 summary, confirm or change any part.

Trade, skills, experience, availability and areas come from the confirmed profile. On
confirmation the answers are saved (new tables `WorkerCv`, `WorkerCvJob`, migration
`WorkerCv`), and a one-page A4 PDF is generated with QuestPDF (Community licence) and sent as a
WhatsApp **document** (media upload, then a `document` message). This is a reply inside the
24-hour window, so it needs no template and has no cost. The PDF is generated on demand and never
stored. A saved CV is reloaded when the worker types `cv` after their session has expired.

**Design choices:**
- The C7 line is a **template filled from the profile**, not AI-generated text, so the CV states
  only what the worker said. The worker can write their own instead.
- Free-text job and training lines keep the worker's words. Only the period is pulled out
  ("2019 to 2023" → "2019 – 2023"), known places get their proper names, and common
  abbreviations are upper-cased (NVTI, JHS). The raw utterance is kept as with skills.
- No photo, date of birth, ID number, marital status or home address: not needed, and a
  discrimination risk.
- CV answers are **not sent to the NLU** and are therefore never in the annotation sample. They
  are names and job histories, not intents.

**Alternatives:** a download link (may not open on basic phones or with little data); an
AI-written CV (unverifiable claims); asking CV questions during sign-up (lengthens the core flow
and distorts its metrics).

**Ethics and privacy:** the supervisor confirmed the CV is in scope and no ethics amendment is
needed. A separate consent is asked before any CV question, and refusing keeps nothing. `delete`
removes the CV. Research exports never include CV fields. The dashboard funnel shows a "Made a
CV" count, reported separately from the S&D metrics. The privacy page lists the CV details and
their use.

**Accepted cost:** the CV offer adds one message after the first rating. Core metrics (task
completion, turns to profile) are unaffected because the CV comes after the profile.

---

## DL-020 — Other-areas fallback in matching, and a larger synthetic job set
**Context:** in user testing, workers whose areas had no fitting job got "no jobs right now", even
when the right kind of job existed elsewhere in Greater Accra. The demo data made it worse: the 32
jobs in seed v1 covered 10 of 12 trades (none for plumbing/metalwork or vehicle repair), unevenly
across areas.

**Decision 1, fallback:** when stage 1 and stage 2 together find nothing in or near the worker's
areas, matching runs the same two stages over **every other area**:
- availability and the 0.55 similarity threshold still apply, so location is the only rule relaxed;
- the bot says so plainly: "I didn't find a job in your areas yet. But here are N similar jobs in
  other areas", with a hint to add areas via *update*;
- each card's reason reads "…; in Tema, outside your areas";
- the score breakdown records `location: "outside worker's areas (fallback)"`, so these matches
  can be separated in analysis;
- local results always take priority: the fallback runs only when there are none;
- `Matching:SearchOtherAreas` (default true) switches it off to restore the strict S&D filter.

New-job alerts are unchanged (neighbourhood only), because they are business-initiated messages
and must stay relevant.

**Decision 2, seed v2:** `Seed/jobs_synthetic_v2.json` adds 120 synthetic jobs, one per taxonomy
skill, arranged so each of the 10 menu areas has a job in every trade. `seed-jobs` with no path
now loads every `jobs_synthetic_*.json` (idempotent, as before). All rows are `IsSynthetic`.

**Alternatives:** widen adjacency (blurs what "near" means for everyone); lower the similarity
threshold (offers worse matches rather than farther ones); ask the worker before searching
further (an extra turn for an answer that is almost always yes).

**Research note:** fallback matches are a deviation from the S&D stage-1 rule. Report the
matching-relevance metric with and without them; the breakdown's `location` field identifies
them. As before, ratings on synthetic jobs are excluded from the relevance metric.

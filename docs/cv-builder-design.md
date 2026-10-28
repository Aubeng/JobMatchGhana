# CV builder: draft design (proposed Slice 6)

**Status:** built on 22 October 2026 (DL-019). The supervisor confirmed the CV is in scope and
needs no ethics amendment. Open questions 3–5 were settled as the defaults below: the worker
chooses whether their number is on the CV; the about line comes from a template, and the worker
can write their own; employers can't request CVs.
**Sample output:** [cv-sample.pdf](cv-sample.pdf), produced by the real renderer for a fictional worker.

## 1. What it is

After a worker has saved their job profile, JobMatch offers to make them a free one-page CV. It
asks a few extra questions, shows a summary to check, then sends the CV as a **PDF document in
the WhatsApp chat**. The worker taps it to open, save or forward it to an employer. A WhatsApp
document is used rather than a download link because a link may not open on basic phones or
with little data, and a document stays in the chat.

**Principles**

- **Optional and separate.** The CV is offered only after the profile is confirmed, never inside
  sign-up. The core flow and its research metrics (task completion, turns to profile) are unchanged.
- **Its own consent.** A CV needs a name and work history, which the job profile deliberately
  doesn't collect (data minimisation, NFR-09). The CV flow asks for consent to these extra
  details before the first question.
- **Short.** About 6–10 turns, every question skippable except the name, buttons and lists wherever
  possible, the same 500-character and CEFR A2–B1 rules as the rest of the bot.
- **Reuses what we have.** Trade, skills, years of experience, availability and areas come
  straight from the confirmed profile.

## 2. When it is offered

| Entry point | Message |
|---|---|
| After the first match delivery and rating (end of S7) | "📄 Would you like a free CV to show employers? It takes about 3 minutes." [Yes, make my CV] [Not now] |
| The `cv` command, any time after the profile is saved | Starts the CV. If a CV exists, it shows its summary: *Yes* re-sends it, *Change something* edits it |
| The Active menu and `help` text | "• *cv* to get your free CV" |

"Not now" is remembered: the offer is not repeated automatically (the `cv` command still works).

## 3. The conversation

States are a sub-machine (C0–C8) entered from `Active`; global commands (`help`, `stop`,
`delete`, `update`, `matches`) keep working, and the 3-strike handoff applies as elsewhere.

| State | Bot asks | Input | Stored |
|---|---|---|---|
| **C0 CV_CONSENT** | "To make your CV we need a few more details: your **name**, past work, training, schooling and languages. We use them **only to make your CV**. Type *delete* any time to remove them." [Yes, continue] [No] | buttons | consent time |
| **C1 CV_NAME** | "What name should be on your CV? (e.g. *Kwame Asante*)" | free text, 2–60 chars, letters | full name |
| **C2 CV_PHONE** | "Put your WhatsApp number on the CV so employers can call you? **024 xxx xxxx**" [Yes] [Use another number] [No number] | buttons, or a typed Ghana number | phone shown on CV |
| **C3 CV_WORK** (loop, up to 3) | "Tell me about a job you have done. What was the work, where, and when? (e.g. *Mason for Asante Builders, Madina, 2019 to 2023*)" | free text; NLU extracts role/place/years where it can; a short confirm for each entry; [Add another] [Done] [Skip] | role, employer/place, from–to (free text kept) |
| **C4 CV_TRAINING** | "Any training or certificate? (e.g. *NVTI Grade 1 Masonry*, *apprenticeship with a master mason*)" [Skip] | free text, up to 3 | training lines |
| **C5 CV_EDUCATION** | "What is the highest school you finished?" [list: No formal school / Primary / JHS / SHS / TVET / Tertiary] | list | level |
| **C6 CV_LANGUAGES** | "Which languages do you speak?" [list: English, Twi, Ga, Ewe, Fante, Dagbani, Hausa, Other] + Done (loop like skills) | list taps | languages |
| **C7 CV_ABOUT** | "Here is a short line about you for the top of your CV: *Mason with 5 years' experience in block laying and plastering, available full-time in Madina and Adenta.* Keep it?" [Yes, keep it] [Write my own] | buttons / free text ≤ 200 chars | about line |
| **C8 CV_REVIEW** | Summary of the CV text (split if > 500 chars). "Is this correct?" [Yes, make my CV] [Change something] | buttons; Change → list of C1–C7 | — |
| **Deliver** | "Here is your CV 📄" + the PDF document. "Show it to employers or forward it. Type *cv* any time to change it." | — | generated on demand |

The C7 line is built from a template (no AI text generation): `{Trade role} with {years} experience
in {top 2 skills}, available {availability} in {areas}.` This keeps it accurate and predictable.

**Example (tapped and typed turns):**

```
BOT  📄 Would you like a free CV to show employers?    [Yes, make my CV] [Not now]
USER (tap) Yes, make my CV
BOT  consent message                                  [Yes, continue] [No]
USER (tap) Yes, continue
BOT  What name should be on your CV?
USER Kwame Asante
BOT  Put your WhatsApp number on the CV?               [Yes] [Use another number] [No number]
USER (tap) Yes
BOT  Tell me about a job you have done...
USER mason for asante builders at madina 2019 to 2023
BOT  Added: Mason, Asante Builders, Madina, 2019–2023 ✅ Another job?  [Add another] [Done]
USER (tap) Done
BOT  Any training or certificate?                      [Skip]
USER nvti grade 1 masonry
BOT  Highest school you finished?                      [list]
USER (tap) JHS
BOT  Which languages do you speak?                     [list + Done]
USER (tap) Twi, (tap) English, (tap) Done
BOT  short "about" line. Keep it?                      [Yes, keep it] [Write my own]
USER (tap) Yes, keep it
BOT  summary                                            [Yes, make my CV] [Change something]
USER (tap) Yes, make my CV
BOT  Here is your CV 📄  [PDF: CV - Kwame Asante.pdf]
```

## 4. The PDF

- **One A4 page**, plain and printable in black and white, large readable type.
- Sections, in order: name and contact; about line; **skills** (from the profile); **work
  experience** (C3, newest first; if none, the profile's years of experience); training;
  education; languages; availability and areas.
- No photo, date of birth, ID number, marital status or home address (not needed, and a
  discrimination risk).
- Footer: "Made with JobMatch Ghana" in small type.
- **Generated on demand** with QuestPDF (free Community licence for this use) and **not stored**:
  only the CV data is kept, so the file can be rebuilt after edits and nothing extra sits on disk.
- **Sent** by uploading to the WhatsApp Cloud API media endpoint and sending a `document`
  message with filename `CV - <name>.pdf`. This is a reply inside the 24-hour customer-service
  window, so no template or extra cost.

## 5. Data

New table `WorkerCv` (one per worker) plus `WorkerCvJob` (up to 3 rows):

| Field | Notes |
|---|---|
| FullName | required for the CV |
| CvPhone | null = no number on the CV |
| Training | up to 3 short lines |
| EducationLevel | enum from the C5 list |
| Languages | list |
| About | the C7 line |
| CvConsentUtc, CompletedUtc, LastSentUtc | for consent audit and uptake metrics |
| WorkerCvJob: Role, EmployerOrPlace, Period, SourceUtterance | free text kept as the worker said it |

- `delete` (FR-16) removes these too. `stop` keeps them, as for the profile.
- Research exports include **only** whether a CV was made and how many turns it took. Never the
  name, phone or work history.
- The admin site shows CV uptake counts on the dashboard; names appear only on Follow-ups, as
  phone numbers do today.

## 6. Research and ethics

| Topic | Proposal |
|---|---|
| Ethics approval | **Check with the supervisor** whether collecting names and work histories needs an amendment. The current approval and consent cover work details for matching and research only. |
| Consent | Separate CV consent (C0), logged with a timestamp; the main consent text is unchanged. |
| Privacy page | Update "What we collect" (name and work history, only if you ask for a CV) and remove "We do not ask for your name". |
| Metrics | New, reported separately: CV offer acceptance rate, CV completion rate, turns to CV, and (in the follow-up survey) whether workers used the CV. Main metrics exclude CV turns. |
| Risk | A CV with false claims: the CV states only what the worker said; JobMatch does not verify it. Say so in the delivery message? (open question) |

## 7. Open questions for the supervisor

1. Does the CV fit the research scope, or is it a "future work" item for the report?
2. Is an ethics amendment needed for names and work histories?
3. Should the worker's phone number be on the CV by default?
4. Is a template-generated "about" line acceptable, or should the worker always write their own?
5. Should employers be able to request a worker's CV through the BOOST team (with the worker's
   permission each time)? Not in this draft.

## 8. Build estimate

About 2–3 days: CV sub-machine and prompts with tests (1 day), tables and migration, PDF
template, WhatsApp media upload and document send (1 day), consent/privacy/admin updates, docs
and a decision-log entry (½ day), end-to-end test on the VPS (½ day).

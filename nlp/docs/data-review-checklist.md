# Researcher review checklist: D4 taxonomy and D5 training data (v1 drafts)

Everything in `nlp/data/` was drafted by Claude on 2026-10-11 and is **synthetic until you
validate it**. The S&D report allows model-generated utterances as training-data source (i),
provided the researcher validates them. This list collects what needs your judgement.
Tick items off, retrain (`nlp/README.md` §3), and record the outcome in the decision log.

## 1. Skills taxonomy (D4): `data/taxonomy_v1.json`
- [ ] **12 categories**: do they match the BOOST cohort trades (masonry, carpentry, electrical,
      dressmaking, street vending, domestic work, catering, agricultural labour)? Security sits
      inside "Domestic, Cleaning & Security". Is that acceptable, or should it be its own category?
- [ ] **ISCO-08 codes**: every 4-digit code is a best-fit assignment that needs checking against
      the ILO ISCO-08 structure. These are the likeliest to be wrong: tyre repair (7231), wig
      making (5141), mobile money agent (4211), leather/bag making (7536), juice & local drinks (7514).
- [ ] **Synonyms**: add the local terms you hear in the field. Matching is fuzzy, so you don't
      need to list misspellings.
- [ ] Known resolver behaviour: fuzzy matching resolved "low cut" (a haircut) to *Kitchen
      assistant*, via "cut vegetables". It's fixed by an explicit synonym, but other short
      phrases can mis-resolve the same way. Scan `validate_data --review` output for such cases.

## 2. Locations: `data/localities_v1.json`
- [ ] Every entry with `"verify": true` is a low-confidence neighbourhood → district assignment
      (e.g. Lapaz, Kaneshie, Spintex, Tema New Town). District names come from the official
      MMDA list in `ghana_districts.json` (264 districts; Upper East corrected 2026-09-13).
- [ ] Add the neighbourhoods your participants actually live in.

## 3. Annotation guidelines: `docs/annotation-guidelines.md`
- [ ] **Open decision:** experience-only answers ("5 years", "since 2016") are labelled
      `SKILL_DECLARE` (20 lines), because the S&D defines an `EXPERIENCE_DURATION` entity but no
      experience intent. Keep this, or relabel?
- [ ] **Pauses vs opting out:** "later", "not now", "good night", "gn" are labelled `EXIT`. The
      dialogue currently treats only the exact keyword *stop* as opt-out, so these don't
      unsubscribe anyone. Decide whether a pause should be a separate behaviour in the HSM.

## 4. Borderline labels flagged by the drafting agents
| Utterance (pattern) | Labelled | Alternative | Reasoning |
|---|---|---|---|
| "no only weekends", "no its part time" | CORRECTION | AVAILABILITY_STATE | correction beats content (precedence rule 2) |
| "u should add more jobs in kasoa", "send more jobs near me" | FEEDBACK_PROVIDE | (no request intent exists) | suggestions about the service |
| "u sent me tiling job but i do plastering" | FEEDBACK_PROVIDE | CORRECTION | opinion on a match, not a reply to a confirm prompt |
| "is this a scam" / "scam" | HELP_REQUEST / FEEDBACK_PROVIDE | | the question shows confusion; the bare word is a verdict |
| "i have got work, stop" | EXIT | FEEDBACK_PROVIDE | asking to stop wins (precedence rule 1) |
| "proceed", "go ahead", "save it" | CONFIRMATION | | only meaningful after a confirm prompt |
| "no [tailor] for [7 years], not [3]" | CORRECTION | | bare "3" marked EXPERIENCE_DURATION with no unit |

## 5. Line-by-line review
```
cd nlp
.venv\Scripts\python -m jobmatch_nlp.validate_data --review
```
This writes `data/review/review_sheet.csv` (git-ignored): one row per utterance with
*approved (Y/N)*, *corrected intent* and *notes* columns. Make the fixes in the `.txt` files
themselves, which are the source of truth, then re-run the validator.

## 6. Reporting caveat (methods chapter)
The held-out test split is drawn from the same synthetic distribution as the training data, so
its scores are an **upper bound**. The study's *Intent Recognition Accuracy* and *Entity
Extraction Precision* metrics must be measured on real pilot messages: annotate a sample of
`MessageLog` rows after the fact and compare against the logged `NluIntent` / `NluEntitiesJson`.

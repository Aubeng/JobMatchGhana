# JobMatch Ghana NLU service (Slice 2)

A stateless Python FastAPI service that the .NET gateway calls once per post-consent message.
It implements layer L4 of the S&D report:

| Component | Model | Output |
|---|---|---|
| Intent classifier | fine-tuned **XLM-RoBERTa** (`FacebookAI/xlm-roberta-base`) | one of 9 intents + confidence (0.75 threshold) |
| Entity recogniser | **BERT+CRF** (`google-bert/bert-base-multilingual-cased` + linear-chain CRF) | SKILL_ITEM, TRADE_CATEGORY, LOCATION, EXPERIENCE_DURATION spans |
| Entity resolution | exact → fuzzy match (RapidFuzz) | canonical taxonomy skill (ISCO-08), trade category, district (MMDA), years |

Everything runs on CPU. Each model takes about half an hour to train on a 16-core laptop
(Core Ultra 9 185H), and inference takes tens of milliseconds per message.

```
nlp/
  jobmatch_nlp/          the package (annotation, resolve, crf, ner_model, pipeline, app, train_*)
  data/
    taxonomy_v1.json     D4: 12 trade categories, 120 skills, ISCO-08 codes, synonyms   (DRAFT)
    localities_v1.json   neighbourhood -> district map                                  (DRAFT)
    ghana_districts.json official 16 regions -> 264 MMDAs
    utterances/*.txt     D5: 1,635 annotated utterances, one file per intent           (DRAFT)
    splits/              which utterance landed in train/val/test (generated, committed)
  models/                trained weights (git-ignored; reproduce with section 3)
  reports/               test-set metrics per model (committed: research results)
  docs/
    annotation-guidelines.md   labelling rules
    data-review-checklist.md   what the researcher must validate before the pilot
  tests/                 pytest; fixtures/nlu_response.json is the shared HTTP contract
```

## 1. Setup
Python 3.11+ (developed on 3.12):
```
cd nlp
py -3.12 -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt --extra-index-url https://download.pytorch.org/whl/cpu
```
The first training run downloads the two base models from Hugging Face (about 1.8 GB, cached
under `%USERPROFILE%\.cache\huggingface`).

## 2. Validate the data
```
.venv\Scripts\python -m jobmatch_nlp.validate_data            # stats, coverage, unresolved spans
.venv\Scripts\python -m jobmatch_nlp.validate_data --review   # + CSV review sheet for the researcher
```
Run it after every edit to `data/`. Training refuses to start on invalid data.

## 3. Train
```
.venv\Scripts\python -m jobmatch_nlp.train_intent    # -> models/intent, reports/intent_metrics.json
.venv\Scripts\python -m jobmatch_nlp.train_ner       # -> models/ner,    reports/ner_metrics.json
```
Each script keeps the epoch with the best validation score and then evaluates **once** on the
held-out test split. Splits are assigned by a hash of each utterance's text, so the test set
stays fixed as data is added (see `jobmatch_nlp/data.py`). The seed is fixed (13). The report
records hyperparameters, per-epoch history, per-class scores, the confusion matrix and the
environment.

## 4. Run
```
.venv\Scripts\python -m uvicorn jobmatch_nlp.app:app --host 127.0.0.1 --port 8000
```
- `GET /health`: `200 {"status":"ok","model_version":...}`, or `503` if the models aren't trained or
  failed to load. The service stays up so the gateway's fallback can take over.
- `POST /nlu` `{"utterance": "...", "state": "SkillsElicitation"}` returns intent, ranking,
  entities, resolved skills, trade, location, experience, model version and latency. See
  `tests/fixtures/nlu_response.json` for an example.

Bind to `127.0.0.1` only: the service has no authentication. `JOBMATCH_NLP_THREADS` (default 4)
caps the CPU threads it uses for inference.

Point the gateway at it with `Nlu:UseStub=false` (and `Nlu:BaseUrl` if the address differs). If
the service is down or slower than `Nlu:TimeoutSeconds` (5s), the gateway falls back to its
keyword stub for that turn and logs `NluModelVersion = "stub-fallback"`.

## 5. Test
```
.venv\Scripts\python -m pytest
```
The tests need no trained models. They cover markup parsing, BIO conversion, the CRF (checked
against brute-force enumeration), resolution, duration parsing, the post-processing rules and
the HTTP contract.

## Design notes
- **Word-level CRF.** Subword states are reduced to each word's first subword, so labels line
  up with the annotation format and CRF transitions are between words. Invalid BIO transitions
  (O → I-X) are forbidden, so output spans are always well formed.
- **Lexicon backoff.** If the entity model tags no skill but the message contains an exact
  taxonomy phrase, that phrase is returned with `source: "lexicon"` and confidence 0.6. Turn it
  off with `JOBMATCH_NLP_LEXICON_BACKOFF=0`. The NER metrics in `reports/` measure the model alone.
- **Entity confidence** is the mean CRF *marginal* probability (forward-backward) of the decoded
  tag over the span's words, multiplied by the resolver's match score. A softmax over emissions
  alone is uncalibrated in a CRF: a correctly tagged "carpentar" scored 0.38 that way, against
  0.89 with marginals. **Known limitation:** on the synthetic test set, wrong spans score about as
  high as right ones (mean 0.985 vs 0.989), so confidence does not flag entity errors. The model is
  confidently fitted to synthetic data; recheck this on pilot data.

## v1 results (synthetic test split, 166 utterances)
| Metric (S&D §4.2) | Target | v1 | Report |
|---|---|---|---|
| Intent recognition accuracy | ≥ 85% | **88.6%** (macro-F1 0.889) | `reports/intent_metrics.json` |
| Entity extraction precision: skills resolved to the right taxonomy skill | ≥ 80% | **90.6%** (29/32), recall 100% | `reports/pipeline_metrics.json` |
| Entity span precision / recall / F1 (strict, all 4 labels) | | 0.842 / 0.865 / 0.853 | `reports/ner_metrics.json` |
| NLU latency (4 threads) | part of ≤ 5 s p95 | mean 66 ms, p95 92 ms | `reports/pipeline_metrics.json` |

These numbers are **upper bounds**: the test utterances are synthetic and come from the same
distribution as the training data. The study's metrics must be computed on annotated pilot
messages (`data-review-checklist.md` §6). Run `python -m jobmatch_nlp.evaluate` to regenerate the
pipeline metrics.

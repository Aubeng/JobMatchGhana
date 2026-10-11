# NLU annotation guidelines (D5 training data)

**Status:** v1.0, drafted 2026-10-11. The researcher must validate these guidelines and every
drafted utterance before the models trained on them are used in the pilot.

These rules define how each utterance in `nlp/data/utterances/` is labelled with one **intent**
and zero or more **entities**. The same files train both models:
- the XLM-RoBERTa intent classifier (one label per utterance), and
- the BERT+CRF entity recogniser (token spans).

The S&D report specifies both models (L4; Design §6.3).

## 1. File format

One file per intent, named after the intent: `SKILL_DECLARE.txt`, `EXIT.txt`, …
One utterance per line. Entities are marked inline:

```
i can [lay blocks](SKILL_ITEM) and [plaster](SKILL_ITEM) walls
am a [carpenter](TRADE_CATEGORY) for [10 years](EXPERIENCE_DURATION) now
i stay at [Madina](LOCATION) but i work in [Accra central](LOCATION)
```

- Lines starting with `#` are comments. Blank lines are ignored.
- The unmarked text is the utterance exactly as a worker would type it. Keep typos and casing.
- No nested or overlapping spans. A `[` or `]` that is not markup must not appear.
- Exact duplicates (case-insensitive) are removed by the validator.

## 2. Intents (9)

| Intent | The worker is… | Examples |
|---|---|---|
| `SKILL_DECLARE` | describing specific work they can do, **or** how long they have done it | "i can lay blocks", "i do braiding and wigs", "5 years", "since 2017" |
| `TRADE_IDENTIFY` | naming their occupation, trade or field, with no specific skill | "am a carpenter", "i'm into catering", "construction work" |
| `LOCATION_SHARE` | saying where they live or can work | "madina", "i stay at kaneshie", "anywhere in tema" |
| `AVAILABILITY_STATE` | saying when or how much they can work | "full time", "only weekends", "mornings", "any day except sunday" |
| `CONFIRMATION` | agreeing or confirming what the system said | "yes", "correct", "that's right", "ok", "yoo" |
| `CORRECTION` | rejecting or correcting what the system said | "no", "wrong", "not plastering, tiling", "change my area to tema" |
| `HELP_REQUEST` | asking for help or showing confusion | "help", "i don't understand", "what should i type?" |
| `FEEDBACK_PROVIDE` | giving an opinion on a match or the service | "the job was too far", "this is helpful", "5 stars", "i got the work, thanks" |
| `EXIT` | wanting to stop, leave or pause | "stop", "bye", "i don't want again", "delete my number" |

**Precedence when an utterance fits more than one intent:**
1. `EXIT` and `HELP_REQUEST` win over everything else.
2. `CORRECTION` beats the intent of its content: "no i'm in [tema](LOCATION)" is a
   `CORRECTION`, with the entity still annotated.
3. Skill + trade in one message → `SKILL_DECLARE`: "am a [mason](TRADE_CATEGORY), i [lay blocks](SKILL_ITEM)".
4. Skill + location in one message → `SKILL_DECLARE`. Location + availability → `LOCATION_SHARE`.

**Experience** (open decision for the researcher): the report defines an `EXPERIENCE_DURATION`
entity but no experience intent. These guidelines label experience-only answers ("5 years",
"since 2015") as `SKILL_DECLARE`, because they describe the declared skill. Change this rule
before the data is finalised if needed; the validator reports how many lines it affects.

**Excluded:** bare menu digits ("1", "2") and exact keywords the FSM already handles
deterministically. Don't put them in the data.

## 3. Entities (4)

| Label | Mark | Don't mark |
|---|---|---|
| `SKILL_ITEM` | the specific task or competency, including its verb when the skill is an action: "[lay blocks]", "[fix burst pipes]", "[braid hair]", "[house wiring]" | "i can", "i know how to", "very well" |
| `TRADE_CATEGORY` | an occupation noun or field: "[carpenter]", "[mason]", "[catering]", "[construction work]" | articles ("a", "an"), "i'm" |
| `LOCATION` | the place name only: "[Madina]", "[Tema community 4]", "[Kaneshie market]" | prepositions ("at", "in", "around") |
| `EXPERIENCE_DURATION` | a duration or start point: "[5 years]", "[two yrs]", "[since 2018]", "[since i left school]", "[6 months]" | "i have been doing it for" |

- Mark each distinct skill separately: "[lay blocks](SKILL_ITEM) and [plaster](SKILL_ITEM)".
- Use the shortest span that still names the thing.
- Entities can occur in any intent (e.g. a `CORRECTION` carrying a `LOCATION`).

## 4. Language and style

The users are Greater Accra informal-sector workers typing on WhatsApp in Ghanaian English
(the pilot is English-only, per the S&D scope). Drafted utterances should reflect that:

- mostly lower case, short, little punctuation; some typos and abbreviations (pls, u, d, wat, abt)
- Ghanaian English and common pidgin forms ("i dey do", "i sabi", "chale", "paa", "small small",
  "ehn", "oo", "please i beg")
- local terms: trotro, okada, aboboyaa, kayayo, chop bar, waakye, kaba and slit, momo, polytank
- a mix of lengths: one word up to about 30 words
- **no real personal data**: no phone numbers, and only common invented first names

## 5. Provenance and validation

Drafts are **synthetic** (generated by Claude, a language model). This is source (i) in the
S&D Design §6.3.1. Before use, the researcher reviews every line, fixes labels, deletes
unrealistic lines and adds real expressions from their own experience. Pilot interaction logs
(source iii) are added only after consent and anonymisation, for the NLP Model v2.0 iteration (D11).

Because the held-out test split is drawn from the same synthetic distribution, test-set accuracy
**overstates** real-world performance. The pilot's intent-accuracy metric must be computed on
real pilot messages, annotated after the fact. `MessageLog.NluIntent` stores the model's prediction
for exactly this purpose.

Run the validator after every edit:
```
cd nlp
.venv\Scripts\python -m jobmatch_nlp.validate_data
```

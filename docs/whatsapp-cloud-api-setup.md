# WhatsApp Cloud API — Setup & Operations Guide

A step-by-step guide to standing up the JobMatch Ghana gateway against the real WhatsApp Cloud
API, written from the actual commissioning session on 2026-07-20. It records what to do, *why*
each step exists, and every failure we hit with its diagnosis.

> **Scope.** This covers development and pilot testing using Meta's **test number** and a
> **dev tunnel**. Production deployment (Azure App Service, own sender number) is Slice 5.
>
> **Update (2026-10-11):** staging now runs on the Windows/IIS VPS with a permanent callback URL,
> `https://jobmatch.intellite.net/webhook`, so you no longer need a tunnel to test with real phones.
> See [vps-deployment.md](vps-deployment.md). The tunnel steps below remain for debugging against
> your local machine.

---

## 1. The mental model

Before touching Meta's UI, understand the shape of the system. Most setup errors come from
misunderstanding this, not from typos.

```
  Worker's phone                Meta's servers                Your machine
  ┌───────────┐   message   ┌──────────────────┐  webhook  ┌──────────────────┐
  │ WhatsApp  │ ──────────▶ │  WhatsApp Cloud  │ ────────▶ │  Dev tunnel      │
  │           │ ◀────────── │  API (Graph)     │ ◀──────── │  → JobMatch.Api  │
  └───────────┘   reply     └──────────────────┘  send     └──────────────────┘
```

Two **independent** directions, which fail independently:

| Direction | Mechanism | Depends on |
|---|---|---|
| **Inbound** (worker → you) | Meta POSTs to your callback URL | Webhook subscriptions, public URL, app secret |
| **Outbound** (you → worker) | You POST to `graph.facebook.com` | Access token, phone number ID, asset permissions |

A broken token kills only outbound. A missing subscription kills only inbound. We hit both at
once, which is why the symptoms looked confusing — always test the two directions separately.

### The four Meta objects (and why they're confused)

These sit next to each other in the UI, all look like long numbers, and are easy to mix up.
We lost time to exactly this.

| Object | Example | What it is |
|---|---|---|
| **App ID** | `<APP_ID>` | Your Meta developer app (`JobMatchGhana`) |
| **WABA ID** | `<WABA_ID>` | WhatsApp Business Account — *owns* phone numbers |
| **Phone number ID** | `<PHONE_NUMBER_ID>` | The specific sender. **This is what you call the API with** |
| **System User ID** | `<SYSTEM_USER_ID>` | The machine identity that holds the token |

None of these are secrets — they're identifiers. The **App Secret** and **Access Token** are the
secrets, and they never go in the repo.

> **Rule of thumb:** if you're POSTing a message, you want the **Phone number ID**. If you're
> configuring subscriptions or permissions, you want the **WABA ID**.

---

## 2. Local prerequisites

### 2.1 Pin the .NET SDK

The machine may have newer SDKs installed. `global.json` at the repo root pins the build:

```json
{ "sdk": { "version": "8.0.303", "rollForward": "latestFeature" } }
```

Verify: `dotnet --version` → should resolve to an 8.0.x SDK inside the repo.

### 2.2 SQL Server

The app expects a reachable SQL Server. Apply migrations:

```powershell
dotnet ef database update `
  --project src/JobMatch.Infrastructure `
  --startup-project src/JobMatch.Api
```

**Use a separate database for testing.** Your own test conversations write real `Worker` and
`MessageLog` rows — and `MessageLog` *is* the research dataset. Mixing test noise into pilot data
is painful to unpick later. Override via user-secrets so `appsettings.json` stays clean:

```powershell
dotnet user-secrets set "ConnectionStrings:SqlServer" `
  "Server=(local)\sqlexpress;Database=JobMatchGhana_Dev;Trusted_Connection=True;TrustServerCertificate=True" `
  --project src/JobMatch.Api
```

Remove it later to return to the pilot database:
```powershell
dotnet user-secrets remove "ConnectionStrings:SqlServer" --project src/JobMatch.Api
```

### 2.3 Redis — required, not optional

Conversation sessions live in Redis. **Without it the FSM cannot run at all**: the orchestrator
loads the session before stepping, so every message fails after the inbound log is written. The
symptom is distinctive — an inbound `MessageLog` row with **blank `SessionState` and
`IntentDetected`**, and no outbound row.

```powershell
docker run -d --name jobmatch-redis -p 6379:6379 redis:7-alpine
docker exec jobmatch-redis redis-cli ping      # → PONG
```

The container does **not** survive a reboot. After restarting your machine:
```powershell
docker start jobmatch-redis
```

---

## 3. Meta setup

### 3.1 Create the app

1. [developers.facebook.com](https://developers.facebook.com) → **My Apps → Create App**
2. Type: **Business**
3. Add the **WhatsApp** product

This provisions a **test number**, a **WABA**, a temporary token, and a phone number ID.

### 3.2 Add test recipients

Meta's test number can only message **up to 5 pre-verified numbers**. Under
**WhatsApp → API Setup → "To" → Manage phone number list**, add your number and confirm the OTP.

> Messages to any number *not* on this list will fail, even with everything else configured
> correctly. Check here first if sends 400.

### 3.3 Get a permanent System User token

The token shown on the API Setup page is a **24-hour temporary token**. It will expire overnight
and every Graph call will start returning `401 OAuthException`. Don't build a habit around it —
go straight to a permanent one.

1. [business.facebook.com/settings](https://business.facebook.com/settings) → **Users → System Users**
2. Create (or select) a system user → **Generate new token**
3. Select your app; tick **`whatsapp_business_messaging`** and **`whatsapp_business_management`**
4. Copy the token — **it is shown once**

**Critically — assign the asset.** A system user only has access to assets *explicitly granted*
to it. A temp token inherits your admin rights; a system user token does not. This is the single
most common failure:

5. Same system user → **Add Assets** → **WhatsApp Accounts** → tick your WABA → **Full control** → Save

Verify it actually took (see §6.2) — an empty result here means nothing else will work.

### 3.4 Store the credentials

Never in `appsettings.json`. Initialise the secret store once:

```powershell
dotnet user-secrets init --project src/JobMatch.Api
```

This writes a `UserSecretsId` GUID into the `.csproj`. That GUID is **not** a secret — it just
points at `%APPDATA%\Microsoft\UserSecrets\<guid>\secrets.json`, outside the repo. Safe to commit.

```powershell
cd src/JobMatch.Api
dotnet user-secrets set "WhatsApp:AppSecret"     "<app-secret>"
dotnet user-secrets set "WhatsApp:VerifyToken"   "<invent-a-random-string>"
dotnet user-secrets set "WhatsApp:PhoneNumberId" "<phone-number-id>"
dotnet user-secrets set "WhatsApp:AccessToken"   "<system-user-token>"
```

| Key | Where it comes from |
|---|---|
| `AppSecret` | App Settings → Basic → App secret (**Show**) |
| `VerifyToken` | You invent it. Must match what you type into Meta's webhook config |
| `PhoneNumberId` | WhatsApp → API Setup, under the test number |
| `AccessToken` | The System User token from §3.3 |

> **Behavioural note:** a non-empty `AccessToken` is what switches the gateway from
> `LoggingWhatsAppClient` (dry-run, logs `[WhatsApp:DRYRUN]`) to the real `WhatsAppClient`.
> Leave it empty and the whole conversation still runs locally with no Meta account at all.

---

## 4. Expose the local endpoint

Meta needs a public HTTPS URL. In development, use a **Microsoft Dev Tunnel** — free, no card,
no inbound firewall ports.

```powershell
winget install Microsoft.devtunnel
devtunnel user login -d                    # interactive; device-code flow
devtunnel host -p 5154 --allow-anonymous
```

It prints e.g. `https://sbhd2wn9-5154.uks1.devtunnels.ms`. Your callback URL is that **+ `/webhook`**.

Points to understand:

- **The hostname is ephemeral.** Stop the tunnel and it's gone; you get a new one next session and
  must re-paste it into Meta. For a persistent hostname across restarts:
  ```powershell
  devtunnel create --allow-anonymous
  devtunnel port create -p 5154
  devtunnel host <tunnel-id>
  ```
- **`--allow-anonymous` means anyone with the URL can reach the endpoint.** That's acceptable here
  because HMAC validation rejects unsigned requests with 403 — but the URL is not a security control.
- **Not for production.** Your machine must be running. The pilot deploys to Azure App Service.
- Check the app's actual port — `launchSettings.json` can override `ASPNETCORE_URLS`. Ours runs
  on **5154**, not 5000.

---

## 5. Configure the webhook — *both* subscriptions

This is the step that cost us the most time. WhatsApp requires **two separate subscriptions**, and
the UI only shows you the first one. Having just the app-level one looks completely healthy and
delivers nothing.

### 5.1 App-level (the UI does this)

**WhatsApp → Configuration → Webhook → Edit**

| Field | Value |
|---|---|
| Callback URL | `https://<tunnel-host>/webhook` |
| Verify token | your `WhatsApp:VerifyToken` |

Click **Verify and save**. Meta immediately sends a `GET` with `hub.mode`, `hub.verify_token` and
`hub.challenge`; the endpoint echoes the challenge as `text/plain` on match, else 403.

Then **Webhook fields → Manage → subscribe to `messages`** (that field only — the rest is noise).

### 5.2 WABA-level (you must do this yourself)

The app must also be subscribed to the specific WhatsApp Business Account. The dashboard never
surfaces this, and it is frequently missing.

**Check:**
```powershell
# Should list YOUR app. If it only shows "WA DevX Webhook Events 1P App", you are not subscribed.
GET https://graph.facebook.com/v21.0/<WABA_ID>/subscribed_apps
```

**Fix:**
```powershell
POST https://graph.facebook.com/v21.0/<WABA_ID>/subscribed_apps
# → {"success":true}
```

See §6 for runnable versions of these.

---

## 6. Diagnostic toolkit

When something doesn't work, **query the Graph API rather than reading the UI**. The UI shows
intent; the API shows reality. Every one of these is read-only except the last.

All snippets assume:
```powershell
$s   = Get-Content "$env:APPDATA\Microsoft\UserSecrets\<guid>\secrets.json" -Raw | ConvertFrom-Json
$tok = $s.'WhatsApp:AccessToken'
function G($url) {
  $req = [System.Net.HttpWebRequest]::Create($url); $req.Headers.Add("Authorization","Bearer $tok")
  try { $r=$req.GetResponse(); (New-Object System.IO.StreamReader($r.GetResponseStream())).ReadToEnd() }
  catch [System.Net.WebException] { $e=$_.Exception.Response
    if($e){ "ERR $([int]$e.StatusCode): " + (New-Object System.IO.StreamReader($e.GetResponseStream())).ReadToEnd() } }
}
```

### 6.1 Is the token valid, and is it permanent?

```powershell
G "https://graph.facebook.com/v21.0/debug_token?input_token=$([uri]::EscapeDataString($tok))"
```

Read the response carefully:
- `"is_valid": true` — the token works
- `"expires_at": 0` — **permanent** (a System User token). Any other value is a ticking clock
- `"type": "SYSTEM_USER"` — confirms it's not a temp user token
- `scopes` must include `whatsapp_business_messaging` **and** `whatsapp_business_management`
- `app_id` tells you which app it belongs to — useful when you have several

### 6.2 Does the system user actually have the WABA?

```powershell
G "https://graph.facebook.com/v21.0/<SYSTEM_USER_ID>/assigned_whatsapp_business_accounts"
```

`{"data":[]}` means **no assets assigned** — go back to §3.3 step 5. This is the root cause of
"object does not exist, cannot be loaded due to missing permissions" errors.

### 6.3 What is this ID, actually?

Query the bare node. A WABA returns a name and `message_template_namespace`:

```powershell
G "https://graph.facebook.com/v21.0/<ID>"
G "https://graph.facebook.com/v21.0/<WABA_ID>/phone_numbers"   # → the real phone number IDs
```

The `phone_numbers` edge is the authoritative way to find your phone number ID, and it also shows
`quality_rating` and the current `webhook_configuration`.

### 6.4 Is the webhook configured?

```powershell
# App-level — needs an APP access token: "<app-id>|<app-secret>"
$appTok = "<APP_ID>|$($s.'WhatsApp:AppSecret')"
G "https://graph.facebook.com/v21.0/<APP_ID>/subscriptions?access_token=$([uri]::EscapeDataString($appTok))"

# WABA-level — the one the UI hides
G "https://graph.facebook.com/v21.0/<WABA_ID>/subscribed_apps"
```

### 6.5 Test the pipeline without Meta

Simulate a signed inbound message. This exercises tunnel → signature → queue → FSM → DB → send,
and isolates *your* code from Meta's configuration:

```powershell
$body = '{"object":"whatsapp_business_account","entry":[{"id":"WABA","changes":[{"field":"messages","value":{"messaging_product":"whatsapp","metadata":{"display_phone_number":"1555","phone_number_id":"PNID"},"contacts":[{"profile":{"name":"Diag"},"wa_id":"233200000000"}],"messages":[{"from":"233200000000","id":"wamid.DIAG001","timestamp":"1700000000","text":{"body":"hi"},"type":"text"}]}}]}]}'
$bytes = [System.Text.Encoding]::UTF8.GetBytes($body)
$h = New-Object System.Security.Cryptography.HMACSHA256
$h.Key = [System.Text.Encoding]::UTF8.GetBytes($s.'WhatsApp:AppSecret')
$sig = "sha256=" + ([System.BitConverter]::ToString($h.ComputeHash($bytes)) -replace '-','').ToLower()

Invoke-WebRequest -Uri "https://<tunnel-host>/webhook" -Method POST -Body $bytes `
  -ContentType "application/json" -UseBasicParsing `
  -Headers @{'X-Hub-Signature-256'=$sig; 'X-Tunnel-Skip-AntiPhishing-Page'='true'}
```

**Use a new `wamid.` each run** — idempotency will silently skip a repeat, which looks like a
failure. Note the HMAC is computed over the **exact raw bytes** you send; re-serialising the JSON
changes the bytes and breaks the signature.

### 6.6 Verify the handshake yourself

```powershell
# correct token → 200 + echoed challenge
Invoke-WebRequest "https://<host>/webhook?hub.mode=subscribe&hub.verify_token=<token>&hub.challenge=TEST" -UseBasicParsing
# wrong token → 403
```

Use `-UseBasicParsing`; without it PowerShell can render an empty response and look like a failure
when the call actually succeeded.

### 6.7 Inspect the results

```powershell
# What the FSM recorded
sqlcmd -S "(local)\sqlexpress" -d JobMatchGhana_Dev -Q `
  "SELECT Id,Direction,WaId,SessionState,IntentDetected,NluConfidence,IsRepair,LEFT(Body,50) FROM MessageLogs ORDER BY Id"

# Live sessions
docker exec jobmatch-redis redis-cli KEYS 'jobmatch:session:*'
```

Healthy first turn looks like:
```
[1] dir=0 wa=233240000000 state=0(New)     intent=0  'Hi'
[2] dir=1 wa=233240000000 state=1(Consent)           'Welcome to *JobMatch Ghana* ...'
Workers rows: 0        ← consent gate holding: no profile before consent
```

---

## 7. Troubleshooting — real failures from commissioning

| Symptom | Root cause | Fix |
|---|---|---|
| Inbound row logged with **blank** `SessionState`/`IntentDetected`, no reply row | Redis not running — FSM never stepped | `docker start jobmatch-redis` |
| `401 OAuthException` on every Graph call, `"Session has expired on …"` | 24-hour temp token expired | Use a permanent System User token (§3.3) |
| `Object with ID '…' does not exist, cannot be loaded due to missing permissions` | System user has no WABA assigned | Add Assets → WhatsApp Accounts → Full control |
| `(#100) Missing Permission` on `/me/businesses` | Token lacks `business_management` | Expected — use the WABA-scoped endpoints instead |
| `Tried accessing nonexisting field (display_phone_number)` | The ID is a **WABA**, not a phone number | Query `/<WABA_ID>/phone_numbers` for the real ID |
| **Nothing arrives at all**; app log silent; tunnel shows no traffic | App not in the WABA's `subscribed_apps` | `POST /<WABA_ID>/subscribed_apps` (§5.2) |
| Send returns 400 to a specific number | Recipient not on the 5-number test allowlist | Add + verify it in API Setup |
| `user-secrets` → *"Could not find the global property 'UserSecretsId'"* | Secret store not initialised | `dotnet user-secrets init` |
| `user-secrets set` appears to work but value doesn't change | Relative `--project` path resolved from the wrong cwd | Use the absolute path to the `.csproj` |

**General method:** work outward from your own code. Confirm the pipeline with a simulated signed
POST (§6.5). If that works, your code is exonerated and the problem is Meta configuration —
then query the Graph API to find which of the four objects is misconfigured.

---

## 8. Daily startup checklist

Nothing here survives a reboot except the token and your secrets.

```powershell
# 1. Infrastructure
docker start jobmatch-redis
docker exec jobmatch-redis redis-cli ping                  # → PONG

# 2. App
dotnet run --project src/JobMatch.Api                      # → listening on :5154

# 3. Public URL
devtunnel host -p 5154 --allow-anonymous                   # → note the new hostname

# 4. Re-point Meta at the new URL
#    WhatsApp → Configuration → Webhook → Edit → paste <new-host>/webhook + verify token
#    (skip this step if using a persistent named tunnel)

# 5. Smoke test
#    Send "hi" from a verified test number; check MessageLogs for a new pair of rows.
```

---

## 9. Security notes

- **Secrets never enter source control.** `AppSecret` and `AccessToken` live only in the
  user-secret store locally, and in App Service configuration in Azure (DL-007).
- **A permanent token never expires on its own** — that's the convenience *and* the risk. If it
  leaks, rotate it in Business Settings → System Users. Temporary tokens self-heal by expiring;
  permanent ones don't.
- **Don't paste tokens into chat, tickets, or commit messages.** If you do, rotate afterwards.
- **The signature check fails closed** — empty secret, missing header, malformed hex, or wrong
  length all yield 403. It compares in constant time (`CryptographicOperations.FixedTimeEquals`)
  and hashes the **raw request bytes**, never a re-serialised body (DL-003).
- **The consent gate is structural, not cosmetic.** No `Worker` row is written before explicit
  consent. `Workers` staying at 0 through the greeting turn is the observable proof (DL-010).

---

## 10. What changes for production

| Concern | Development (this guide) | Pilot / production (Slice 5) |
|---|---|---|
| Public URL | Dev tunnel, ephemeral hostname | Azure App Service, stable HTTPS |
| Availability | Only while your laptop runs | Always on |
| Sender | Meta test number, 5 recipients max | Own verified business number |
| App mode | Development | Live (needs business verification) |
| Secrets | `dotnet user-secrets` | App Service configuration |
| Redis | Local Docker container | Azure Cache for Redis |
| Database | `JobMatchGhana_Dev` | `JobMatchGhana` (the research dataset) |

Going Live requires business verification, a privacy policy URL, and an app category — a real
detour. **Don't start it while debugging**; it is not required for test-number development, and
it won't fix a subscription problem.

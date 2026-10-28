# Deploying to the VPS and testing end to end

This runbook takes JobMatch from your laptop to **`https://jobmatch.intellite.net`** on the shared
Intellite VPS, connects Meta's **test phone number** to it, and walks through a full end-to-end
test from your own phone. Do the parts in order; each ends with a check.

| | |
|---|---|
| Server | Windows Server 2022, IIS 10, 24 GB RAM (shared with other sites), public IP <VPS_PUBLIC_IP> |
| Database | SQL Server 2022 Web, default instance `.`, database `JobMatchGhana` |
| Sessions | Redis for Windows (tporadowski port), `localhost:6379` |
| Gateway + admin site | ASP.NET Core 8 in IIS, app pool `JobMatch`, site `jobmatch` |
| NLU + matching | Python service `JobMatchNlu` (NSSM), `127.0.0.1:8000` only |
| Webhook | `https://jobmatch.intellite.net/webhook` |
| Admin site | `https://jobmatch.intellite.net/admin` |

```
 Your phone ── WhatsApp ── Meta Cloud API (test number)
                                │ HTTPS POST /webhook
 ┌──────────────────────────── VPS ───────────────────────────────────┐
 │ IIS :443 jobmatch.intellite.net ── app pool JobMatch               │
 │    gateway + /admin  ──► SQL Server (JobMatchGhana)                │
 │                      ──► Redis localhost:6379                      │
 │                      ──► JobMatchNlu service 127.0.0.1:8000        │
 │                              (XLM-R + BERT+CRF + MiniLM, C:\inetpub\jobmatch\nlp) │
 └────────────────────────────────────────────────────────────────────┘
```

Scripts live in `deploy/`. `Publish.ps1` and `Package-NluModels.ps1` run on your laptop; everything
in `deploy/server/` runs **on the VPS in an elevated PowerShell** and travels inside each release zip.

---

## Part A: build the packages (laptop)

From the repo root, in PowerShell:

```powershell
.\deploy\Publish.ps1              # tests, admin site, API, migrate.sql, NLU code -> artifacts\jobmatch-<time>.zip
.\deploy\Package-NluModels.ps1    # trained models (~1.8 GB) -> artifacts\jobmatch-models-<time>.zip
```

Copy both zips to the VPS (e.g. `C:\Deploy\`). RDP copy-paste works; for the 1.8 GB models zip a
cloud drive or `scp` is more reliable. Then extract the **release** zip once to `C:\Deploy\first`
to get its `server\` and `nlp\` folders (Deploy-Release unpacks its own copy later).

**Check:** two zips on the VPS; `C:\Deploy\first\server\` contains `Setup-Server.ps1`.

---

## Part B: Meta, with the test phone number

Full click-paths and screenshots are in [whatsapp-cloud-api-setup.md](whatsapp-cloud-api-setup.md);
this is what the test number needs.

1. **Recipients.** In the Meta developer dashboard → your app → **WhatsApp → API Setup**, add your
   own phone (and up to four testers) to the **To** list and verify each with the code WhatsApp
   sends. The test number can only message numbers on this list; anything else is rejected
   (that is what protected us in the preview incident, DL-017).
2. **Note the IDs** on the same page: the test **Phone number ID** and the **WhatsApp Business
   Account ID**.
3. **Access token.** The token on API Setup expires in 24 hours. For a test today it works; for
   anything longer create a **permanent System User token**: Business Settings → Users → System
   users → add (Admin) → assign your app and the WhatsApp account → Generate token with
   `whatsapp_business_messaging` and `whatsapp_business_management`. An expired token silently
   stops replies.
4. **App Secret.** App settings → Basic → App Secret (Show).
5. **Verify token.** Make up a long random string now; you'll type it twice (Part C step 9 and Part E).
6. **Alert template (optional for today).** WhatsApp Manager → Message templates → Create:
   category **Utility**, name `job_match_alert`, language English, body
   `A new job may suit you: {{1}} in {{2}}. Tap below to see it.`, with a **quick reply** button
   `See jobs`. Without an approved template, publishing a job still works; the alerts just fail
   and are logged (the rest of the test is unaffected).

**Check:** you have the Phone number ID, a token, the App Secret, your verify token, and your
phone is a verified recipient.

---

## Part C: one-time VPS setup

Run everything in an **elevated** PowerShell on the VPS.

### 1. DNS
At the DNS host for `intellite.net`, add an **A record**: `jobmatch` → `<VPS_PUBLIC_IP>`.
Check from anywhere: `Resolve-DnsName jobmatch.intellite.net` → <VPS_PUBLIC_IP>.

### 2. Software
Install, if not already there:

| What | From | Check |
|---|---|---|
| ASP.NET Core 8 **Hosting Bundle** | dotnet.microsoft.com/download/dotnet/8.0 → "Hosting Bundle"; then `net stop was /y` and `net start w3svc` | `dotnet --list-runtimes` shows `Microsoft.AspNetCore.App 8.` |
| **Python 3.12** (64-bit) | python.org → Windows installer; tick *Install for all users* and *py launcher* | `py -3.12 --version` |
| **NSSM** 2.24+ | nssm.cc → unzip to `C:\Tools\nssm` | `C:\Tools\nssm\win64\nssm.exe version` |
| **win-acme** | win-acme.com → x64 pluggable zip → `C:\Tools\win-acme` | `wacs.exe` exists |

### 3. Redis
Confirm the service runs and listens only locally: `Get-Service Redis*`, and in
`C:\Program Files\Redis\redis.windows-service.conf` check `bind 127.0.0.1` and whether
`requirepass` is set (you'll give that password in step 9). Port 6379 must not be open in
Windows Firewall. Don't add a password if other sites use this Redis without one.

### 4. IIS site and app pool
```powershell
cd C:\Deploy\first\server
.\Setup-Server.ps1
```
Creates `C:\inetpub\jobmatch`, the `JobMatch` app pool and the `jobmatch` site on
`http://jobmatch.intellite.net`. Touches nothing else.

### 5. Keep the app pool running (your reminder)
By default IIS stops idle apps after 20 minutes and recycles every 29 hours, which drops
messages waiting in the in-memory queue. Set it now:
```powershell
Install-WindowsFeature Web-AppInit
Import-Module WebAdministration
Set-ItemProperty IIS:\AppPools\JobMatch -Name startMode -Value AlwaysRunning
Set-ItemProperty IIS:\AppPools\JobMatch -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty IIS:\AppPools\JobMatch -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty IIS:\Sites\jobmatch -Name applicationDefaults.preloadEnabled -Value $true
```

### 6. Database
In **SSMS**, connected to the VPS's SQL Server as an administrator, open
`server\create-database-ssms.sql` from the release and **Execute**. (Or, where sqlcmd works:
`sqlcmd -S . -E -b -i .\create-database.sql -v DatabaseName="JobMatchGhana" AppPool="JobMatch"`.)
Creates `JobMatchGhana` and gives `IIS APPPOOL\JobMatch` read/write on it only. No sqlcmd is needed
anywhere else: each release applies its own migrations (Part D).

### 7. HTTPS certificate
Run `C:\Tools\win-acme\wacs.exe` → **N** → *Single binding of an IIS site* → `jobmatch.intellite.net`
→ your email. It adds an SNI binding on 443 for this host only and a renewal task.
**Check:** `https://jobmatch.intellite.net/` shows "No release deployed yet".

### 8. NLU + matching service
```powershell
.\Install-NluService.ps1 -Nssm C:\Tools\nssm\win64\nssm.exe -ModelsZip C:\Deploy\jobmatch-models-<time>.zip
```
Creates the Python environment (downloads ~1 GB of packages), installs the models, registers the
`JobMatchNlu` Windows service on `127.0.0.1:8000` (low-privilege LocalService account) and waits
for it. The first start also downloads the MiniLM matching model (~90 MB). Takes 5–15 minutes.
**Check:** it ends with "Healthy: models intent@…; matching sentence-transformers/…". Memory use
settles at roughly 2–3 GB (`Get-Process python`).

### 9. Settings and secrets
```powershell
.\Set-AppSettings.ps1
```
Answer the prompts: Redis password (blank if none), App Secret, verify token, Phone number ID,
access token, **Dry run: `n`** (this is the live test), and Enter for the pseudonym key (one is
generated: copy it somewhere safe). It also points the gateway at the NLU service.

---

## Part D: deploy the release

```powershell
cd C:\Deploy\first
.\server\Deploy-Release.ps1 -Package C:\Deploy\jobmatch-<time>.zip
```
Unpacks the release, applies any new database migrations (the release's own `migrate` command,
run as you, so your Windows account needs rights on the database), refreshes the NLU service
code, switches the site to the new release and polls `/health`. If migrations ever fail, the
site keeps serving the previous release; as a fallback run the release's `migrate.sql` in SSMS
and re-run with `-SkipMigrations`. **Check:** "Deployed jobmatch-<time>."

Then load demo jobs and create your admin account:
```powershell
cd C:\inetpub\jobmatch\releases\jobmatch-<time>\server
.\Invoke-AppCommand.ps1 seed-jobs
.\Invoke-AppCommand.ps1 create-admin you@example.org     # type a 12+ character password
```

**Checks:**
- `https://jobmatch.intellite.net/health` → `{"status":"ok"}`
- Event Viewer → Windows Logs → Application has a warning from the app: **"WhatsApp client:
  LIVE (messages go to real WhatsApp numbers)"**. No such warning means it started in DRY RUN
  (logged at information level, so not in the Event Log): re-run step 9 and answer `n`.
- `https://jobmatch.intellite.net/admin` → sign in → **Jobs** lists 152 synthetic jobs (seed v1: 32, v2: 120). Re-running `seed-jobs` after a new release adds only the new ones.

---

## Part E: connect Meta

Meta dashboard → your app → **WhatsApp → Configuration → Webhook → Edit**:

| Field | Value |
|---|---|
| Callback URL | `https://jobmatch.intellite.net/webhook` |
| Verify token | the string from Part B step 5 |

**Verify and save**, then **Manage** → subscribe to **`messages`**. If nothing arrives later, the
app may not be subscribed to the WhatsApp account: see "Nothing arrives at all" in
[whatsapp-cloud-api-setup.md](whatsapp-cloud-api-setup.md) (`POST /<WABA_ID>/subscribed_apps`).

---

## Part F: the end-to-end test

From your **verified** phone, message the test number. Expected replies follow the S&D flow
([conversation-design.md](conversation-design.md)). Tick each row.

| # | You send | Expect |
|---|---|---|
| 1 | `hi` | Welcome + consent question, **Yes, I agree / No** buttons |
| 2 | tap **Yes, I agree** | "Thank you" + **Choose your work** list |
| 3 | `am a mason, i lay blocks and plaster for 5 years` | Skills list showing *Block laying, Plastering*; experience already captured |
| 4 | tap **Done, that's all** | Availability buttons (years are not asked again) |
| 5 | tap **Full-time** | **Choose your area** list |
| 6 | `i stay at madina` | Profile summary with *Madina (La Nkwantanang Madina)*, **Yes / Change something** |
| 7 | `no my area is adenta` | "Updated" + summary with *Adenta (Adentan)* |
| 8 | tap **Yes, that's right** | "Profile saved", then up to 3 job cards (synthetic jobs near Adenta) and a rating list |
| 9 | tap **I'm interested** on one job | "The JobMatch team will contact you about …" |
| 10 | choose **4 – Helpful**, then type a comment | Thanks for the feedback |
| 11 | `help` | Help text + the current question |
| 12 | `asdf`, then `qwer`, then `zxcv` | First two: the menu again; third: "someone from the JobMatch team will message you" |

Then in the **admin site**:
- **Follow-ups** shows your interested job and the handoff case (with your number).
- **Dashboard**: funnel shows 1 consented, 1 confirmed, 1 matched; the rating is counted under
  "synthetic jobs (excluded)" because the demo jobs are synthetic.
- **Jobs → New job** near Adenta → **Publish**: the notice shows "1 alerted" (or "1 could not be
  sent" if the template isn't approved yet).
- **Annotate**: your free-text messages appear for blind coding.

Finally, the data controls: send `delete` → **Yes, delete** → "Your data has been deleted"; the
admin dashboard funnel drops back to 0. (Do this last, or with a second test phone.)

---

## Troubleshooting

First stops: **Event Viewer → Windows Logs → Application** (sources `IIS AspNetCore Module V2`,
`.NET Runtime`, and the app's own warnings) and `C:\inetpub\jobmatch\nlp\logs\nlu.log`.

| Symptom | Likely cause | Fix |
|---|---|---|
| HTTP 500.19 | Hosting Bundle missing or installed before IIS | Reinstall it (C2), `net stop was /y`, `net start w3svc` |
| HTTP 500.30 | App crashed at startup (settings, data files) | Event Viewer; stdout logs below |
| `Login failed for user 'IIS APPPOOL\JobMatch'` | Database step skipped | C6 |
| Meta: "callback URL couldn't be validated" | Verify token mismatch, or HTTPS not bound | C9, C7 |
| Webhook POSTs get 403 | Wrong App Secret | C9 |
| Messages arrive, no replies | Token expired, DRY RUN on, or recipient not verified | Event Viewer (`WhatsApp send failed`), C9, B1 |
| Replies but skills never recognised beyond exact words | NLU service down → keyword fallback | `Get-Service JobMatchNlu`, `nlu.log`; Admin dashboard "NLU fallback rate" |
| `RedisConnectionException` | Redis stopped or wrong password | C3, C9 |
| Replies stop after a quiet spell | App pool idled out | C5 |
| `/admin` sign-in loops | Cookie needs HTTPS in production | Use `https://` |

**stdout logs** (temporary): in the live release's `app\web.config` set `stdoutLogEnabled="true"`
and `stdoutLogFile="C:\inetpub\jobmatch\logs\stdout"`; turn it off afterwards.

**Roll back** the site and NLU code to the previous release (the database is not rolled back):
```powershell
.\server\Deploy-Release.ps1 -Rollback
```

**Next release:** `.\deploy\Publish.ps1` on the laptop, copy the zip, run `Deploy-Release.ps1`
again. New models: `Package-NluModels.ps1`, then `Install-NluService.ps1 -Nssm … -ModelsZip …`.

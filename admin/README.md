# JobMatch Ghana admin SPA (Slice 4, S&D layer L6)

Vue 3 + Vite + TypeScript. The .NET API serves it at **`/admin`**: same origin, so sign-in uses a
strict session cookie and there is no CORS to configure.

| Page | For | Requirement |
|---|---|---|
| **Dashboard** | The six S&D §4.2 metrics against their targets, the worker journey, operations, CSV exports | Evaluation; NFR-12 |
| **Jobs** | Create, edit, publish (opens matching + sends new-job alerts) and close jobs | L6 job CRUD; FR-17 |
| **Follow-ups** | Workers who tapped "I'm interested", and human-handoff cases from the 3-strike rule | DL-016; S&D 3-strike rule |
| **Annotate** | Blind manual coding of real messages: intent accuracy and entity precision | §4.2 (20% sample) |
| **Skills supply** | Confirmed workers by trade and district; counts under 3 hidden | FR-17; NFR-12 |

**Privacy.** Follow-ups is the only page that shows phone numbers, because staff need them to
make the connection a worker asked for. Analytics and supply are aggregated with small-cell
suppression. Exports replace phone numbers with keyed pseudonyms and leave out message text.

## Develop

```bash
cd admin
npm install
npm run dev          # http://localhost:5173/admin/ ; /api proxied to the API on :5154
npm test             # component + formatter tests (Vitest)
npm run build        # type-check, then build into src/JobMatch.Api/wwwroot/admin
```

Run the API alongside it (`dotnet run --project src/JobMatch.Api`, http profile, port 5154).

## First admin account

```bash
dotnet run --project src/JobMatch.Api -- create-admin you@example.org
```
Type the password when asked (12+ characters; it isn't echoed), or set `JOBMATCH_ADMIN_PASSWORD`
for scripted setup. Five wrong passwords lock the account for 15 minutes.

## Configuration the admin features need
- `Research:PseudonymKey`: a secret of at least 16 characters, for the CSV exports. Keep it in
  user-secrets or app-pool variables like other secrets. Losing it changes every pseudonym.
- `WhatsApp:DryRun=true` for any demo or preview: publishing a job then logs alerts instead of
  sending them, whatever access token is configured.

## Structure
`views/` are thin route pages that compose feature components (`components/<feature>/`). Data and
side effects live in composables (`composables/use*.ts`) built on `useResource` (loading, error,
reload). The only app-wide store is `stores/auth.ts`. `api/client.ts` adds the CSRF header the
API requires on every write and sends 401s back to sign-in.

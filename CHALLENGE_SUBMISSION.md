# Challenge Submission

## Candidate

- **Name:** Arturo de Jongh
- **Date:** 2026-09-14

---

## How to Run

Lives entirely under `candidate/` as its own solution, separate from the read-only `src/SyncPlatform/` simulator.

1. Prerequisites: .NET 8 SDK, SQL Server with AdventureWorks restored, and the `SyncPlatform` WPF simulator running (`http://localhost:5100`).
2. Set secrets (not committed):
   ```powershell
   cd candidate/SyncAgent
   dotnet user-secrets set "SyncAgent:ApiKey" "candidate-test-key-2026"
   dotnet user-secrets set "SyncAgent:ConnectionString" "Server=localhost;Database=AdventureWorks2025;Trusted_Connection=True;TrustServerCertificate=True"
   ```
3. `dotnet run` from `candidate/SyncAgent`.
4. `dotnet test candidate/SyncAgent.slnx` (28 tests, no live DB needed).
5. Trigger a task type from the simulator UI; the agent picks it up on its next poll (default 5s) and posts the result.

Details: `candidate/SyncAgent/README.md`.

---

## Architecture Decisions

- **Worker Service + `BackgroundService`**: a long-running poller fits the generic host's `BackgroundService` better than a console app or hand-rolled `IHostedService`.
- **EF Core over Dapper**: more familiar to me. A minimal `AdventureWorksDbContext` maps only the ~16 tables actually needed (Fluent API, no migrations — read-only). All queries use `AsNoTracking()` and projection-based LINQ; `GetOrdersAsync` uses `AsSplitQuery()` to avoid row fan-out on the order/detail join.
- **Scoped services in a singleton worker**: `Worker` never holds a `DbContext`/repository directly; it creates a fresh DI scope per polling cycle via `IServiceScopeFactory` so scoped state never leaks across cycles.
- **Strategy pattern for task types**: `ITaskHandler` per task type, resolved by `TaskHandlerDispatcher` keyed on `taskType`. Adding a new task type only needs one new handler class + one DI registration — satisfies the "reusable components" requirement directly.
- **Shared handler base class**: `TaskHandlerBase` centralizes `modifiedSince` parsing, timing, and success/failure `SyncResultDto` construction (always echoing `TaskId`/`TaskType`), so each handler is a one-line delegation to the repository.
- **Layered, not DDD**: the agent has no business rules of its own (thin read/relay pipeline), so a lightweight layered structure (Contracts/Api/Data/Handlers) fits better than DDD ceremony.

---

## Security Measures

- **No secrets in source control**: `ApiKey`/`ConnectionString` are empty placeholders in `appsettings.json`, supplied via `dotnet user-secrets` locally; never logged.
- **Fail-fast config validation**: `SyncAgentOptionsValidator` + `ValidateOnStart()` checks a valid API URL, required key/connection string, and a positive poll interval at startup.
- **No SQL injection surface**: all data access is parameterized EF Core LINQ, no raw SQL.
- **Defensive validation of external input**: rejects a fetched task missing `taskId`/`taskType`; dispatcher guards against null tasks/unknown `taskType` and returns a graceful `failed` result instead of throwing.
- **Bounded HTTP resilience**: 30s timeout + standard resilience handler (retry/backoff, circuit breaker) instead of unbounded retries.
- **Read-only DB access**: `DbContext` never writes; only the columns the DTOs need are mapped.

---

## Testing Strategy

28 xUnit tests (Moq + EF Core InMemory, no live SQL Server needed):

- **API client**: 200/204/401, malformed JSON, POST success/400.
- **Repository**: `modifiedSince` filtering, category/inventory joins, order grouping, store-only-customer fallback.
- **Handlers**: success/failure/missing-`modifiedSince` per task type, always asserting `TaskId` echoing.
- **Dispatcher**: correct routing, case-insensitive matching, unknown-type fallback.
- **Worker**: full polling cycle (dispatch + post) and the no-task-available path.

With more time: an integration test against a real SQL Server to confirm no N+1 queries, and byte-for-byte JSON shape assertions against the sample payloads.

---

## Known Limitations

- **At-most-once delivery**: if the agent crashes between dequeuing a task and posting its result, that task is silently dropped (the queue is in-memory on the server, agent has no checkpoint). Accepted trade-off, not engineered around.
- **No self-restart on crash**: left to the hosting environment (Windows Service recovery, container restart policy), not app code.
- **Sequential polling**: one task per cycle, not batched/parallel — matches the simple polling contract but wouldn't scale to a busy queue.
- **No automated integration test**: generated SQL was checked manually via EF Core logging, not asserted in a test.

---

## AI Tools Used

- GitHub Copilot (agent mode) throughout: analyzed the challenge/contract/sample payloads to build the development plan; scaffolded projects; implemented each layer (DTOs, API client, EF Core, handlers, dispatcher, worker) and matching tests commit-by-commit; drafted the ER diagrams in `candidate/docs/query-notes.md`.
- All suggestions were reviewed and adjusted (e.g., correcting scaffolded `net10.0` targets to `net8.0`, fixing DI/test issues from later refactors) before committing.

---

## Time Spent

Approximately 2-3 hours total: ~30 min planning/validation, ~1.5-2 hours implementation + tests (committed incrementally), ~15-20 min hardening/logging/docs.

---

## Feedback

Well-scoped challenge; the simulator and sample payloads made validating the exact wire contract easy. The `candidate/` directory requirement and the exact `error-result.json` shape were easy to miss on a first skim of the README vs. the fuller `CHALLENGE_DESCRIPTION.md`/`docs/api-contract.md` \u2014 a direct pointer from the README would help.

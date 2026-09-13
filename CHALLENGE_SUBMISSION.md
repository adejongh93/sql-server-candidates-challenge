# Challenge Submission

## Candidate

- **Name:** Arturo de Jongh
- **Date:** 2026-09-13

---

## How to Run

The solution lives entirely under `candidate/` as its own solution, independent of the read-only `src/SyncPlatform/` simulator.

1. Prerequisites: .NET 8 SDK, a SQL Server instance with the AdventureWorks sample database restored, and the `SyncPlatform` WPF simulator running (`src/SyncPlatform/SyncPlatform.sln`, listening on `http://localhost:5100`).
2. Configure secrets (not committed):
   ```powershell
   cd candidate/SyncAgent
   dotnet user-secrets set "SyncAgent:ApiKey" "candidate-test-key-2026"
   dotnet user-secrets set "SyncAgent:ConnectionString" "Server=localhost;Database=AdventureWorks2025;Trusted_Connection=True;TrustServerCertificate=True"
   ```
3. Run the agent: `dotnet run` from `candidate/SyncAgent`.
4. Run tests: `dotnet test candidate/SyncAgent.slnx` (28 tests, no live database required — repository tests use EF Core's InMemory provider).
5. Trigger a task from the SyncPlatform WPF simulator's UI (`GetCustomers`, `GetProducts`, `GetProductInventory`, or `GetOrders`); the agent picks it up on its next poll cycle (default 5s) and posts the result back.

See `candidate/SyncAgent/README.md` for full details.

---

## Architecture Decisions

- **Worker Service + `BackgroundService`**: the agent is a long-running polling process, not a request/response app, so the generic host's Worker Service template with a single `BackgroundService` (`Worker`) is the natural fit over a raw console app or a hand-rolled `IHostedService`.
- **EF Core over Dapper**: I'm more familiar with EF Core, so I used `Microsoft.EntityFrameworkCore.SqlServer` with a hand-written, minimal `AdventureWorksDbContext` mapping only the ~16 tables the four task types actually need (via Fluent API, no migrations since the agent never writes to AdventureWorks). All queries use `AsNoTracking()` and projection-based LINQ (`.Select()` into DTOs) instead of `Include()`-everything, to keep generated SQL efficient; `GetOrdersAsync` additionally uses `AsSplitQuery()` to avoid the row fan-out that a single-query `Include` would cause on the one-to-many order/detail join.
- **Scoped services inside a singleton `BackgroundService`**: `Worker` is registered once via `AddHostedService<Worker>()`, but it never holds a `DbContext`/repository/API client directly. Instead it takes an `IServiceScopeFactory` and creates a fresh DI scope per polling cycle, so scoped EF Core state is always short-lived and never shared across cycles.
- **Strategy pattern for task types**: `ITaskHandler` is implemented once per task type (`GetCustomersTaskHandler`, `GetProductsTaskHandler`, `GetProductInventoryTaskHandler`, `GetOrdersTaskHandler`) and resolved at runtime by `TaskHandlerDispatcher`, keyed by `taskType` (case-insensitive) via a dictionary built from DI-registered `IEnumerable<ITaskHandler>`. This directly satisfies the "reusable components" requirement: adding a new sync task type only means adding one new handler class and one `AddScoped<ITaskHandler, ...>()` line in `Program.cs` — no changes to the worker, dispatcher, or API client.
- **Shared handler base class**: `TaskHandlerBase` centralizes the cross-cutting behavior every handler needs — defensive `modifiedSince` parsing with a safe fallback, execution timing, and constructing the success/failure `SyncResultDto` (always echoing back the original `TaskId`/`TaskType`, matching the API contract's `error-result.json` shape on failure) — so individual handlers stay a thin one-line delegation to the repository.
- **Layered design, no DDD**: given the agent has no business rules or domain invariants of its own (it's a thin read/relay pipeline), a lightweight layered structure (Contracts / Api / Data / Handlers) was a better fit than full DDD ceremony.

---

## Security Measures

- **No secrets in source control**: `ApiKey` and `ConnectionString` are empty placeholders in `appsettings.json` and are supplied locally via `dotnet user-secrets` (or environment variables in a real deployment); they are never written to logs.
- **Fail-fast configuration validation**: `SyncAgentOptionsValidator` (`IValidateOptions<SyncAgentOptions>` + `ValidateOnStart()`) checks that `ApiBaseUrl` is a valid absolute URL and that `ApiKey`/`ConnectionString` are present and `PollIntervalSeconds` is positive, so misconfiguration surfaces immediately at startup instead of as a confusing failure mid-poll.
- **No SQL injection surface**: all AdventureWorks access goes through EF Core's parameterized LINQ; there is no raw SQL or string concatenation anywhere in the repository layer.
- **Defensive input validation on external data**: `SyncPlatformApiClient.GetNextTaskAsync` rejects a deserialized task that is missing `taskId`/`taskType`; `TaskHandlerDispatcher.DispatchAsync` guards against a null task or empty `taskType` and returns a graceful `failed` result instead of throwing for any task type it doesn't recognize, so a malformed or unknown task can never crash the polling loop.
- **Bounded HTTP resilience**: the typed `HttpClient` for the SyncPlatform API has an explicit 30s timeout and uses `Microsoft.Extensions.Http.Resilience`'s standard resilience handler (retry with backoff, circuit breaker, per-attempt timeout) instead of retrying indefinitely or hanging.
- **Read-only database access**: the `DbContext` is never used to write, and entities are mapped only for the columns the four DTOs need — reducing the blast radius if a query were ever misused.

---

## Testing Strategy

The suite has 28 xUnit tests covering every layer, using Moq for collaborators and EF Core's InMemory provider for the repository (no live SQL Server needed to run `dotnet test`):

- **API client** (`SyncPlatformApiClientTests`): 200/204/401 responses, malformed JSON, and both success and 400 paths for posting a result, using a fake `HttpMessageHandler`.
- **Repository** (`AdventureWorksRepositoryTests`): `modifiedSince` filtering, category/subcategory projection, inventory join mapping, multi-line order grouping into nested `OrderDetails`, and the store-only-customer fallback — seeded against an InMemory `AdventureWorksDbContext`.
- **Handlers** (one test class per task type): success path (including `result.TaskId == task.TaskId` echoing), failure path when the repository throws, and the missing-`modifiedSince` fallback.
- **Dispatcher** (`TaskHandlerDispatcherTests`): correct routing to the matching handler, case-insensitive `taskType` matching, and the graceful failed-result path for an unrecognized task type.
- **Worker** (`WorkerTests`): end-to-end polling cycle using an in-process `ServiceCollection` — verifies a task is dispatched and its result posted, and that no result is posted when the queue is empty.

With more time I would add: an integration test running the agent against a real (containerized) SQL Server instance to validate the actual generated SQL and confirm no N+1 queries under load; and a test asserting the exact JSON shape posted for each task type matches the sample payloads byte-for-byte rather than just structurally.

---

## Known Limitations

- **At-most-once task delivery**: if the agent crashes between dequeuing a task from the SyncPlatform server and posting its result, that task is silently dropped — the simulator's queue is in-memory with no local checkpoint/cursor on the agent side. This was a conscious trade-off rather than something engineered around, since the agent doesn't own the queue and the challenge's simulator has no re-delivery mechanism.
- **No self-restart on crash**: the agent does not restart itself after an unhandled process-level failure; that's left to the hosting environment (Windows Service recovery options, container restart policy) rather than app code.
- **Sequential, single-task polling**: the worker processes one task per poll cycle rather than draining the queue or processing tasks concurrently. This matches the simple polling contract described in the challenge, but a busier queue would benefit from batching or parallel processing with a concurrency limit.
- **No integration/E2E automated test**: the test suite is unit-level only (mocked HTTP, InMemory EF Core); the actual generated SQL was verified via EF Core's SQL logging during manual end-to-end runs, not asserted in an automated test.

---

## AI Tools Used

- Used GitHub Copilot (agent mode) throughout to: analyze the challenge description, API contract, sample payloads, and simulator source to derive an accurate, verified development plan before writing any code; scaffold the `SyncAgent`/`SyncAgent.Tests` projects; implement each layer (DTOs, API client, EF Core `DbContext`/repository, task handlers, dispatcher, polling worker) commit-by-commit; and generate the corresponding unit tests for each layer.
- Copilot was also used to validate design decisions against the challenge's stated evaluation criteria (SOLID principles, reusable components, input validation) and to draft the ER diagrams for each of the four sync task types in `candidate/docs/query-notes.md`.
- All AI-suggested code was reviewed, and adjusted (e.g., correcting project target frameworks from the scaffolded `net10.0` default down to `net8.0`, and fixing DI registration/test compilation issues introduced by later refactors) before being committed.

---

## Time Spent

Approximately 2-3 hours total:

- ~30 min: reading the challenge description, API contract, sample payloads, README, and simulator source; validating the AdventureWorks schema/query design and producing the step-by-step development plan.
- ~1.5-2 hours: implementation (project scaffolding, configuration, DTOs, API client, EF Core data access, task handlers, dispatcher, polling worker) and matching unit tests, committed incrementally.
- ~15-20 min: hardening, structured logging, and documentation (this file plus `candidate/SyncAgent/README.md`).

---

## Feedback

The challenge is well-scoped and the provided simulator/sample payloads made it straightforward to validate the exact wire contract up front instead of guessing at shapes. The one thing that took extra care was confirming the `candidate/` directory requirement and the exact `error-result.json` failed-task shape — both were easy to miss on a first skim of the README versus the fuller `CHALLENGE_DESCRIPTION.md`/`docs/api-contract.md`. A short pointer from the top-level README directly to those two files would make onboarding slightly faster.

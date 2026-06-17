# Architecture

## Layers

The project is organized around a small set of boundaries:

- `apps/web`: standalone browser workspace served separately from the backend in Docker.
- `Api`: HTTP endpoints for browser-facing commands such as auth, workspace config, streaming chat, conversations, and export.
- `Services`: application behavior, external integrations, and chat orchestration.
- `Services/Auth`: auth contracts, workflow orchestration, cookie policy, session persistence, rate limiting, validation, and token generation.
- `Services/Chat`: chat request models, Gemini request construction, streaming orchestration, persistence coordination, and user-facing error mapping.
- `Data`: EF Core persistence, entities, and startup database preparation.
- `Configuration`: dependency injection, middleware, and endpoint composition.
- `Services/Health`: runtime health checks for deployment and readiness probes.

## Startup Flow

1. `Program.cs` creates logging and the web host.
2. `AddVertexApplication` registers options, persistence, app services, and health checks.
3. `InitializeVertexApplicationAsync` prepares the database.
4. `UseVertexPipeline` applies forwarded headers, error handling, health checks, and APIs.
5. Health endpoints are exposed as `/health/live` and `/health/ready`.

## Extension Points

- Add new workspace UI features under `apps/web`.
- Add new browser/API workflows under `Api`, then register them from `UseVertexPipeline`.
- Add new application capabilities under `Services`, then register them from `ServiceCollectionExtensions`.
- Extend authentication through `Services/Auth` instead of storing session, cookie, token, or rate-limit logic in endpoints.
- Extend chat send behavior through `ChatOrchestrator` instead of adding persistence, streaming, or SDK logic to endpoints.
- Swap chat models via `IChatModelClient` and persistence via `IConversationStore`.
- Add provider adapters behind `IChatModelProvider`; `GeminiProvider` and the optional OpenAI-compatible adapter prove the current shape.
- Add schema compatibility work in `DatabaseInitializer`; move to EF migrations when the schema stabilizes.
- Add new provider-specific AI code behind a service boundary instead of calling SDKs directly from endpoints.
- Preserve multimodal message attachments through `attachments_json`, `ChatHistoryEntry`, API detail responses, and provider history loading.
- Add regression coverage in `VertexAI.Tests` for service-layer behavior before larger refactors.
- Use `/health/live` for process liveness and `/health/ready` when traffic should wait for database connectivity.

## Target Cloud Architecture

The planned migration target is Firebase Auth + Firestore + Cloud Run + Vertex AI / Gemini:

- Firebase Auth owns identity and produces `uid` plus an ID token.
- Firestore owns users, conversations, and messages.
- Cloud Run hosts this ASP.NET Core API and verifies Firebase tokens before orchestrating chat requests.
- Vertex AI / Gemini remains stateless and receives prompt, history, and attachments from the backend.

See [MIGRATION_PLAN.md](MIGRATION_PLAN.md) for the phased migration plan and Firestore document shape.

## Current Technical Debt

- Auth workflows still use EF directly; repositories can be introduced when tests or alternate storage require it.
- Database creation currently uses `EnsureCreated` plus compatibility SQL. EF migrations should replace this before production data grows.
- UI copy and visual polish should be reviewed after locking a product design brief.

## Recommended Next Refactors

1. Add tests for `AuthWorkflowService` around login, registration, reset, verification, and rate limiting.
2. Add broader `ChatOrchestrator` tests for image-only requests and existing conversation sends.
3. Convert the lightweight test runner to a full test framework if CI reporting needs richer output.
4. Move database compatibility SQL into real EF migrations.
5. Confirm a product design brief, then refine loading, empty, error, and mobile chat states.

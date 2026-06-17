# Architecture

## Layers

The project is organized around these boundaries:

- `apps/web`: standalone browser workspace served separately from the backend in Docker.
- `Api`: HTTP endpoints for workspace config, Firebase auth status, streaming chat, conversations, settings, and export.
- `Services/Auth`: Firebase ID token verification and current-user resolution.
- `Services/UserSettings`: Firestore-backed user settings under `users/{uid}`.
- `Services/Chat`: chat request models, Gemini request construction, streaming orchestration, Firestore persistence, and user-facing error mapping.
- `Configuration`: dependency injection, middleware, and endpoint composition.
- `Services/Health`: liveness and Firestore readiness checks.

## Request Flow

1. The browser signs in through Firebase Authentication.
2. The browser sends the Firebase ID token as an `Authorization: Bearer <token>` header.
3. Cloud Run / the ASP.NET Core API verifies the token and extracts the Firebase `uid`.
4. The API reads user settings, conversation config, and history from Firestore.
5. The API builds the model prompt and calls Vertex AI / Gemini.
6. The API writes user and assistant messages back to Firestore.
7. The API streams the assistant response to the browser.

## Runtime Ownership

- Firebase Auth owns identity only: registration, login, `uid`, and ID tokens.
- Firestore owns data only: user settings, conversations, messages, and persisted token counts.
- The ASP.NET Core API owns orchestration only: token verification, Firestore reads/writes, prompt assembly, model calls, and export formatting.
- Vertex AI / Gemini owns generation only and remains stateless.

## Extension Points

- Add new workspace UI features under `apps/web`.
- Add new browser/API workflows under `Api`, then register them from `UseVertexPipeline`.
- Add new application capabilities under `Services`, then register them from `ServiceCollectionExtensions`.
- Extend authentication through `Services/Auth/FirebaseUserContext.cs`.
- Extend Firestore chat persistence through `Services/Chat/FirestoreConversationStore.cs`.
- Extend user settings through `Services/UserSettings/FirestoreUserSettingsStore.cs`.
- Add provider adapters behind `IChatModelProvider`; `GeminiProvider` and the optional OpenAI-compatible adapter prove the current shape.
- Add regression coverage in `VertexAI.Tests` for service-layer behavior before larger refactors.

## Health Checks

- `/health/live` verifies process liveness.
- `/health/ready` verifies Firestore connectivity.

## Remaining Work

1. Deploy Firestore composite indexes from `firestore.indexes.json`.
2. Add Firebase-token integration tests around authenticated API endpoints.
3. Add broader `ChatOrchestrator` tests for image-only requests and existing conversation sends.
4. Convert the lightweight test runner to a full test framework if CI reporting needs richer output.

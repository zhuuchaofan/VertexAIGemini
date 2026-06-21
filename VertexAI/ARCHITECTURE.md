# Architecture

## Layers

The project is organized around these boundaries:

- `apps/web`: standalone browser workspace served by the Cloud Run web service.
- `Api`: HTTP endpoints for workspace config, Firebase auth status, streaming chat, conversations, and export.
- `Services/Auth`: Firebase ID token verification and current-user resolution.
- `Services/Chat`: chat request models, provider request construction, streaming orchestration, Firestore persistence, attachment storage, and user-facing error mapping.
- `Configuration`: dependency injection, middleware, and endpoint composition.
- `Services/Health`: liveness and Firestore readiness checks.

## Request Flow

1. The browser signs in through Firebase Authentication.
2. The browser sends the Firebase ID token as an `Authorization: Bearer <token>` header.
3. Cloud Run / the ASP.NET Core API verifies the token and extracts the Firebase `uid`.
4. The API reads conversation config and history from Firestore.
5. The API stores large attachments in Cloud Storage and keeps Firestore messages lightweight.
6. The API builds the model prompt and calls Vertex AI / Gemini.
7. The API writes user and assistant messages back to Firestore.
8. The API streams the assistant response to the browser.

## Runtime Ownership

- Firebase Auth owns identity only: registration, login, `uid`, and ID tokens.
- Firestore owns data only: conversations, messages, and persisted token counts.
- Cloud Storage owns attachment objects only: uploaded images and files remain private and are hydrated by the API.
- The ASP.NET Core API owns orchestration only: token verification, Firestore reads/writes, prompt assembly, model calls, and export formatting.
- Vertex AI / Gemini owns generation only and remains stateless.

## Chat Pipeline

`ChatOrchestrator` keeps the high-level request flow small: resolve provider, load history, persist the user message, run request augmentation, stream the model, then persist the assistant message.

Request augmentation is handled by `IChatRequestAugmenter` implementations. Each augmenter receives the stable `ChatRequestContext` plus the current augmented message and returns the next `ChatRequestAugmentation`. This is the extension point for RAG retrieval, long-term memory injection, document context, and bounded agent tools that need to prepare context before the model call. The built-in `WebSearchInstructionAugmenter` preserves the existing forced-search prompt behavior.

## Extension Points

- Add new workspace UI features under `apps/web`.
- Add new browser/API workflows under `Api`, then register them from `UseVertexPipeline`.
- Add new application capabilities under `Services`, then register them from `ServiceCollectionExtensions`.
- Extend authentication through `Services/Auth/FirebaseUserContext.cs`.
- Extend Firestore chat persistence through `Services/Chat/FirestoreConversationStore.cs`.
- Extend attachment storage through `Services/Attachments/IChatAttachmentStore.cs`.
- Add provider adapters behind `IChatModelProvider`; `GeminiProvider` and the optional OpenAI-compatible adapter prove the current shape.
- Add pre-model capabilities such as RAG, memory, and tool context by registering `IChatRequestAugmenter` implementations.
- Add regression coverage in `VertexAI.Tests` for service-layer behavior before larger refactors.

## Health Checks

- `/health/live` verifies process liveness.
- `/health/ready` verifies Firestore connectivity.

## Remaining Work

1. Verify Firestore composite indexes from `firestore.indexes.json` are `READY` after deployment.
2. Add Firebase-token integration tests around authenticated API endpoints.
3. Add Firestore-backed integration coverage for conversation ownership, export, and delete cascades. Endpoint-level regression coverage now verifies export ownership, attachment metadata export, and authenticated delete dispatch.
4. Convert the lightweight test runner to a full test framework if CI reporting needs richer output.

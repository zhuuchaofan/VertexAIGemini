# Firebase / Firestore / Cloud Run Migration Plan

## Target Architecture

The target production shape is:

1. Firebase Auth owns identity only.
   - Browser login and registration are handled by Firebase.
   - The browser sends a Firebase ID token with API requests.
   - The backend verifies the token and uses the resulting `uid`.

2. Firestore owns application data only.
   - `users/{uid}` stores user profile and workspace settings.
   - `conversations/{conversationId}` stores conversation configuration and metadata.
   - `messages/{messageId}` stores chat records.
   - Each conversation and message must carry `uid` when top-level collections are used.

3. Cloud Run owns backend logic only.
   - Verify Firebase token.
   - Load user settings, conversation config, and message history from Firestore.
   - Build the prompt/session options.
   - Call the selected AI provider.
   - Persist user and assistant messages.
   - Stream or return the assistant response.

4. Vertex AI / Gemini owns generation only.
   - The model receives prompt, history, and attachments.
   - It does not own user identity or conversation state.

## Request Flow

```text
Browser request + Firebase ID token
-> Cloud Run
-> verify token and get uid
-> load Firestore user, conversation, and history
-> call Gemini / Vertex AI
-> persist messages in Firestore
-> stream or return assistant result
```

## Core Principles

- Auth: identity only (`uid`, token verification).
- Firestore: data only (users, conversations, messages).
- Cloud Run: orchestration only (authorization, prompt assembly, AI calls, persistence).
- Gemini: generation only, stateless with respect to chat history.

## Recommended Firestore Shape

Top-level collections are acceptable when every document stores `uid`:

```text
users/{uid}
conversations/{conversationId}
messages/{messageId}
```

Required fields:

- `users/{uid}`: `email`, `createdAt`, `lastLoginAt`, `defaultAssistantPrompt`.
- `conversations/{conversationId}`: `uid`, `title`, `providerId`, `modelName`, `presetId`, `customPrompt`, `historySummary`, `tokenCount`, `createdAt`, `updatedAt`.
- `messages/{messageId}`: `uid`, `conversationId`, `role`, `content`, `thinkingContent`, `attachments`, `createdAt`.

Important indexes are tracked in `../firestore.indexes.json`:

- `conversations`: `uid ASC`, `updatedAt DESC`.
- `messages`: `uid ASC`, `conversationId ASC`, `createdAt ASC`.
- `messages`: `uid ASC`, `conversationId ASC`, `createdAt DESC`.

For stronger ownership locality, this alternate nested shape can be used instead:

```text
users/{uid}
users/{uid}/conversations/{conversationId}
users/{uid}/conversations/{conversationId}/messages/{messageId}
```

The top-level shape is closer to the current API and export code, while the nested shape gives simpler Firestore security rules. The first implementation should choose one shape and avoid supporting both.

## Migration Phases

### Phase 1: Identity Boundary

- Introduce a backend user context abstraction.
- Update API endpoints to depend on the abstraction instead of direct auth/session reads.

Status: complete.

### Phase 2: Firebase Auth

- Add Firebase Admin SDK configuration to the API host.
- Implement Firebase token verification as the production `IUserContext`.
- Derive a stable local `Guid` from Firebase `uid` only for internal API compatibility; Firestore ownership uses the Firebase `uid`.
- Update the browser app to use Firebase Web SDK for login, registration, logout, and token refresh.
- Send `Authorization: Bearer <idToken>` on API requests.

Status: complete. Local email/password auth and cookie sessions are removed from the runtime path.

### Phase 3: String User IDs

- Use Firebase `uid` as the persisted owner key across chat, conversation, export, and settings flows.
- Keep a stable derived `Guid` only where existing response contracts still expose GUID-shaped IDs.
- Update tests for `uid`-based authorization.

Status: complete for the Firestore runtime path.

### Phase 4: Firestore Persistence

- Add a Firestore-backed conversation store that preserves the existing `IConversationStore` behavior.
- Move user settings reads/writes to Firestore.
- Move conversation list/detail/title/delete/export reads to Firestore.
- Handle delete cascades explicitly because Firestore does not enforce relational cascade deletes.

Status: complete. User settings read/write through `FirestoreUserSettingsStore`, conversations/messages use `FirestoreConversationStore`, and PostgreSQL fallback has been removed.

### Phase 5: Firestore Environment Verification

- Deploy composite indexes from `../firestore.indexes.json`.
- Verify Firebase token authentication against the real Firebase project.
- Verify Firestore reads/writes for `users`, `conversations`, and `messages`.
- Verify export output from Firestore-backed conversations.

Status: in progress. The app reaches Firebase Auth and Firestore; the real project still needs all required Firestore composite indexes to finish building.

### Phase 6: Cloud Run Deployment

- Build the existing ASP.NET Core API container for Cloud Run.
- Configure service account permissions for Firebase Admin, Firestore, and Vertex AI.
- Configure environment variables for project, location, default provider, and model settings.
- Verify SSE streaming timeout behavior and Cloud Run concurrency settings.

Status: planned.

### Phase 7: Remove Legacy Infrastructure

- Remove local email/password auth, session table, cookie auth, password reset, and email verification workflows.
- Remove PostgreSQL/EF Core dependencies.
- Replace database health checks with Firestore readiness checks.
- Update Docker Compose to match cloud-backed Firebase/Firestore development.

Status: complete for local Docker runtime and application code.

## Known Risks

- Firestore documents are limited to 1 MiB. Image attachments should move to Cloud Storage before production-scale usage; messages should store attachment metadata and URLs rather than large base64 payloads.
- Firestore has no joins or cascade deletes. Ownership checks and message cleanup must be explicit.
- Long chat histories can increase Firestore read costs. Keep the existing max-history policy and add summarization before raising limits.
- Cloud Run streaming works, but timeout and buffering behavior must be verified under the deployed ingress path.
- Firestore composite indexes must exist before list/history queries work in a new Firebase project.

## Current Code Mapping

- `ChatOrchestrator` already matches the target orchestration role and should remain the central chat flow.
- `FirebaseUserContext` verifies Firebase tokens and produces the current authenticated user.
- `FirestoreUserSettingsStore` owns `users/{uid}` settings.
- `FirestoreConversationStore` owns Firestore conversations and messages.
- `IConversationStore` remains the persistence boundary for chat orchestration and export flows.

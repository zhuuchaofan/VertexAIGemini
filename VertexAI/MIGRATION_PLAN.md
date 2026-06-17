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

Important indexes:

- `conversations`: `uid ASC`, `updatedAt DESC`.
- `messages`: `conversationId ASC`, `createdAt ASC`.
- `messages`: `uid ASC`, `conversationId ASC`, `createdAt DESC` if history reads filter by both `uid` and conversation.

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
- Move current cookie/session lookup behind that abstraction.
- Update API endpoints to depend on the abstraction instead of direct cookie/session reads.
- Keep the existing PostgreSQL authentication flow working during this phase.

Status: in progress.

### Phase 2: Firebase Auth

- Add Firebase Admin SDK configuration to the API host.
- Implement Firebase token verification as the production `IUserContext`.
- During the PostgreSQL transition, map Firebase `uid` to `users.firebase_uid` and return the existing local `Guid` user id to the current chat code.
- Update the browser app to use Firebase Web SDK for login, registration, logout, and token refresh.
- Send `Authorization: Bearer <idToken>` on API requests.
- Keep legacy cookie auth only as a temporary local-development fallback if needed.

Status: implemented in code; pending verification with real Firebase project config.

### Phase 3: String User IDs

- Change internal user identity from `Guid` to Firebase `uid` (`string`) across chat, conversation, export, and settings flows.
- Preserve existing `Guid` IDs only for legacy PostgreSQL migration code if needed.
- Update tests for `uid`-based authorization.

Status: in progress. The API user context and chat/conversation store contracts now carry an authenticated user object with both legacy local `Guid` and optional Firebase `uid`; the PostgreSQL implementation still uses the local `Guid`.

### Phase 4: Firestore Persistence

- Introduce storage abstractions before replacing PostgreSQL implementations.
- Add a Firestore-backed conversation store that preserves the existing `IConversationStore` behavior.
- Move user settings reads/writes to Firestore.
- Move conversation list/detail/title/delete/export reads to Firestore.
- Handle delete cascades explicitly because Firestore does not enforce relational cascade deletes.

Status: in progress. User settings now read/write through `IUserSettingsStore` with PostgreSQL and Firestore implementations selected by `USER_SETTINGS_PROVIDER`; conversation list/detail/title/delete/export and chat history persistence use `IConversationStore`, with PostgreSQL and Firestore implementations selected by `CONVERSATION_PROVIDER`.

### Phase 5: Data Migration

- Export existing PostgreSQL users, conversations, and messages.
- Map legacy user IDs to Firebase `uid`.
- Import Firestore documents with stable conversation and message IDs where possible.
- Validate counts, latest message timestamps, token counts, attachments, and export output.

Status: planned.

### Phase 6: Cloud Run Deployment

- Build the existing ASP.NET Core API container for Cloud Run.
- Configure service account permissions for Firebase Admin, Firestore, and Vertex AI.
- Configure environment variables for project, location, default provider, and model settings.
- Verify SSE streaming timeout behavior and Cloud Run concurrency settings.

Status: planned.

### Phase 7: Remove Legacy Infrastructure

- Remove local email/password auth, session table, cookie auth, password reset, and email verification workflows.
- Remove PostgreSQL/EF Core dependencies after Firestore parity is verified.
- Replace database health checks with Firestore/Vertex readiness checks.
- Update Docker Compose to match local Firebase emulator or cloud-backed development.

Status: planned.

## Known Risks

- Firestore documents are limited to 1 MiB. Image attachments should move to Cloud Storage before production-scale usage; messages should store attachment metadata and URLs rather than large base64 payloads.
- Firestore has no joins or cascade deletes. Ownership checks and message cleanup must be explicit.
- Long chat histories can increase Firestore read costs. Keep the existing max-history policy and add summarization before raising limits.
- Cloud Run streaming works, but timeout and buffering behavior must be verified under the deployed ingress path.
- Firebase `uid` is a string, while the current code uses `Guid` user IDs. This is the largest cross-cutting type migration.

## Current Code Mapping

- `ChatOrchestrator` already matches the target orchestration role and should remain the central chat flow.
- `IConversationStore` is the correct seam for replacing PostgreSQL with Firestore.
- `ApiUserContext` / cookie/session lookup is the first area to replace with Firebase token verification.
- `ConversationService` is the PostgreSQL implementation to replace with a Firestore implementation.
- `UserSettingsEndpoints` currently reads `users.default_assistant_prompt` from PostgreSQL and must move to `users/{uid}`.

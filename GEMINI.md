# 球球布丁工作室

This project is a split web/API AI workspace migrated to Firebase Auth,
Firestore, and Cloud Run.

- `apps/web`: standalone browser client served by a small Node proxy server.
- `VertexAI`: ASP.NET Core API host for workspace config, Firebase auth status,
  streaming chat, conversations, export, health checks, and provider integration.
- `VertexAI.Tests`: lightweight console-based regression test runner.

The only supported UI is the Docker web client in `apps/web`. Do not add new UI
work under the API project.

## Runtime Shape

Docker Compose runs two services:

- `web`: serves `apps/web/public` and proxies `/api/*` to the API container.
- `app`: ASP.NET Core API listening on port `8880` inside the Docker network.

Authentication is Firebase-only. The browser signs in with the Firebase Web SDK
and sends a Firebase ID token as `Authorization: Bearer <token>`. The API
verifies that token with Firebase Admin and uses the Firebase `uid` as the
Firestore owner key.

Firestore is the only application persistence layer. It stores:

- `users/{uid}` for user profile and workspace settings.
- `conversations/{conversationId}` for conversation metadata and model config.
- `messages/{messageId}` for chat history and attachments metadata.

## Backend Boundaries

- `Api`: minimal API endpoint groups.
- `Services/Auth`: Firebase ID token verification and current-user resolution.
- `Services/Chat`: chat request contracts, model provider abstraction, streaming
  orchestration, Firestore persistence, attachment validation, and error mapping.
- `Services/Firestore`: Firestore ownership/document ID helpers.
- `Services/Health`: liveness and Firestore readiness checks.
- `Configuration`: dependency registration and middleware/endpoint composition.

Keep endpoint handlers thin. Put application behavior in services and
provider-specific logic behind `IChatModelProvider` / `IChatModelClient`.

## Frontend Boundaries

The frontend is vanilla HTML/CSS/JavaScript in `apps/web/public`:

- `index.html`: static shell and message template.
- `app.js`: Firebase auth, provider selection, SSE chat streaming, history,
  attachments, export, and Markdown rendering.
- `styles.css`: responsive workspace layout and message presentation.

The frontend consumes only the API contract exposed by the backend. Do not call
model providers directly from browser code.

## Development Checks

Use these from the repository root:

```bash
dotnet restore VertexAI/VertexAI.csproj
dotnet build VertexAI/VertexAI.csproj --no-restore
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-restore
npm --prefix apps/web run check
```

For Docker verification:

```bash
cd VertexAI
docker compose build
docker compose up -d
docker compose ps
```

The API health checks are `/health/live` and `/health/ready`. The readiness
check verifies Firestore connectivity.

## Migration Notes

- Do not reintroduce PostgreSQL, EF Core, cookies, local sessions, SMTP, password
  reset, or email verification workflows.
- Firestore has no cascade deletes, so conversation deletion must explicitly
  remove messages.
- Top-level Firestore collections must always include `uid` ownership fields.
- Composite indexes are tracked in `firestore.indexes.json`.

# 球球布丁工作室

This project is a split web/API AI workspace deployed to Cloud Run. It uses
Firebase Auth, Firestore, Cloud Storage, and Vertex AI / Gemini.

- `apps/web`: standalone browser client served by a small Node proxy service.
- `VertexAI`: ASP.NET Core API host for workspace config, Firebase auth status,
  streaming chat, conversations, export, health checks, and provider integration.
- `VertexAI.Tests`: lightweight console-based regression test runner.

The only supported UI is the Cloud Run web service from `apps/web`. Do not add
new UI work under the API project.

## Runtime Shape

Cloud Run runs two public services:

- `vertex-ai-web`: serves `apps/web/public` and proxies `/api/*` to the API URL.
- `vertex-ai-api`: ASP.NET Core API on port `8080`.

Authentication is Firebase-only. The browser signs in with the Firebase Web SDK
and sends a Firebase ID token as `Authorization: Bearer <token>`. The API
verifies that token with Firebase Admin and uses the Firebase `uid` as the
Firestore owner key.

Persistence and object storage are intentionally separate:

- Firestore stores users, conversation metadata, messages, and attachment
  metadata.
- Cloud Storage stores private uploaded attachment objects.
- Vertex AI / Gemini owns generation and remains stateless.

## Backend Boundaries

- `Api`: minimal API endpoint groups.
- `Services/Auth`: Firebase ID token verification and current-user resolution.
- `Services/Chat`: chat request contracts, model provider abstraction, streaming
  orchestration, Firestore persistence coordination, attachment validation, and
  error mapping.
- `Services/Attachments`: attachment object storage abstraction and Cloud
  Storage implementation.
- `Services/Firestore`: Firestore ownership/document ID helpers.
- `Services/Health`: liveness and Firestore readiness checks.
- `Configuration`: dependency registration and middleware/endpoint composition.

Keep endpoint handlers thin. Put application behavior in services and
provider-specific logic behind `IChatModelProvider` / `IChatModelClient`.

## Frontend Boundaries

The frontend is vanilla HTML/CSS/JavaScript in `apps/web/public`:

- `index.html`: static shell and message template.
- `app.js`: Firebase auth, provider selection, SSE chat streaming, history,
  image compression, attachments, export, and Markdown rendering.
- `styles.css`: responsive workspace layout and message presentation.

The frontend consumes only the API contract exposed by the backend. Do not call
model providers directly from browser code.

## Checks

Use these before deployment:

```bash
npm --prefix apps/web run check
dotnet restore VertexAI.slnx
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-restore
```

The API health checks are `/health/live` and `/health/ready`. The readiness
check verifies Firestore connectivity.

## Guardrails

- Do not reintroduce PostgreSQL, EF Core, cookies, sessions, SMTP, password
  reset, email verification workflows, or credential JSON files.
- Firestore has no cascade deletes, so conversation deletion must explicitly
  remove messages.
- Top-level Firestore collections must always include `uid` ownership fields.
- Composite indexes are tracked through Cloud Firestore configuration and must
  be deployed before routing production traffic.

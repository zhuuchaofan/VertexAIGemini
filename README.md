# 球球布丁工作室

A model-neutral AI workspace in transition from a Blazor Server Gemini chat app to a split frontend/API architecture. It supports local authentication, persisted conversations, streaming responses, thinking output display, image attachments, email verification, export endpoints, and Docker-based PostgreSQL setup.

## What Is Included

- Standalone web workspace under `apps/web` that talks to the backend through HTTP APIs and SSE.
- ASP.NET Core API host with streaming model responses; the legacy Blazor UI can be enabled only as a transition path.
- Model-neutral chat contracts and provider catalog with Google.GenAI / Vertex AI implemented as the current provider adapter.
- Local email/password authentication with HttpOnly cookies.
- PostgreSQL persistence for users, sessions, conversations, messages, and token counts.
- Conversation management APIs for listing, loading, renaming, and deleting workspace sessions.
- Conversation export endpoints.
- Image validation and compression before sending multimodal prompts.
- SMTP hooks for verification and password reset emails.
- Docker Compose setup for the web client, API host, and PostgreSQL.

Authentication uses the model-neutral `vertex_auth` HttpOnly cookie. The backend can still read the legacy `gemini_auth` cookie during migration, but new sign-ins clear it.

## Requirements

- .NET 10 SDK
- Node.js 24+ for the standalone web client
- PostgreSQL 15+ or Docker
- Google Cloud project with Vertex AI enabled
- Google application credentials JSON

## Configuration

The app reads `VertexAI/appsettings.json`, environment variables, and an optional `.env` file when running from the `VertexAI/` directory.

Important settings:

| Setting | Description |
| --- | --- |
| `ConnectionStrings:Default` | PostgreSQL connection string |
| `VertexAI:ProjectId` | Google Cloud project id |
| `VertexAI:Location` | Vertex AI location, defaults to `global` |
| `VertexAI:ModelName` | Gemini model name |
| `Workspace:EnableLegacyBlazor` | Enables the old Blazor UI host when `true`; Docker/production split deployments set this to `false` |
| `Workspace:DefaultProviderId` / `DEFAULT_PROVIDER_ID` | Default provider shown by the standalone web client, for example `gemini`; falls back to the first registered provider if misconfigured |
| `OpenAICompatible:*` / `OPENAI_COMPATIBLE_*` | Optional OpenAI-compatible chat completions provider |
| `SMTP_USER` / `SMTP_PASSWORD` | Optional SMTP credentials |
| `APP_BASE_URL` | Base URL used in email links |
| `DATABASE_URL` | Optional override for the database connection |
| `WEB_PORT` | Optional Docker host port for the standalone web workspace |

For local backend usage:

```powershell
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\path\to\credentials.json"
$env:VertexAI__ProjectId="your-google-cloud-project"
dotnet run --project VertexAI\VertexAI.csproj --urls "http://localhost:5000"
```

Open `http://localhost:5000`.

For the standalone web workspace:

```bash
cd apps/web
BACKEND_URL=http://localhost:5000 npm run dev
```

Open `http://localhost:5173`.

## Docker

Create a `.env` file in `VertexAI/` by copying `VertexAI/.env.example`:

```env
PROJECT_ID=copper-affinity-467409-k7
GCP_KEY_PATH=./GCPKey/copper-affinity-467409-k7-7ba2e06ec019.json
DB_PASSWORD=GeminiChat2024!
APP_BASE_URL=http://localhost:8880
WEB_PORT=8880
DEFAULT_PROVIDER_ID=gemini
OPENAI_COMPATIBLE_ENABLED=false
```

Then run:

```bash
cd VertexAI
docker compose up --build
```

Open `http://localhost:8880`.
The standalone web workspace is served on the same external port as the legacy app. API calls are proxied internally from the web container to `app:8880`.
The API container sets `Workspace__EnableLegacyBlazor=false`, so Docker runs the backend as an API host instead of exposing the transitional Blazor UI. The sample `.env` defaults `DEFAULT_PROVIDER_ID=gemini`; make sure the mounted Google credential file and project id are ready before using the Gemini provider.

Docker checks `http://localhost:5173/` inside the web container and `http://localhost:8880/health/live` inside the API container. Use `/health/ready` when you need to verify database connectivity before routing traffic.

Useful checks:

```powershell
Invoke-WebRequest http://localhost:5000/health/live
Invoke-WebRequest http://localhost:5000/health/ready
```

## Development

Restore and build:

```powershell
dotnet restore VertexAI.slnx
dotnet build VertexAI.slnx --no-restore
dotnet run --project VertexAI.Tests\VertexAI.Tests.csproj --no-restore
```

Run these checks before publishing a deployment image; CI can use the same commands when a workflow is added.

### Provider Configuration

The workspace exposes provider capabilities through `GET /api/workspace/config`. Gemini is registered by default, and an OpenAI-compatible provider can be enabled through configuration:

| Provider | Purpose |
| --- | --- |
| `gemini` | Google GenAI / Vertex AI Gemini adapter |
| `openai-compatible` | Optional provider for OpenAI-compatible `/chat/completions` APIs such as OpenAI, LiteLLM, vLLM gateways, or local adapters |

Enable the OpenAI-compatible provider with `OPENAI_COMPATIBLE_ENABLED=true`, `OPENAI_COMPATIBLE_ENDPOINT`, `OPENAI_COMPATIBLE_API_KEY`, and `OPENAI_COMPATIBLE_MODEL`.

Health endpoints:

| Endpoint | Purpose |
| --- | --- |
| `/health/live` | Process liveness check used by Docker healthchecks |
| `/health/ready` | Readiness check that includes database connectivity |

The default database bootstrap path uses EF `EnsureCreated` plus compatibility migrations in `VertexAI/Data/DatabaseInitializer.cs`. For an external database, `VertexAI/Database/init.sql` contains the equivalent schema.

## Project Structure

```text
apps/
  web/                Standalone frontend workspace and local proxy server

VertexAI/
  Api/                 Minimal API endpoints
  Components/          Legacy Blazor pages and chat components, kept behind Workspace:EnableLegacyBlazor
  Configuration/       Service registration and HTTP pipeline setup
  Data/                EF Core context, entities, database initialization
  Database/            Optional SQL bootstrap script
  Services/            Application services and Gemini integration
  Services/Auth/       Auth workflows, cookies, sessions, token generation, validation, and rate limiting
  Services/Chat/       Chat orchestration, request models, and error mapping
  wwwroot/             Browser JavaScript assets
```

## Current Architecture Direction

The startup path is intentionally split:

- `Program.cs` is the composition root.
- `Configuration/ServiceCollectionExtensions.cs` owns dependency registration.
- `Configuration/WebApplicationExtensions.cs` owns middleware and endpoint mapping.
- `Data/DatabaseInitializer.cs` owns startup database preparation.
- `Services/Auth/AuthWorkflowService.cs` owns login, registration, password reset, email verification, and session workflows.
- `Services/Auth/*` owns authentication infrastructure such as cookies, session storage, tokens, validation, and rate limiting.
- `Services/Chat/ChatOrchestrator.cs` owns chat sending, streaming, persistence, and token updates.
- `Services/Chat/IChatModelClient.cs` and `IConversationStore.cs` isolate model providers and persistence from chat orchestration.

This keeps future additions, such as alternate AI providers, richer auth, observability, migrations, or background jobs, from accumulating in `Program.cs`.

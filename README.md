# Gemini Chat

A Blazor Server chat application for Google Vertex AI Gemini. It supports local authentication, persisted conversations, streaming responses, thinking output display, image attachments, email verification, export endpoints, and Docker-based PostgreSQL setup.

## What Is Included

- Blazor Server chat UI with streaming model responses.
- Google.GenAI / Vertex AI integration.
- Local email/password authentication with HttpOnly cookies.
- PostgreSQL persistence for users, sessions, conversations, messages, and token counts.
- Conversation export endpoints.
- Image validation and compression before sending multimodal prompts.
- SMTP hooks for verification and password reset emails.
- Docker Compose setup for the app and PostgreSQL.

## Requirements

- .NET 10 SDK
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
| `SMTP_USER` / `SMTP_PASSWORD` | Optional SMTP credentials |
| `APP_BASE_URL` | Base URL used in email links |
| `DATABASE_URL` | Optional override for the database connection |

For local shell usage:

```powershell
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\path\to\credentials.json"
$env:VertexAI__ProjectId="your-google-cloud-project"
dotnet run --project VertexAI\VertexAI.csproj --urls "http://localhost:5000"
```

Open `http://localhost:5000`.

## Docker

Create a `.env` file in `VertexAI/` by copying `VertexAI/.env.example`:

```env
PROJECT_ID=your-google-cloud-project
GCP_KEY_PATH=./GCPKey/credentials.json
DB_PASSWORD=replace-this-password
APP_BASE_URL=http://localhost:8880
```

Then run:

```bash
cd VertexAI
docker compose up --build
```

Open `http://localhost:8880`.

Docker also checks `http://localhost:8880/health/live` inside the container. Use `/health/ready` when you need to verify database connectivity before routing traffic.

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

CI runs the same restore, build, and service-test checks through `.github/workflows/ci.yml`.

Health endpoints:

| Endpoint | Purpose |
| --- | --- |
| `/health/live` | Process liveness check used by Docker healthchecks |
| `/health/ready` | Readiness check that includes database connectivity |

The default database bootstrap path uses EF `EnsureCreated` plus compatibility migrations in `VertexAI/Data/DatabaseInitializer.cs`. For an external database, `VertexAI/Database/init.sql` contains the equivalent schema.

## Project Structure

```text
VertexAI/
  Api/                 Minimal API endpoints
  Components/          Blazor pages and chat components
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

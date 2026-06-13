# 球球布丁工作室

This project is a split web/API AI workspace:

- `apps/web`: standalone browser client served by a small Node proxy server.
- `VertexAI`: ASP.NET Core API host for auth, workspace config, streaming chat, conversations, export, health checks, and provider integration.
- `VertexAI/Database` and `VertexAI/Data`: PostgreSQL schema and EF Core persistence.

The only supported UI is the Docker web client in `apps/web`. Do not add new UI work under the API project.

## Runtime Shape

Docker Compose runs three services:

- `web`: serves `apps/web/public` and proxies `/api/*` to the API container.
- `app`: ASP.NET Core API listening on port `8880` inside the Docker network.
- `db`: PostgreSQL.

The browser should open the web service, normally `http://localhost:8880`.

## Backend Boundaries

- `Api`: minimal API endpoint groups.
- `Services/Auth`: authentication workflow, cookies, sessions, validation, rate limiting, and token generation.
- `Services/Chat`: chat request contracts, model provider abstraction, streaming orchestration, attachment validation, persistence coordination, and error mapping.
- `Services`: external integrations such as Gemini, OpenAI-compatible providers, conversation persistence, and email.
- `Configuration`: dependency registration and middleware/endpoint composition.
- `Data`: EF Core context, entities, and startup database initialization.

Keep endpoint handlers thin. Put application behavior in services and provider-specific logic behind `IChatModelProvider` / `IChatModelClient`.

## Frontend Boundaries

The frontend is vanilla HTML/CSS/JavaScript in `apps/web/public`:

- `index.html`: static shell and message template.
- `app.js`: auth, provider selection, SSE chat streaming, history, attachments, export, and Markdown rendering.
- `styles.css`: responsive workspace layout and message presentation.

The frontend consumes only the API contract exposed by the backend. Do not call model providers directly from browser code.

## Development Checks

Use these from the `VertexAI/` directory:

```bash
dotnet restore VertexAI.slnx
dotnet build VertexAI.slnx --no-restore
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-restore
```

For Docker verification:

```bash
cd VertexAI
docker compose build
docker compose up -d
docker compose ps
```

The API health checks are `/health/live` and `/health/ready`.

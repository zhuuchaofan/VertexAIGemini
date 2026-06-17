# 球球布丁工作室

A model-neutral AI workspace with a standalone web frontend and an ASP.NET Core API backend. It uses Firebase Authentication for identity, Firestore for user settings and chat persistence, and Vertex AI / Gemini for model responses.

## What Is Included

- Standalone web workspace under `apps/web` that talks to the backend through HTTP APIs and SSE.
- ASP.NET Core API host with streaming model responses.
- Model-neutral chat contracts and provider catalog with Google.GenAI / Vertex AI implemented as the current provider adapter.
- Firebase Authentication for registration, login, and ID tokens.
- Firestore persistence for users, conversations, messages, and token counts.
- Conversation management APIs for listing, loading, renaming, and deleting workspace sessions.
- Conversation export endpoints.
- Image validation before sending multimodal prompts.
- Docker Compose setup for the web client and API host.

Authentication is Firebase-only. The browser signs in with the Firebase Web SDK and sends the Firebase ID token as a Bearer token to the API. The backend verifies that token and uses the Firebase `uid` as the owner key for Firestore documents.

## Requirements

- .NET 10 SDK
- Node.js 24+ for the standalone web client
- Google Cloud project with Vertex AI enabled
- Firebase project with Authentication and Firestore enabled
- Google application credentials JSON

## Configuration

The app reads `VertexAI/appsettings.json`, environment variables, and an optional `.env` file when running from the `VertexAI/` directory.

Important settings:

| Setting | Description |
| --- | --- |
| `VertexAI:ProjectId` | Google Cloud project id |
| `Firebase:ProjectId` / `FIREBASE_PROJECT_ID` | Firebase project id for ID token verification; defaults can match the Vertex AI project |
| `Firebase:ApiKey`, `Firebase:AuthDomain`, `Firebase:AppId` / `FIREBASE_API_KEY`, `FIREBASE_AUTH_DOMAIN`, `FIREBASE_APP_ID` | Firebase Web SDK config exposed to the browser |
| `Persistence:FirestoreProjectId` / `FIRESTORE_PROJECT_ID` | Optional Firestore project override; falls back to Firebase or Vertex project id |
| `VertexAI:Location` | Vertex AI location, defaults to `global` |
| `VertexAI:ModelName` | Gemini model name |
| `Workspace:DefaultProviderId` / `DEFAULT_PROVIDER_ID` | Default provider shown by the standalone web client, for example `gemini`; falls back to the first registered provider if misconfigured |
| `OpenAICompatible:*` / `OPENAI_COMPATIBLE_*` | Optional single OpenAI-compatible chat completions provider |
| `OpenAICompatible:Providers` | Optional provider templates for DeepSeek, Kimi, Qwen, Zhipu, OpenRouter, Xiaomi MiMo, or custom OpenAI-compatible gateways |
| `DEEPSEEK_API_KEY`, `MOONSHOT_API_KEY`, `DASHSCOPE_API_KEY`, `ZHIPU_API_KEY`, `OPENROUTER_API_KEY`, `XIAOMI_MIMO_API_KEY` | Optional provider keys. Setting a key enables the matching configured provider when its endpoint is present |
| `APP_BASE_URL` | Public base URL for the Docker-hosted workspace |
| `WEB_PORT` | Optional Docker host port for the standalone web workspace |

For local backend usage:

```powershell
$env:GOOGLE_APPLICATION_CREDENTIALS="C:\path\to\credentials.json"
$env:VertexAI__ProjectId="your-google-cloud-project"
$env:Firebase__ProjectId="your-google-cloud-project"
$env:Firebase__ApiKey="your-firebase-web-api-key"
dotnet run --project VertexAI\VertexAI.csproj --urls "http://localhost:5000"
```

The backend exposes APIs and health checks on `http://localhost:5000`.

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
FIREBASE_PROJECT_ID=my-agent-app-a5e42
FIREBASE_API_KEY=
FIREBASE_AUTH_DOMAIN=
FIREBASE_APP_ID=
FIRESTORE_PROJECT_ID=my-agent-app-a5e42
GCP_KEY_PATH=./GCPKey/copper-affinity-467409-k7-7ba2e06ec019.json
APP_BASE_URL=http://localhost:8880
WEB_PORT=8880
DEFAULT_PROVIDER_ID=gemini
OPENAI_COMPATIBLE_ENABLED=false
DEEPSEEK_API_KEY=
MOONSHOT_API_KEY=
DASHSCOPE_API_KEY=
ZHIPU_API_KEY=
OPENROUTER_API_KEY=
```

Then run:

```bash
cd VertexAI
docker compose up --build
```

Open `http://localhost:8880`.
The standalone web workspace is served on the external port, and API calls are proxied internally from the web container to `app:8880`. The sample `.env` defaults `DEFAULT_PROVIDER_ID=gemini`; make sure the mounted Google credential file and project id are ready before using the Gemini provider.

Docker checks `http://localhost:5173/` inside the web container and `http://localhost:8880/health/live` inside the API container. Use `/health/ready` when you need to verify Firestore connectivity before routing traffic.

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

The workspace exposes provider capabilities through `GET /api/workspace/config`. Gemini is registered by default. OpenAI-compatible providers are enabled from `OpenAICompatible:Providers` when they have an endpoint, model, and API key. The bundled templates cover several common providers:

| Provider | Purpose |
| --- | --- |
| `gemini` | Google GenAI / Vertex AI Gemini adapter |
| `deepseek` | DeepSeek OpenAI-compatible API |
| `kimi` | Moonshot Kimi OpenAI-compatible API |
| `qwen` | Alibaba Cloud Model Studio / DashScope OpenAI-compatible API |
| `zhipu` | Zhipu GLM OpenAI-compatible API |
| `openrouter` | OpenRouter OpenAI-compatible gateway |
| `xiaomi-mimo` | Xiaomi MiMo placeholder; configure the endpoint when using Xiaomi's official API or a compatible gateway |
| `openai-compatible` | Optional legacy single provider for OpenAI-compatible `/chat/completions` APIs such as OpenAI, LiteLLM, vLLM gateways, or local adapters |

To enable a bundled provider, set the matching API key in `.env` and rebuild/restart Docker. To add another OpenAI-compatible provider, add a new object under `OpenAICompatible:Providers` with `ProviderId`, `Name`, `Endpoint`, `ApiKeyEnv`, `ModelName`, and `Models`. Keep secrets in environment variables, not in `appsettings.json`.

Health endpoints:

| Endpoint | Purpose |
| --- | --- |
| `/health/live` | Process liveness check used by Docker healthchecks |
| `/health/ready` | Readiness check that includes Firestore connectivity |

## Project Structure

```text
apps/
  web/                Standalone frontend workspace and local proxy server

VertexAI/
  Api/                 Minimal API endpoints
  Configuration/       Service registration and HTTP pipeline setup
  Services/            Application services and Gemini integration
  Services/Auth/       Firebase token verification and current-user resolution
  Services/Chat/       Chat orchestration, request models, Firestore persistence, and error mapping
```

## Current Architecture Direction

The startup path is intentionally split between the standalone frontend and API backend:

- `Program.cs` is the composition root.
- `Configuration/ServiceCollectionExtensions.cs` owns dependency registration.
- `Configuration/WebApplicationExtensions.cs` owns middleware and endpoint mapping.
- `Services/Auth/FirebaseUserContext.cs` owns Firebase ID token verification.
- `Services/UserSettings/FirestoreUserSettingsStore.cs` owns `users/{uid}` settings.
- `Services/Chat/ChatOrchestrator.cs` owns chat sending, streaming, persistence, and token updates.
- `Services/Chat/FirestoreConversationStore.cs` owns `conversations` and `messages` Firestore documents.
- `Services/Chat/IChatModelClient.cs` and `IConversationStore.cs` isolate model providers and persistence from chat orchestration.

This keeps future additions, such as alternate AI providers, richer auth, observability, migrations, or background jobs, from accumulating in `Program.cs`.

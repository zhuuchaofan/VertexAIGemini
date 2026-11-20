# 球球布丁工作室

Cloud-first AI workspace deployed on Google Cloud Run. The browser frontend is
served by a lightweight Node proxy service, and the backend is an ASP.NET Core
API that uses Firebase Authentication, Firestore, Cloud Storage, and Vertex AI /
Gemini.

## Runtime Shape

- `vertex-ai-web`: Cloud Run web service under `apps/web`.
- `vertex-ai-api`: Cloud Run API service under `VertexAI`.
- Firebase Authentication: browser sign-in and bearer-token identity.
- Firestore: user settings, conversations, messages, and token counts.
- Cloud Storage: private attachment objects for uploaded images and files.
- Vertex AI / Gemini: default model provider.
- OpenAI-compatible providers: optional provider adapters configured by
  environment variables.

The frontend sends Firebase ID tokens to the API. The API verifies those tokens
and uses the Firebase-backed user key for Firestore ownership checks.

## Configuration

Production configuration is supplied through Cloud Run environment variables and
GitHub Actions variables/secrets. Do not commit credentials or service account
key files.

Important settings:

| Setting | Description |
| --- | --- |
| `PROJECT_ID`, `VertexAI__ProjectId` | Google Cloud project used for Vertex AI |
| `FIREBASE_PROJECT_ID`, `Firebase__ProjectId` | Firebase project used for auth |
| `FIRESTORE_PROJECT_ID`, `Persistence__FirestoreProjectId` | Firestore project |
| `ATTACHMENT_STORAGE_BUCKET`, `Persistence__AttachmentStorageBucket` | Private Cloud Storage bucket for attachments |
| `Firebase__ApiKey`, `Firebase__AuthDomain`, `Firebase__AppId` | Firebase Web SDK config exposed to the browser |
| `Workspace__DefaultProviderId` / `DEFAULT_PROVIDER_ID` | Default provider, usually `gemini` |
| `OpenAICompatible:Providers` and provider API key env vars | Optional OpenAI-compatible providers |

## Deployment

Cloud Run deployment is documented in [docs/cloud-run.md](docs/cloud-run.md) (project `zhumyproject`, region `asia-east1`):

- **Web (`vertex-ai-web`)**: automatically deployed on pushes to `main` touching `apps/web/**` or `cloudbuild-web.yaml` via Cloud Build trigger `deploy-web` (`asia-east1`).
- **API (`vertex-ai-api`)**: manually deployed via `bash scripts/deploy-api.sh` (image-only release to the existing service).

Initial / full deployment script pattern:

```bash
SERVICE_ACCOUNT=vertex-express@zhumyproject.iam.gserviceaccount.com \
PROJECT_ID=zhumyproject \
FIREBASE_PROJECT_ID=zhumyproject \
FIRESTORE_PROJECT_ID=zhumyproject \
FIREBASE_API_KEY=... \
FIREBASE_APP_ID=... \
./scripts/deploy-cloud-run.sh
```

`.github/workflows/deploy-cloud-run.yml` is manual-only (`workflow_dispatch`) and does not deploy automatically on pushes to `main`.

## Checks

Before deploying:

```bash
npm run check --prefix apps/web
dotnet restore VertexAI/VertexAI.csproj
dotnet restore VertexAI.Tests/VertexAI.Tests.csproj
dotnet build VertexAI/VertexAI.csproj --no-restore --disable-build-servers
dotnet build VertexAI.Tests/VertexAI.Tests.csproj --no-restore --disable-build-servers
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-build
```

After deploying:

```bash
curl -i https://API_URL/health/live
curl -i https://API_URL/health/ready
curl -i https://WEB_URL/
```

## Architecture Notes

- `Program.cs` is the composition root.
- `Configuration/ServiceCollectionExtensions.cs` owns dependency registration.
- `Configuration/WebApplicationExtensions.cs` owns middleware and endpoint
  mapping.
- `Services/Auth/FirebaseUserContext.cs` owns Firebase token verification.
- `Services/Chat/ChatOrchestrator.cs` owns chat flow, streaming, persistence,
  and token updates.
- `Services/Chat/IChatModelClient.cs` and `IChatModelProvider` isolate model
  providers.
- `Services/Chat/IConversationStore.cs` isolates conversation persistence.
- `Services/Attachments/IChatAttachmentStore.cs` isolates attachment object storage.

This keeps provider integrations, persistence, attachment storage, and API
composition behind narrow interfaces so the deployed system can evolve without
spreading cloud-specific details through the request flow.

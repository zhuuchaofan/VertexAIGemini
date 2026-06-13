# 球球布丁工作室 Web

Standalone browser client for the split frontend/backend architecture.

## Local Development

Run the ASP.NET backend first:

```bash
dotnet run --project VertexAI/VertexAI.csproj --urls http://localhost:5000
```

Then run this web client:

```bash
cd apps/web
npm run dev
```

Open `http://localhost:5173`.

The web server serves static assets and proxies `/api/*` to `BACKEND_URL`, which defaults to `http://localhost:5000`. Override it when the backend runs elsewhere:

```bash
BACKEND_URL=http://localhost:8880 npm run dev
```

## API Contract Used

- `GET /api/auth/status`
- `POST /api/auth/login`
- `POST /api/auth/register`
- `POST /api/auth/logout`
- `GET /api/workspace/config`
- `POST /api/chat/stream`
- `GET /api/conversations/`
- `GET /api/conversations/{id}`
- `PATCH /api/conversations/{id}/title`
- `DELETE /api/conversations/{id}`
- `GET /api/export/{id}/markdown`
- `GET /api/export/{id}/json`

`GET /api/workspace/config` returns provider, model, and prompt preset catalogs. `/api/chat/stream` accepts `providerId`, `modelName`, `presetId`, and `customPrompt`, then streams Server-Sent Events with `update` and `final` event names.

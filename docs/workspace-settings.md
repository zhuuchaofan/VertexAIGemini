# Workspace settings

The sidebar includes a Gemini-only **高级设置** panel. It sends per-request
temperature (0–2), Top-P (>0–1), maximum output tokens (1–65536), and safety
threshold overrides. Blank values follow server defaults (1, 0.9, 8192).
Actual output limits still depend on the selected model. Gemini 3.8 Flash and
4 Argon ignore temperature and Top-P, so those controls are hidden and the
parameters are omitted from generation requests for these models.
Preferences are retained in this browser, not written to Firestore conversations.
They apply on each send, including existing conversations; changing them does
not rewrite prior messages.

The authenticated `/api/auth/status` response provides the allowed safety
thresholds. The API independently enforces those permissions before starting
the SSE response. For internal testing, appsettings.json explicitly sets
`VertexAI:Safety:AllowUserOverrides=true`: all signed-in users can select OFF,
BLOCK_NONE, BLOCK_ONLY_HIGH, BLOCK_MEDIUM_AND_ABOVE, or BLOCK_LOW_AND_ABOVE.
This does not grant administrator privileges or change the default threshold.
Set the Cloud Run environment variable `VertexAI__Safety__AllowUserOverrides=false`
to restore restricted operation: ordinary users can select the server threshold
or stricter settings. Administrators with a Firebase custom claim
`admin: true` can relax it when `VertexAI:Safety:AdminCanDisable` is enabled.
Owning the Google Cloud project or logging in with a particular email does not
automatically grant that claim. Grant claims only through a trusted Admin SDK
workflow, preserving existing claims, then refresh the Firebase ID token.
This change does not grant any account administrator access.
Provider-enforced restrictions remain in effect even when configurable filters
are disabled.

## Verified model specifications

The requested `google/gemini-3.8-flash` and `google/gemini-4-argon` entries
use the Google publisher's Vertex AI model IDs `gemini-3.8-flash` and
`gemini-4-argon`. These are the only two built-in Gemini entries; the default is
3.8 Flash. Old saved model selections fall back to the new default. Conversation
history is retained.

The official display name is **Gemini 4 Argon**, not "Gemini 4 Pro (Argon)".
The application pins Google.GenAI **1.26.1**; Google's Argon guide requires
at least **1.26.0** for .NET. The 1.x SDK's typed string values replace the
older uppercase enum members. Filtering configuration continues to use the
wire names (for example `BLOCK_MEDIUM_AND_ABOVE`) through explicit mapping.
Required model thinking cannot be disabled through a request override.
The retired civic-integrity filter is no longer sent.

Google's official guides confirm both model IDs and LOW, MEDIUM (default),
and HIGH thinking levels. Both entries expose only those three levels.
3.8 Flash has a 1,048,576-token context window; 4 Argon has a unified
1,048,576-token input/output budget. The application continues to use its
configured 32000-token history budget and caps a streaming response at 65536
tokens. It does not implement Argon's long-output continuation protocol.
Live authenticated model access in this particular project remains unverified.

Sources:
- https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/guides/gemini-3-8-flash
- https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/guides/gemini-4-argon

Deploy both the API and web images to expose the new controls and catalog.
Existing Cloud Run `VertexAI__ModelName` / `VertexAI__Models__*` overrides
take precedence over appsettings.json; update them explicitly if they pin an
older catalog. Do not clear unrelated environment variables or Secret bindings.

## Validation

```bash
npm test --prefix apps/web
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj
```

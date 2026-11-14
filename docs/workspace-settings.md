# Workspace settings

The sidebar includes a Gemini-only **高级设置** panel. It sends per-request
temperature (0–2), Top-P (>0–1), maximum output tokens (1–65536), and safety
threshold overrides. Blank values follow server defaults (1, 0.9, 8192).
Actual output limits still depend on the selected model. The Gemini 4 models
ignore temperature and Top-P, so those controls are hidden and the
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

## Built-in Gemini models

The built-in Gemini entries are the three Gemini 4 models, in this order (the first is the default):

| Entry | Vertex AI model ID | Shown as |
|---|---|---|
| Gemini 4 Hydrogen (default) | `gemini-4-hydrogen` | 性价比最高，日常与编程首选 |
| Gemini 4 Carbon | `gemini-4-carbon` | 综合能力最强，适合复杂架构和疑难问题 |
| Gemini 4 Argon | `gemini-4-argon` | 能力与 Hydrogen 相近，价格更高 |

Gemini 3.8 Flash was removed. A conversation or saved setting that names a model no longer listed uses the default
model; its other choices (preset, role, search, thinking where valid) are kept. Conversation history is retained.

What was checked, and how: the three IDs were listed from `publishers/google/models` in this project, and each answered
a `generateContent` call on the `global` location with `thinkingLevel: LOW`. LOW, MEDIUM (default) and HIGH are offered
for all three, as before for Argon; MEDIUM and HIGH were not separately called for Carbon and Hydrogen. The 1,048,576
token figure is carried over from Argon's guide and was not confirmed for Carbon and Hydrogen; the application's own
32000-token history budget and 65536-token streaming cap are unchanged. The positioning text comes from the project
owner's summary of published comparisons, not from Google's model pages.

Note: in that check Hydrogen spent a 64-token output limit on thinking at LOW and returned no text, while the other two
answered. Real requests use much larger limits, so this does not affect normal use.

The application pins Google.GenAI **1.26.1**; Google's Argon guide requires at least **1.26.0** for .NET.
Required model thinking cannot be disabled through a request override. The retired civic-integrity filter is no longer
sent. The application does not implement the long-output continuation protocol.

Deploy both the API and web images to expose the new controls and catalog.
Existing Cloud Run `VertexAI__ModelName` / `VertexAI__Models__*` overrides
take precedence over appsettings.json; update them explicitly if they pin an
older catalog. Do not clear unrelated environment variables or Secret bindings.

## Validation

```bash
npm test --prefix apps/web
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj
```

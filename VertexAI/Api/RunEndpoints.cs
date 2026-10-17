using System.Text.Json;
using Microsoft.Extensions.Options;
using VertexAI.Services;
using VertexAI.Services.Auth;
using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

namespace VertexAI.Api;

/// <summary>
/// Run API, version 2. Every route answers 404 until <c>Runs:Enabled</c> is turned on, so it can ship dark.
/// </summary>
public static class RunEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapRunEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v2");
        group.MapPost("/runs", CreateRunAsync);
        group.MapPost("/runs/{runId}/stop", StopRunAsync);
        group.MapGet("/runs/{runId}", GetRunAsync);
        group.MapGet("/conversations/{conversationId:guid}", GetConversationAsync);
        group.MapGet("/me/settings", GetDefaultsAsync);
        group.MapPut("/me/settings", PutDefaultsAsync);
        group.MapPut("/conversations/{conversationId:guid}/settings", PutConversationSettingsAsync);
    }

    // ------------------------------------------------------------------------------------------------ settings

    private static async Task GetDefaultsAsync(
        HttpContext context, IUserContext users, ISettingsStore store, IOptions<RunSettings> runSettings)
    {
        if (!runSettings.Value.Enabled) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        var user = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (user == null) { await Error(context, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized"); return; }

        var defaults = await store.GetDefaultsAsync(user, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(new { defaults }, Json);
    }

    private static async Task PutDefaultsAsync(
        ChatSettingsDocument? body, HttpContext context, IUserContext users, ISettingsStore store,
        IChatProviderCatalog catalog, GeminiSafetyPolicy safety, IOptions<RunSettings> runSettings)
    {
        if (!runSettings.Value.Enabled) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        var user = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (user == null) { await Error(context, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized"); return; }

        var (settings, error) = SettingsValidator.Validate(body, catalog, safety, user, includeCustomPrompt: false);
        if (settings == null) { await Error(context, StatusCodes.Status400BadRequest, "invalid_settings", error ?? "invalid settings"); return; }

        await store.SetDefaultsAsync(user, settings, context.RequestAborted);
        await context.Response.WriteAsJsonAsync(new { defaults = await store.GetDefaultsAsync(user, context.RequestAborted) }, Json);
    }

    private static async Task PutConversationSettingsAsync(
        Guid conversationId, ChatSettingsDocument? body, HttpContext context, IUserContext users, ISettingsStore store,
        IChatProviderCatalog catalog, GeminiSafetyPolicy safety, IOptions<RunSettings> runSettings)
    {
        if (!runSettings.Value.Enabled) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        var user = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (user == null) { await Error(context, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized"); return; }

        var (settings, error) = SettingsValidator.Validate(body, catalog, safety, user, includeCustomPrompt: true);
        if (settings == null) { await Error(context, StatusCodes.Status400BadRequest, "invalid_settings", error ?? "invalid settings"); return; }

        if (!await store.SetConversationSettingsAsync(user, conversationId, settings, context.RequestAborted))
        {
            // Not yours and not there look the same.
            await Error(context, StatusCodes.Status404NotFound, "not_found", "Conversation not found.");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    internal sealed record ApiRunRequest(
        string? RunId,
        Guid? ConversationId,
        string? Message,
        IReadOnlyCollection<ChatAttachment>? Attachments,
        string? SearchMode,
        string? ProviderId,
        string? ModelName,
        string? PresetId,
        string? CustomPrompt,
        bool? ThinkingEnabled,
        string? ThinkingLevel,
        int? ThinkingBudget,
        ChatAdvancedOptions? Advanced);

    internal static async Task CreateRunAsync(
        ApiRunRequest request,
        HttpContext context,
        IUserContext users,
        RunService runs,
        IOptions<RunSettings> settings,
        GeminiSafetyPolicy safety)
    {
        if (!settings.Value.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var user = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (user == null)
        {
            await Error(context, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized");
            return;
        }

        if (!RunIds.TryParseRunId(request.RunId, out var runId))
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_run_id", "runId must be a UUID.");
            return;
        }

        if (request.ConversationId is not { } conversationId || conversationId == Guid.Empty)
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_conversation_id", "conversationId must be a UUID.");
            return;
        }

        if (request.Attachments is { Count: > 0 })
        {
            await Error(context, StatusCodes.Status400BadRequest, "attachments_not_supported",
                "Attachments are not supported on this endpoint yet.");
            return;
        }

        var message = request.Message?.Trim() ?? "";
        if (message.Length == 0)
        {
            await Error(context, StatusCodes.Status400BadRequest, "message_required", "Message is required.");
            return;
        }

        if (request.Advanced?.Validate() is { } validationError)
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_options", validationError);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.Advanced?.SafetyThreshold)
            && !safety.GetAllowedThresholds(user).Contains(request.Advanced.SafetyThreshold, StringComparer.OrdinalIgnoreCase))
        {
            await Error(context, StatusCodes.Status403Forbidden, "forbidden_threshold", "当前账户不允许使用此过滤级别。");
            return;
        }

        RunAdmission admission;
        try
        {
            admission = await runs.AdmitAsync(
                new RunStartRequest(
                    user, runId, conversationId, message, request.SearchMode,
                    new ChatSessionOptions(
                        request.ProviderId, request.ModelName, request.PresetId, request.CustomPrompt,
                        request.ThinkingEnabled, request.ThinkingLevel, request.ThinkingBudget, request.Advanced)),
                context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (ArgumentException ex)
        {
            await Error(context, StatusCodes.Status400BadRequest, "invalid_options", ex.Message);
            return;
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Unknown chat model provider", StringComparison.Ordinal))
        {
            await Error(context, StatusCodes.Status400BadRequest, "unknown_provider", ex.Message);
            return;
        }
        catch (RunAdmissionUnavailableException)
        {
            // Not admitted, or not provably so: the browser may send the same run id again.
            await Error(context, StatusCodes.Status503ServiceUnavailable, "retry_with_same_run_id",
                "无法确认请求是否已受理，请稍后用同一个请求重试。", retryable: true);
            return;
        }

        switch (admission.Result)
        {
            case AdmitResult.Conflict conflict:
                await Error(context, StatusCodes.Status409Conflict, conflict.Code,
                    conflict.Code == "idempotency_mismatch"
                        ? "同一个 runId 对应的请求内容不一致。"
                        : "请求冲突。");
                return;

            case AdmitResult.Busy busy:
                await Error(context, StatusCodes.Status409Conflict, "run_in_progress",
                    "此会话正在生成中。", extra: new { activeRunId = busy.ActiveRunId });
                return;

            case AdmitResult.Replay replay:
            {
                var writer = new SseWriter(context);
                await writer.WriteAsync(new RunEvent.Started(replay.Run, Replayed: true));
                if (replay.Run.Status.IsTerminal()) await writer.WriteAsync(new RunEvent.Done(replay.Run));
                await writer.WriteAsync(new RunEvent.End(Replayed: true));
                return;
            }

            case AdmitResult.Rejected rejected:
            {
                var writer = new SseWriter(context);
                await writer.WriteAsync(new RunEvent.Started(rejected.Run, Replayed: false));
                await writer.WriteAsync(new RunEvent.Done(rejected.Run));
                return;
            }

            case AdmitResult.Admitted:
            {
                var writer = new SseWriter(context);
                using var pings = new CancellationTokenSource();
                var pinger = PingAsync(writer, pings.Token);
                try
                {
                    await runs.ExecuteAsync(admission, writer.WriteAsync, context.RequestAborted);
                }
                finally
                {
                    pings.Cancel();
                    await pinger;
                }

                return;
            }
        }
    }

    private static async Task PingAsync(SseWriter writer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await writer.PingAsync();
            }
        }
        catch (Exception)
        {
            // the connection is gone or the run ended
        }
    }

    internal static async Task StopRunAsync(
        string runId, HttpContext context, IUserContext users, RunService runs, IOptions<RunSettings> settings)
    {
        if (!settings.Value.Enabled)
        {
            Results404(context);
            return;
        }
        var user = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (user == null)
        {
            await Error(context, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized");
            return;
        }

        var record = RunIds.TryParseRunId(runId, out var id)
            ? await runs.StopAsync(user, id, context.RequestAborted)
            : null;
        await Snapshot(context, record);
    }

    internal static async Task GetRunAsync(
        string runId, HttpContext context, IUserContext users, RunService runs, IOptions<RunSettings> settings)
    {
        if (!settings.Value.Enabled)
        {
            Results404(context);
            return;
        }
        var user = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (user == null)
        {
            await Error(context, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized");
            return;
        }

        var record = RunIds.TryParseRunId(runId, out var id)
            ? await runs.GetAsync(user, id, context.RequestAborted)
            : null;
        await Snapshot(context, record);
    }

    internal static async Task GetConversationAsync(
        Guid conversationId,
        HttpContext context,
        IUserContext users,
        RunService runs,
        IConversationStore conversations,
        IOptions<RunSettings> settings)
    {
        if (!settings.Value.Enabled)
        {
            Results404(context);
            return;
        }
        var user = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (user == null)
        {
            await Error(context, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized");
            return;
        }

        var conversation = await conversations.GetConversationAsync(conversationId, user);
        if (conversation == null)
        {
            await Error(context, StatusCodes.Status404NotFound, "not_found", "Conversation not found.");
            return;
        }

        // Listing reconciles any run whose executor vanished, so a reload always shows a settled state.
        var records = await runs.ListAsync(user, conversationId, context.RequestAborted);
        var active = records.FirstOrDefault(run => run.Status == RunStatus.Running);

        await context.Response.WriteAsJsonAsync(new
        {
            id = conversation.Id,
            title = conversation.Title,
            providerId = conversation.ProviderId,
            modelName = conversation.ModelName,
            presetId = conversation.PresetId,
            customPrompt = conversation.CustomPrompt,
            tokenCount = conversation.TokenCount,
            createdAt = conversation.CreatedAt,
            updatedAt = conversation.UpdatedAt,
            activeRunId = active?.RunId,
            // The conversation's own settings, or what the older columns say for a conversation made before they existed.
            settings = conversation.Settings ?? new ChatSettingsDocument(
                conversation.ProviderId, conversation.ModelName, conversation.PresetId,
                conversation.PresetId == "custom" ? conversation.CustomPrompt : null, null!, null, null),
            messages = conversation.Messages.OrderBy(m => m.CreatedAt).Select(m => new
            {
                id = m.Id,
                role = m.Role,
                content = m.Content,
                thinkingContent = m.ThinkingContent,
                runId = m.RunId,
                status = m.Status,
                citations = ParseCitations(m.CitationsJson),
                createdAt = m.CreatedAt
            }),
            runs = records.Select(run => RunSnapshot.From(run))
        }, Json);
    }

    private static IReadOnlyList<SearchCitation> ParseCitations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<SearchCitation>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static Task Snapshot(HttpContext context, RunRecord? record)
    {
        if (record == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return context.Response.WriteAsJsonAsync(RunSnapshot.From(record), Json);
    }

    private static void Results404(HttpContext context) => context.Response.StatusCode = StatusCodes.Status404NotFound;

    private static Task Error(
        HttpContext context, int status, string code, string message, bool retryable = false, object? extra = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { code, message, retryable, extra } }, Json);
    }
}

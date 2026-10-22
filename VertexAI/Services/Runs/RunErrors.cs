using VertexAI.Services.Chat;

namespace VertexAI.Services.Runs;

/// <summary>What was being run when it failed; only used to explain the failure.</summary>
public sealed record RunErrorContext(string? ProviderId = null, string? ModelName = null, string? FinishReason = null);

public static class RunErrors
{
    private const int MaxDetailChars = 800;

    private static readonly string[] FilterFinishReasons =
        ["content_filter", "safety", "prohibited_content", "blocklist", "spii", "image_safety", "recitation"];

    /// <summary>Turns a provider or infrastructure exception into the run's error: what happened, what to do, and for admins the details.</summary>
    public static RunError Classify(Exception exception, RunErrorContext? context = null)
    {
        var text = exception.Message;
        var detail = Detail(context, $"{exception.GetType().Name}: {text}");

        if (Contains(text, "blocked by safety") || IsFilterReason(context?.FinishReason))
            return Filtered(context, detail);
        if (Contains(text, "response was empty"))
            return Empty(context, detail);
        if (Contains(text, "Daily ") && Contains(text, "quota exceeded"))
            return ForQuota(text, detail);
        if (Contains(text, "quota") || Contains(text, "RESOURCE_EXHAUSTED"))
            return new RunError(RunReasons.Quota,
                "模型服务商的配额已用尽（请求太频繁或额度用完）。",
                false,
                "请过几分钟再试；如果一直这样，请联系管理员。",
                detail,
                "这是 Vertex AI / 上游的配额（不是应用里的每日限额）。到 Google Cloud 控制台 → IAM 和管理 → 配额 搜索对应模型的 \"requests per minute\" 申请提高，或换一个模型。");
        if (exception is TaskCanceledException or TimeoutException)
            return new RunError(RunReasons.Timeout,
                "等待模型回复超时。",
                true,
                "点「重试」；问题很长或带大附件时，可以缩短内容，或在设置里降低思考深度。",
                detail,
                "单次生成的上限是 Runs__MaxDurationSeconds（默认 270 秒），且不能超过 Web 服务的请求超时（默认 300 秒）。");
        if (Contains(text, "permission") || Contains(text, "IAM_PERMISSION_DENIED") || Contains(text, "credential") || Contains(text, "not configured") || Contains(text, "Access to the path"))
            return new RunError(RunReasons.Internal,
                "服务暂时不可用。",
                false,
                "请稍后再试；如果持续出现，请联系管理员。",
                detail,
                InfrastructureHint(text));
        if (exception is HttpRequestException || Contains(text, "Unavailable") || Contains(text, "ServerError"))
            return new RunError(RunReasons.ProviderError,
                "模型服务暂时不可用或网络中断。",
                true,
                "点「重试」；换一个模型通常也能绕过去。",
                detail,
                "到 Cloud Run → vertex-ai-api → 日志，按这次运行的时间查看上游返回的状态码；如果只有某个模型报错，多半是该模型在所选区域暂时不可用（Grok 可检查 GROK_VERTEX_LOCATION）。");

        return new RunError(RunReasons.Internal,
            "服务暂时不可用。",
            false,
            "请稍后再试；如果持续出现，请联系管理员。",
            detail,
            "到 Cloud Run → vertex-ai-api → 日志，按这次运行的时间和运行 ID 查看完整错误。");
    }

    /// <summary>The model ended with nothing to show. If it said why (a filter, the length limit), say that instead of "empty".</summary>
    public static RunError ForEmptyResponse(RunErrorContext? context) =>
        IsFilterReason(context?.FinishReason)
            ? Filtered(context, Detail(context, "The response was empty."))
            : Empty(context, Detail(context, "The response was empty."));

    /// <summary>A daily limit of this app was reached. <paramref name="raw"/> is the quota service's own text.</summary>
    public static RunError ForQuota(string? raw, string? detail = null)
    {
        var (what, setting, example) = QuotaKind(raw);
        return new RunError(RunReasons.Quota,
            $"今日{what}已用完。",
            false,
            "每天北京时间早上 8 点（UTC 0 点）重置。需要更多额度请联系管理员。",
            detail ?? $"配额服务返回：{raw}",
            $"这是应用的每日限额（Quota__{setting}）。调大它：gcloud run services update vertex-ai-api --region asia-east1 --update-env-vars=Quota__{setting}={example}；" +
            "或给你的账号加管理员标记，管理员不受每日限额限制（见手册里的「管理员标记」）。");
    }

    private static RunError Filtered(RunErrorContext? context, string detail)
    {
        var reason = string.IsNullOrWhiteSpace(context?.FinishReason) ? "" : $"（结束原因：{context.FinishReason}）";
        if (!string.Equals(context?.ProviderId, "gemini", StringComparison.OrdinalIgnoreCase))
            return new RunError(RunReasons.Safety,
                $"这条回复被模型自带的内容过滤拦截了{reason}。这个模型没有可调的过滤开关。",
                true,
                "过滤有一定随机性，可以点「重试」；也可以换一种说法，或换一个预设/模型再试。",
                detail,
                "拦截来自模型服务商（Grok 为 xAI，经 Vertex AI 返回 finish_reason=content_filter），应用和 Google Cloud 项目里都没有可关的开关。" +
                "排查办法：用中性的预设发同一句话，确认是不是提示词触发；很短的开场（比如「开始吧」）配上尺度较大的系统提示词最容易被拦。");
        return new RunError(RunReasons.Safety,
            $"这条回复被模型服务商的内容过滤拦截了{reason}。",
            true,
            "过滤有一定随机性，可以点「重试」；也可以换一种说法，或换一个预设/模型再试。",
            detail,
            "拦截发生在模型服务商那一侧，应用无法关闭它。排查办法：用中性的预设发同一句话，确认是不是提示词触发；很短的开场（比如「开始吧」）配上尺度较大的系统提示词最容易被拦。" +
            "Gemini 的过滤级别可以在 设置 → 高级 → 内容过滤 里调整（管理员账号可选更宽松的级别）。");
    }

    private static RunError Empty(RunErrorContext? context, string detail)
    {
        if (string.Equals(context?.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context?.FinishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
            return new RunError(RunReasons.EmptyResponse,
                "模型用完了输出长度上限，却没有给出可见的回复。",
                true,
                "在设置里调大「最大输出 token」，或降低思考深度，再点「重试」。",
                detail,
                "思考内容也占用输出额度；思考很长时，正文可能一个字也没写出来。");
        return new RunError(RunReasons.EmptyResponse,
            "模型没有返回任何内容。",
            true,
            "点「重试」；一直为空的话，换一个模型或预设。",
            detail,
            "到 运行详情 看「结束原因」；如果同一个模型反复为空，检查它在所选区域是否可用，并查看 Cloud Run 日志。");
    }

    private static (string What, string Setting, string Example) QuotaKind(string? raw) => raw switch
    {
        _ when Contains(raw, "request") => ("请求次数", "DailyRequestLimit", "200"),
        _ when Contains(raw, "token") => ("Token 额度", "DailyTokenLimit", "500000"),
        _ when Contains(raw, "search") => ("联网搜索次数", "DailySearchLimit", "60"),
        _ when Contains(raw, "attachment") => ("附件上传额度", "DailyAttachmentBytesLimit", "52428800"),
        _ => ("额度", "DailyTokenLimit", "500000")
    };

    private static string InfrastructureHint(string text) => text switch
    {
        _ when Contains(text, "not configured") => "服务端配置未完成：检查 Cloud Run 环境变量。",
        _ when Contains(text, "application default credentials") || Contains(text, "credential") =>
            "服务账号凭据不可用：检查 Cloud Run 运行时服务账号。",
        _ when Contains(text, "Access to the path") => "服务无法读取必要资源：检查 Cloud Run 运行时权限。",
        _ when Contains(text, "firestore") => "会话存储权限不足：给 Cloud Run 服务账号加 Firestore 权限（roles/datastore.user）。",
        _ when Contains(text, "storage") => "附件存储权限不足：检查 Cloud Run 服务账号对附件桶的权限。",
        _ => "权限不足：给 Cloud Run 运行时服务账号加 Vertex AI 权限（roles/aiplatform.user）。"
    };

    private static string Detail(RunErrorContext? context, string error)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(context?.ProviderId)) parts.Add($"服务商 {context.ProviderId}");
        if (!string.IsNullOrWhiteSpace(context?.ModelName)) parts.Add($"模型 {context.ModelName}");
        if (!string.IsNullOrWhiteSpace(context?.FinishReason)) parts.Add($"结束原因 {context.FinishReason}");
        var prefix = parts.Count > 0 ? string.Join("，", parts) + "。" : "";
        var text = error.Length > MaxDetailChars ? error[..MaxDetailChars] + "…" : error;
        return prefix + "原始信息：" + text;
    }

    private static bool IsFilterReason(string? finishReason) =>
        !string.IsNullOrWhiteSpace(finishReason)
        && FilterFinishReasons.Any(reason => finishReason.Contains(reason, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? value, string text) =>
        value != null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
}

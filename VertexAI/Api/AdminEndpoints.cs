using Microsoft.AspNetCore.Mvc;
using VertexAI.Services.Auth;
using VertexAI.Services.Quota;

namespace VertexAI.Api;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/quotas/daily", GetDailyQuotaUsageAsync);
    }

    internal static async Task<IResult> GetDailyQuotaUsageAsync(
        [FromQuery] string? date,
        [FromQuery] int? limit,
        [FromQuery] string? userId,
        HttpContext context,
        IUserContext users,
        IQuotaUsageReader quotaUsage)
    {
        var currentUser = await ApiUserContext.GetCurrentUserAsync(context, users);
        if (currentUser == null)
        {
            return Results.Unauthorized();
        }

        if (!currentUser.IsAdmin)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        string normalizedDate;
        try
        {
            normalizedDate = FirestoreChatQuotaService.NormalizeDateKey(
                string.IsNullOrWhiteSpace(date)
                    ? DateTime.UtcNow.ToString("yyyyMMdd")
                    : date);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var pageLimit = Math.Clamp(limit ?? 100, 1, 500);
        var report = await quotaUsage.GetDailyUsageAsync(
            normalizedDate,
            pageLimit,
            string.IsNullOrWhiteSpace(userId) ? null : userId.Trim(),
            context.RequestAborted);

        return Results.Ok(new DailyQuotaUsageResponse(
            report.Date,
            pageLimit,
            report.Entries.Count,
            report.Totals,
            report.Entries));
    }

    private sealed record DailyQuotaUsageResponse(
        string Date,
        int Limit,
        int Count,
        QuotaUsageTotals Totals,
        IReadOnlyList<QuotaUsageEntry> Entries);
}

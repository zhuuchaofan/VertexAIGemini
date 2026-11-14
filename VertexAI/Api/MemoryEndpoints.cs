using VertexAI.Services.Auth;
using VertexAI.Services.Memory;

namespace VertexAI.Api;

/// <summary>The person's own memory: read it, add to it, remove from it, switch it off. Never anyone else's.</summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v2/me/memory");
        group.MapGet("/", GetAsync);
        group.MapPut("/", SetEnabledAsync);
        group.MapPost("/items", AddAsync);
        group.MapDelete("/items/{itemId}", RemoveAsync);
    }

    internal sealed record MemoryAddBody(string? Text);
    internal sealed record MemoryEnabledBody(bool Enabled);

    private static object Error(string code, string message) => new { error = new { code, message, retryable = false } };

    private static object View(UserMemory memory) => new
    {
        enabled = memory.Enabled,
        items = memory.Items.OrderBy(item => item.CreatedAt).ToList(),
        maxItems = MemoryRules.MaxItems,
        maxChars = MemoryRules.MaxChars
    };

    internal static async Task<IResult> GetAsync(HttpContext context, IUserContext users, IUserMemoryStore memory)
    {
        var user = await users.GetCurrentUserAsync(context);
        if (user is null) return Results.Unauthorized();
        return Results.Ok(View(await memory.GetAsync(user, context.RequestAborted)));
    }

    internal static async Task<IResult> SetEnabledAsync(MemoryEnabledBody body, HttpContext context, IUserContext users, IUserMemoryStore memory)
    {
        var user = await users.GetCurrentUserAsync(context);
        if (user is null) return Results.Unauthorized();
        await memory.SetEnabledAsync(user, body.Enabled, context.RequestAborted);
        return Results.Ok(View(await memory.GetAsync(user, context.RequestAborted)));
    }

    internal static async Task<IResult> AddAsync(MemoryAddBody? body, HttpContext context, IUserContext users, IUserMemoryStore memory)
    {
        var user = await users.GetCurrentUserAsync(context);
        if (user is null) return Results.Unauthorized();

        var (text, error) = MemoryRules.Validate(body?.Text);
        if (error != null) return Results.BadRequest(Error("invalid_memory", error));

        var item = await memory.AddAsync(user, text, context.RequestAborted);
        return item is null
            ? Results.Conflict(Error("memory_full", $"最多记住 {MemoryRules.MaxItems} 条，请先删掉不需要的。"))
            : Results.Ok(new { item });
    }

    internal static async Task<IResult> RemoveAsync(string itemId, HttpContext context, IUserContext users, IUserMemoryStore memory)
    {
        var user = await users.GetCurrentUserAsync(context);
        if (user is null) return Results.Unauthorized();
        return await memory.RemoveAsync(user, itemId, context.RequestAborted)
            ? Results.NoContent()
            : Results.NotFound(Error("not_found", "这条记忆不存在。"));
    }
}

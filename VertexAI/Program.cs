using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using VertexAI.Api;
using VertexAI.Components;
using VertexAI.Data;
using VertexAI.Services;

// 加载 .env 环境变量文件（如果存在）
DotNetEnv.Env.Load();

// --------------------------------------------------------------------------------------
// 项目名称: Gemini Chat - Blazor Web App
// 描述: 使用 Google.GenAI SDK 调用 Gemini，可视化展示"思考过程"的聊天界面。
// --------------------------------------------------------------------------------------

// Serilog 配置
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/gemini-chat-.json",
        rollingInterval: RollingInterval.Day,
        formatter: new Serilog.Formatting.Compact.CompactJsonFormatter())
    .CreateLogger();

try
{

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// 1. 配置服务
builder.Services.Configure<GeminiSettings>(
    builder.Configuration.GetSection("VertexAI"));

// 2. 数据库配置 (本地 PostgreSQL) - 使用 Factory 避免并发问题
// 优先从环境变量读取连接字符串，否则使用 appsettings.json
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// 3. HttpContext 访问器（用于认证）
builder.Services.AddHttpContextAccessor();

// 4. 业务服务
builder.Services.AddScoped<GeminiService>();       // AI 聊天
builder.Services.AddScoped<AuthService>();          // 用户认证
builder.Services.AddHostedService<SessionCleanupService>(); // 过期 Session 清理
builder.Services.AddScoped<ConversationService>();  // 对话持久化
builder.Services.AddSingleton<MarkdownService>();   // Markdown 渲染
builder.Services.AddScoped<ImageService>();         // 图片处理

// 5. HttpClient（用于 Blazor 组件调用 API）
builder.Services.AddScoped(sp =>
{
    var navigationManager = sp.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigationManager.BaseUri) };
});

// 5. Blazor 服务
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 6. SignalR 配置（支持图片上传）
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 5 * 1024 * 1024; // 5MB
});

var app = builder.Build();


// 5. 初始化数据库
try
{
    var dbFactory = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    // 手动添加 TokenCount 列（如果不存在）
    try
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE conversations ADD COLUMN IF NOT EXISTS \"TokenCount\" INTEGER DEFAULT 0");
        // 邮箱验证预留字段
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS email_verified BOOLEAN DEFAULT FALSE");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_token VARCHAR(64)");
    }
    catch (Exception ex)
    {
        // 列已存在是预期内的，但其他错误应该记录
        if (!ex.Message.Contains("already exists"))
        {
             Log.Warning(ex, "尝试添加 TokenCount 列时发生非关键错误");
        }
    }

    Console.WriteLine("数据库连接成功");
}
catch (Exception ex)
{
    Console.WriteLine($"警告: 数据库连接失败 - {ex.Message}");
    Console.WriteLine("应用将以离线模式启动（对话历史不会持久化）");
}

// 6. 配置中间件
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

// 8. 认证 API 端点
app.MapAuthEndpoints();

// 9. 配置 Blazor 路由
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

Log.Information("Gemini Chat 已启动 - http://localhost:5000");

app.Run();

}
catch (Exception ex)
{
    Log.Fatal(ex, "应用程序启动失败");
}
finally
{
    Log.CloseAndFlush();
}

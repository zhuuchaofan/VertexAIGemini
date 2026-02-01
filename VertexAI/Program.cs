using Microsoft.EntityFrameworkCore;
using VertexAI.Components;
using VertexAI.Data;
using VertexAI.Services;

// --------------------------------------------------------------------------------------
// 项目名称: Gemini Chat - Blazor Web App
// 描述: 使用 Google.GenAI SDK 调用 Gemini，可视化展示"思考过程"的聊天界面。
// --------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// 1. 配置服务
builder.Services.Configure<GeminiSettings>(
    builder.Configuration.GetSection("VertexAI"));

// 2. 数据库配置 (本地 PostgreSQL) - 使用 Factory 避免并发问题
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// 3. 业务服务
builder.Services.AddScoped<GeminiService>();       // AI 聊天
builder.Services.AddScoped<AuthService>();          // 用户认证
builder.Services.AddScoped<ConversationService>();  // 对话持久化
builder.Services.AddSingleton<MarkdownService>();   // Markdown 渲染

// 4. Blazor 服务
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// 5. 初始化数据库
try
{
    var dbFactory = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
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

// 7. 配置 Blazor 路由
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

Console.WriteLine("Gemini Chat 已启动 - http://localhost:5000");

app.Run();

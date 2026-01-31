using VertexAI.Components;
using VertexAI.Services;

// --------------------------------------------------------------------------------------
// 项目名称: Gemini Chat - Blazor Web App
// 描述: 使用 Google.GenAI SDK 调用 Gemini，可视化展示"思考过程"的聊天界面。
// --------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// 1. 配置 Gemini 服务
builder.Services.Configure<GeminiSettings>(
    builder.Configuration.GetSection("VertexAI"));

// 使用 Scoped 生命周期，每个用户会话一个实例
builder.Services.AddScoped<GeminiService>();

// 2. 添加 Blazor 服务
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// 3. 配置中间件
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

// 4. 配置 Blazor 路由
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

Console.WriteLine("Gemini Chat 已启动 - http://localhost:5000");

app.Run();
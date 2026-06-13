using Serilog;
using Serilog.Events;
using VertexAI.Configuration;

DotNetEnv.Env.Load();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/antigravity-studio-.json",
        rollingInterval: RollingInterval.Day,
        formatter: new Serilog.Formatting.Compact.CompactJsonFormatter())
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.Services.AddVertexApplication(builder.Configuration);

    var app = builder.Build();

    await app.InitializeVertexApplicationAsync();
    app.UseVertexPipeline();

    Log.Information("球球布丁工作室 started");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup failed");
}
finally
{
    Log.CloseAndFlush();
}

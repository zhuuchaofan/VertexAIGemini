using Serilog;
using Serilog.Events;
using VertexAI.Configuration;

var loggerConfiguration = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}");

Log.Logger = loggerConfiguration.CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    ConfigureCloudRunPort(builder);
    builder.Services.AddVertexApplication(builder.Configuration);

    var app = builder.Build();

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

static void ConfigureCloudRunPort(WebApplicationBuilder builder)
{
    var port = Environment.GetEnvironmentVariable("PORT");
    if (!string.IsNullOrWhiteSpace(port)
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    {
        builder.WebHost.UseUrls($"http://+:{port}");
    }
}

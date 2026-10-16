using System.Text.Json;
using Google.GenAI.Types;
using Microsoft.Extensions.Options;
using VertexAI.Services;
using VertexAI.Services.Auth;

internal static class GeminiSdkCompatibilityTests
{
    public static void AllowsInternalTestingOverrides()
    {
        var user = new AuthenticatedUser(Guid.NewGuid(), "tester", "tester@example.test");
        var policy = new GeminiSafetyPolicy(Options.Create(new GeminiSettings
        {
            Safety = new GeminiSafetySettings { AllowUserOverrides = true, AdminCanDisable = false }
        }));
        string[] expected = ["OFF", "BLOCK_NONE", "BLOCK_ONLY_HIGH",
            "BLOCK_MEDIUM_AND_ABOVE", "BLOCK_LOW_AND_ABOVE"];
        Assert.SequenceEqual(expected, policy.GetAllowedThresholds(user));
        foreach (var threshold in expected)
            Assert.True(policy.CreateSafetySettings(user, threshold)
                .All(setting => setting.Threshold.ToString() == threshold));
        Assert.False(policy.GetAllowedThresholds(null).Contains("OFF"));
        Assert.False(new GeminiSafetyPolicy(Options.Create(new GeminiSettings()))
            .GetAllowedThresholds(user).Contains("OFF"));
        var status = ResultTestHelpers.Execute(VertexAI.Api.AuthEndpoints.GetStatusAsync(
            new Microsoft.AspNetCore.Http.DefaultHttpContext(), new FakeUserContext(user), policy));
        Assert.Contains("\"OFF\"", status.Body);
        Assert.Contains("\"BLOCK_NONE\"", status.Body);
        // Exposing the controls does not silently change the server default.
        Assert.True(policy.CreateSafetySettings(user).All(
            setting => setting.Threshold == HarmBlockThreshold.BlockMediumAndAbove));
    }

    public static void SerializesWireValues()
    {
        var admin = new AuthenticatedUser(Guid.NewGuid(), "admin", "admin@example.test", IsAdmin: true);
        var policy = new GeminiSafetyPolicy(Options.Create(new GeminiSettings()));
        foreach (var threshold in policy.GetAllowedThresholds(admin))
        {
            var settings = policy.CreateSafetySettings(admin, threshold.ToLowerInvariant());
            Assert.True(settings.All(setting => setting.Threshold.ToString() == threshold));
            Assert.False(settings.Any(setting => setting.Category == HarmCategory.HarmCategoryCivicIntegrity));
            Assert.Contains($"\"{threshold}\"", JsonSerializer.Serialize(settings));
        }
        var fallback = new GeminiSafetyPolicy(Options.Create(new GeminiSettings
        {
            Safety = new GeminiSafetySettings { DefaultThreshold = "invalid-value" }
        }));
        Assert.True(fallback.CreateSafetySettings(null).All(
            setting => setting.Threshold == HarmBlockThreshold.BlockMediumAndAbove));
        Assert.Contains("\"MEDIUM\"", JsonSerializer.Serialize(
            new ThinkingConfig { ThinkingLevel = ThinkingLevel.Medium }));
    }

    public static void KeepsRequiredThinkingEnabled()
    {
        foreach (var model in GeminiCatalog.CreateModelOptions(new GeminiSettings()))
        {
            Assert.Equal(model.ModelName == "gemini-4-argon" ? "Gemini 4 Argon" : "Gemini 3.8 Flash", model.Name);
            var config = GeminiService.BuildConfig("", model.Thinking,
                thinkingEnabled: false, thinkingLevel: "medium", thinkingBudget: 0, safetySettings: []);
            Assert.NotNull(config.ThinkingConfig);
            Assert.Null(config.ThinkingConfig!.ThinkingBudget);
            Assert.Equal(ThinkingLevel.Medium, config.ThinkingConfig.ThinkingLevel);
        }
    }

    public static void ClassifiesFinishReasons()
    {
        foreach (var reason in new[] { FinishReason.Safety, FinishReason.Blocklist,
            FinishReason.ProhibitedContent, FinishReason.Spii, FinishReason.ImageSafety,
            FinishReason.ImageProhibitedContent })
            Assert.True(GeminiService.IsBlockedFinishReason(reason));
        Assert.False(GeminiService.IsBlockedFinishReason(FinishReason.Stop));
        Assert.False(GeminiService.IsBlockedFinishReason(null));
    }
}

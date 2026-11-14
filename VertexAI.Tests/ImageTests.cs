using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google.GenAI.Types;
using VertexAI.Services;
using VertexAI.Services.Chat;
using VertexAI.Services.Runs;

/// <summary>Pictures from the picture model: kept only when final, stored next to the reply, served back, counted.</summary>
internal static class ImageTests
{
    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    // ------------------------------------------------------------------------- the Gemini answer, part by part (no emulator)

    public static void OnlyFinalPicturesAreKept()
    {
        var png = ScriptedPicture.Bytes;
        var picture = GeminiService.FinalImage(new Part { InlineData = new Blob { Data = png, MimeType = "image/png" } }, 1)!;
        Assert.Equal("image/png", picture.MimeType);
        Assert.Equal("image-1.png", picture.FileName!);
        Assert.Equal((long)png.Length, picture.SizeBytes!.Value);
        Assert.Equal(Convert.ToBase64String(png), picture.Base64Data!);

        // A draft inside the model's thinking, a non-image blob, an empty blob and plain text are not pictures.
        Assert.Null(GeminiService.FinalImage(new Part { Thought = true, InlineData = new Blob { Data = png, MimeType = "image/png" } }, 1));
        Assert.Null(GeminiService.FinalImage(new Part { InlineData = new Blob { Data = png, MimeType = "audio/wav" } }, 1));
        Assert.Null(GeminiService.FinalImage(new Part { InlineData = new Blob { Data = [], MimeType = "image/png" } }, 1));
        Assert.Null(GeminiService.FinalImage(new Part { Text = "hello" }, 1));
    }

    // ------------------------------------------------------------------------- the whole run (Firestore emulator)

    public static void APictureIsStoredWithTheReplyServedBackAndCounted() => Wait(Async1());
    private static async Task Async1()
    {
        var h = new RunHarness();
        using var host = new ApiTestHost(h, h.User);
        var conversation = Guid.NewGuid();
        var runId = RunHarness.NewRunId();

        var response = await host.Http.PostAsJsonAsync("/api/v2/runs", new
        {
            runId, conversationId = conversation, message = "/image a cat", providerId = "scripted", modelName = "scripted-image", searchMode = "auto"
        });
        await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RunStatus.Completed, (await h.Store.GetAsync(h.User, runId, default))!.Status);

        // The reply keeps its words and records the picture as stored; the bytes are in the bucket, not in Firestore.
        var reply = (await h.MessagesAsync(conversation)).Single(m => (string)m["role"] == "model");
        Assert.Equal("Here is your cat.", (string)reply["content"]);
        var stored = ChatAttachmentSerializer.Deserialize((string)reply["attachmentsJson"]);
        Assert.Equal(1, stored.Count);
        Assert.Null(stored[0].Base64Data);
        Assert.Equal(AttachmentStates.Stored, stored[0].State!);
        Assert.True(h.Attachments.Objects.ContainsKey(stored[0].StorageObjectName!));

        // The conversation lists it on the reply, and the attachment route serves the same bytes.
        var detail = JsonDocument.Parse(await host.Http.GetStringAsync($"/api/v2/conversations/{conversation}")).RootElement;
        var model = detail.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "model");
        var view = model.GetProperty("attachments")[0];
        Assert.Equal("image/png", view.GetProperty("mimeType").GetString());
        Assert.Equal("stored", view.GetProperty("state").GetString());
        var bytes = await host.Http.GetByteArrayAsync($"/api/v2/conversations/{conversation}/messages/{model.GetProperty("id").GetString()}/attachments/0");
        Assert.SequenceEqual(ScriptedPicture.Bytes, bytes);

        // One picture from the daily allowance, and no search although the search mode was "auto".
        Assert.Equal(1L, await h.UsageAsync("images"));
        Assert.Equal(0L, await h.UsageAsync("searches"));
    }

    public static void APictureWithoutWordsStillMakesAReply() => Wait(Async2());
    private static async Task Async2()
    {
        var h = new RunHarness();
        var conversation = Guid.NewGuid();
        var outcome = await h.SendAsync("/image-only", conversationId: conversation, modelName: "scripted-image");
        Assert.Equal(RunStatus.Completed, outcome.Final!.Status);
        var reply = (await h.MessagesAsync(conversation)).Single(m => (string)m["role"] == "model");
        Assert.Equal("🎨 图片画好了。", (string)reply["content"]);
        Assert.Equal(1, ChatAttachmentSerializer.Deserialize((string)reply["attachmentsJson"]).Count);
    }

    public static void APictureThatCannotBeStoredIsLeftOutAndSaidSo() => Wait(Async3());
    private static async Task Async3()
    {
        var h = new RunHarness();
        h.Attachments.FailWrite = _ => true;
        var conversation = Guid.NewGuid();
        var outcome = await h.SendAsync("/image a cat", conversationId: conversation, modelName: "scripted-image");
        Assert.Equal(RunStatus.Completed, outcome.Final!.Status);
        var reply = (await h.MessagesAsync(conversation)).Single(m => (string)m["role"] == "model");
        Assert.Contains("保存失败", (string)reply["content"]);
        Assert.False(reply.ContainsKey("attachmentsJson") && reply["attachmentsJson"] is string json && ChatAttachmentSerializer.Deserialize(json).Count > 0);
    }

    public static void TheDailyPictureAllowanceIsEnforcedAndChatIsNotCounted() => Wait(Async4());
    private static async Task Async4()
    {
        var h = new RunHarness(dailyImageLimit: 1);
        Assert.Equal(RunStatus.Completed, (await h.SendAsync("/image one", modelName: "scripted-image")).Final!.Status);

        // A chat reply uses no picture, so it is still allowed.
        Assert.Equal(RunStatus.Completed, (await h.SendAsync("/ok chat")).Final!.Status);
        Assert.Equal(1L, await h.UsageAsync("images"));

        var refused = await h.SendAsync("/image two", modelName: "scripted-image");
        var rejected = (AdmitResult.Rejected)refused.Result;
        Assert.Equal(RunReasons.Quota, rejected.Run.StatusReason!);
        Assert.Equal("今日画图次数已用完。", rejected.Run.Error!.Message);
        Assert.Equal(2, h.Probe.StreamCalls); // the model was not called for the refused picture
    }
}

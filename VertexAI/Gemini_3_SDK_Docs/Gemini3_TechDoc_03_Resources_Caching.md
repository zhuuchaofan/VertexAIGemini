# Gemini 3.0 SDK 实战指南 - 03: 资源与缓存 (Resources & Caching)

**版本**: 3.0 (2026-02)
**目标**: 管理大文件上传 (Files) 及降低长上下文成本 (Context Caching)。

---

## 1. 文件管理 (Files API)

适用于视频、音频、PDF 等大文件。**不能**直接把 1GB 的视频转成 Base64 塞进 Prompt，必须使用 Files API。

### 1.1 上传并等待处理 (标准流程)

**关键点**：文件上传后不能立即使用，必须轮询状态直到 `ACTIVE`。

```csharp
using Google.GenAI;
using Google.GenAI.Files;

using var client = new Client();

// 1. 上传文件
Console.WriteLine("开始上传视频...");
var uploadResult = await client.Files.UploadAsync(
    filePath: "my_vacation.mp4",
    mimeType: "video/mp4" // 可选，SDK 会自动尝试推断
);

string fileUri = uploadResult.Uri;
Console.WriteLine($"上传成功: {fileUri}");

// 2. 轮询等待处理完成 (这是必不可少的一步！)
Console.Write("正在处理...");
while (true)
{
    var fileState = await client.Files.GetAsync(uploadResult.Name);
    
    if (fileState.State == State.ACTIVE)
    {
        Console.WriteLine("\n文件处理完毕，可以使用！");
        break;
    }
    else if (fileState.State == State.FAILED)
    {
        throw new Exception("文件处理失败");
    }

    Console.Write(".");
    await Task.Delay(2000); // 等待 2 秒重试
}

// 3. 在 Prompt 中使用
var response = await client.Models.GenerateContentAsync(
    modelId: "gemini-3-flash",
    contents: new List<Content>
    {
        new Content 
        {
            Parts = new List<Part>
            {
                new Part { Text = "这个视频里的人在做什么？" },
                new Part { FileData = new FileData { FileUri = fileUri, MimeType = "video/mp4" } }
            }
        }
    }
);
Console.WriteLine(response.Text);
```

### 1.2 删除文件
文件默认会在 48 小时后自动删除。但为了良好的资源管理：

```csharp
await client.Files.DeleteAsync(uploadResult.Name);
```

---

## 2. 上下文缓存 (Context Caching)

当你需要对同一份长文档（如《红楼梦》全书，或巨大的 API 文档）进行反复提问时，使用缓存。

**计费警告**: 缓存存储是按分钟计费的。

### 2.1 创建缓存

```csharp
using Google.GenAI.Caches;

// 假设我们有一个巨大的文本内容 (或多个 FileData)
var hugeContent = new List<Content> 
{
    new Content { Parts = new List<Part> { new Part { Text = "...(这里有 100万字)..." } } }
};

var cacheConfig = new CachedContent
{
    Model = "models/gemini-3-flash-001", // 必须明确指定版本号
    Contents = hugeContent,
    Ttl = "3600s" // 存活时间：1小时
};

var cachedContent = await client.Caches.CreateAsync(cacheConfig);
Console.WriteLine($"缓存已创建: {cachedContent.Name}"); // e.g., "cachedContents/123456"
```

### 2.2 使用缓存进行推理

**注意**: 使用缓存时，必须**只**传递 `CachedContent` 引用，而不要再重复传递原文。

```csharp
var response = await client.Models.GenerateContentAsync(
    modelId: "models/gemini-3-flash-001", // 模型必须匹配
    fromCache: cachedContent.Name,          // <--- 引用缓存 ID
    prompt: "总结一下这段内容的主要冲突点。"
);
```

### 2.3 更新缓存寿命 (TTL)

如果用户还在持续对话，需要给缓存“续命”。

```csharp
var updatedCache = await client.Caches.UpdateAsync(
    name: cachedContent.Name,
    cachedContent: new CachedContent { Ttl = "7200s" } // 延长到 2 小时
);
```

---

## 3. 成本估算

通过 `UsageMetadata` 检查缓存命中情况。

```csharp
var metadata = response.UsageMetadata;

Console.WriteLine($"总 Token: {metadata.TotalTokenCount}");
Console.WriteLine($"缓存命中 Token (便宜): {metadata.CachedContentTokenCount}");
Console.WriteLine($"新输入 Token (全价): {metadata.PromptTokenCount}");
```

*   **CachedContentTokenCount > 0** 表示缓存生效。这部分的计费费率通常只有输入的 1/4 或更低。

---

## 4. 常见问题

1.  **缓存能跨模型使用吗？**
    *   **不能**。`gemini-3-flash` 的缓存不能给 `gemini-3-pro` 用。创建时必须指定精确的模型名。
2.  **最短 TTL 是多少？**
    *   通常为 5 分钟。如果你的任务能在 1 分钟内跑完且不再复用，不要用缓存，直接传 Context 可能更便宜（省去创建开销）。
3.  **支持哪些内容？**
    *   Text, Images, Audio, Video, PDF 均可。凡是可以放进 `GenerateContent` 的都可以放进缓存。
# Gemini 3.0 SDK 实战指南 - 05: 高级配置、安全与多模态元数据

**版本**: 3.0 (2026-02)
**目标**: 掌握安全过滤、视频元数据处理以及基于 URI 的远程资源调用。

---

## 1. 安全设置 (Safety Settings)

Gemini 3 默认开启了严格的内容过滤。如果您在处理医学研究、犯罪小说创作等场景时触发了“安全拦截”，需要调整 `SafetySettings`。

### 1.1 核心属性
*   **`Category`**: 过滤类别（仇恨言论、骚扰、色情、暴力）。
*   **`Threshold`**: 阈值。常用的有：
    *   `BLOCK_NONE`: 不拦截（需特殊权限或企业版）。
    *   `BLOCK_ONLY_HIGH`: 仅拦截极高风险。
    *   `BLOCK_LOW_AND_ABOVE`: 拦截所有低风险及以上。

### 1.2 代码示例
```csharp
var config = new GenerateContentConfig
{
    SafetySettings = new List<SafetySetting>
    {
        new SafetySetting 
        { 
            Category = SafetyCategory.HARM_CATEGORY_HARASSMENT, 
            Threshold = SafetyThreshold.BLOCK_ONLY_HIGH 
        },
        new SafetySetting 
        { 
            Category = SafetyCategory.HARM_CATEGORY_HATE_SPEECH, 
            Threshold = SafetyThreshold.BLOCK_NONE 
        }
    }
};
```

---

## 2. 视频输入与元数据 (Video Metadata)

在 Gemini 3 中，当你输入视频时，可以通过 `VideoMetadata` 告诉模型你应该关注视频的哪个片段，从而提高识别精度并节省计算量。

### 2.1 指定视频切片 (Start/End Offset)
```csharp
var videoPart = new Part
{
    FileData = new FileData 
    { 
        FileUri = "https://storage.googleapis.com/.../video.mp4",
        MimeType = "video/mp4" 
    },
    VideoMetadata = new VideoMetadata
    {
        // 仅处理视频中第 10 秒到第 25 秒的内容
        StartOffset = new Duration { Seconds = 10, Nanos = 0 },
        EndOffset = new Duration { Seconds = 25, Nanos = 0 }
    }
};
```

---

## 3. 远程资源引用 (URIs vs InlineData)

SDK 支持两种数据输入方式：

### 3.1 `InlineData` (Base64 模式)
适用于小图片（< 4MB）。直接将字节流发给 API。
```csharp
var imagePart = new Part
{
    InlineData = new Blob
    {
        MimeType = "image/jpeg",
        Data = Convert.ToBase64String(System.IO.File.ReadAllBytes("image.jpg"))
    }
};
```

### 3.2 `FileData` (URI 模式)
**推荐模式**。适用于大文件。必须是已经上传到 `Files API` 或 `Google Cloud Storage (GCS)` 的资源。
```csharp
var remotePart = new Part
{
    FileData = new FileData 
    { 
        FileUri = "gs://my-bucket/large-document.pdf", 
        MimeType = "application/pdf" 
    }
};
```

---

## 4. 系统指令 (System Instructions)

这是定义 AI “性格”或“专业领域”的最佳方式，比在 Prompt 里写“你是一个助手”更有效且更节省 Token（因为它在多轮对话中被视为固定前缀）。

```csharp
var config = new GenerateContentConfig
{
    SystemInstruction = new Content
    {
        Parts = new List<Part> 
        { 
            new Part { Text = "你是一位拥有 20 年经验的 .NET 架构师。回答必须包含代码示例。" } 
        }
    }
};

// 在初始化对话或单次请求中使用
var response = await client.Models.GenerateContentAsync(
    modelId: "gemini-3-pro-preview",
    prompt: "解释一下 ValueTask 的内存优化原理。",
    config: config
);
```

---

## 5. 响应反馈与元数据 (Response Metadata)

当你拿到响应后，除了 `response.Text`，还有很多重要数据：

### 5.1 检查拦截原因
如果 `response.Text` 为空，请检查 `FinishReason`。
```csharp
var candidate = response.Candidates[0];
if (candidate.FinishReason == FinishReason.SAFETY)
{
    Console.WriteLine("由于安全原因，输出被拦截。");
    // 查看是哪个类别导致的拦截
    foreach (var rating in candidate.SafetyRatings)
    {
        if (rating.Blocked) 
            Console.WriteLine($"原因: {rating.Category} (风险等级: {rating.Probability})");
    }
}
```

### 5.2 引用溯源 (Grounding Metadata)
Gemini 3 具备强大的搜索与联网能力。如果模型查阅了外部资料，会返回 `GroundingMetadata`。
```csharp
if (response.GroundingMetadata != null)
{
    foreach (var chunk in response.GroundingMetadata.GroundingChunks)
    {
        // 打印 AI 参考的网页链接或引用来源
        Console.WriteLine($"参考来源: {chunk.Web?.Uri} - {chunk.Web?.Title}");
    }
}
```

---

## 6. 参数汇总表 (Advanced Part Properties)

| 属性名 | 说明 | 适用 Part 类型 |
| :--- | :--- | :--- |
| **`Text`** | 纯文本 Prompt | Text |
| **`InlineData`** | Base64 编码的媒体数据 | Image/Audio/Video (Small) |
| **`FileData`** | 远程资源 URI (Files API/GCS) | Any Media (Large) |
| **`VideoMetadata`** | 控制视频采样范围 (Offset) | Video |
| **`FunctionCall`** | 模型请求调用函数 | Tool Interaction |
| **`FunctionResponse`**| 客户端回传函数执行结果 | Tool Interaction |
| **`Thought`** | (Gemini 3 新增) 指示该 Part 是否为推理过程 | Reasoning |

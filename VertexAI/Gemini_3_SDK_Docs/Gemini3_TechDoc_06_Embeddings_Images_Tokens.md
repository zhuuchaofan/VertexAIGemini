# Gemini 3.0 SDK 实战指南 - 06: 嵌入、生图与实用工具

**版本**: 3.0 (2026-02)
**目标**: 掌握 RAG 核心技术 (Embeddings)、图像生成 (Imagen) 及 Token 计数工具。

---

## 1. 文本嵌入 (Embeddings)

构建 RAG (检索增强生成) 应用的第一步。将文本转化为向量，以便进行语义搜索。

### 1.1 单条文本嵌入
```csharp
using Google.GenAI;
using Google.GenAI.Types;

// 使用专门的 Embedding 模型 (Gemini 3 时代常用 text-embedding-004)
string embeddingModel = "models/text-embedding-004";

var response = await client.Models.EmbedContentAsync(
    model: embeddingModel,
    content: new Content { Parts = { new Part { Text = "机器学习是人工智能的一个子集。" } } }
);

// 获取向量数据 (float 数组)
float[] vector = response.Embedding.Values.ToArray();
Console.WriteLine($"向量维度: {vector.Length}"); // 通常为 768 或 1536
```

### 1.2 批量嵌入 (Batch Embeddings)
为了提高效率，通常批量处理文档段落。

```csharp
var batchContent = new List<Content>
{
    new Content { Parts = { new Part { Text = "第一章：简介" } } },
    new Content { Parts = { new Part { Text = "第二章：安装" } } },
    new Content { Parts = { new Part { Text = "第三章：配置" } } }
};

// BatchEmbedContents 在某些 SDK 版本中可能通过 Models.EmbedContentAsync 重载或专用 Batch 方法提供
// 这里展示标准调用方式
foreach (var content in batchContent)
{
    // 在实际生产中，建议使用并行 Task 控制并发度
    var res = await client.Models.EmbedContentAsync(embeddingModel, content);
    // 存入向量数据库...
}
```

### 1.3 降维配置
如果向量数据库对维度有要求，可以请求模型输出更小的维度（截断）。

```csharp
var config = new EmbedContentConfig
{
    OutputDimensionality = 256 // 强制输出 256 维向量
};
```

---

## 2. 图像生成 (Image Generation)

Gemini SDK 集成了 Imagen 3 的能力，支持高质量文生图。

### 2.1 生成图片
```csharp
var imageConfig = new GenerateImagesConfig
{
    NumberOfImages = 1,
    AspectRatio = "16:9",
    OutputMimeType = "image/png",
    SafetyFilterLevel = SafetyFilterLevel.BLOCK_ONLY_HIGH
};

try 
{
    // 注意：生图通常使用 imagen-3.0-generate-001 等专用模型 ID
    var imageResponse = await client.Models.GenerateImagesAsync(
        model: "models/imagen-3.0-generate-001", 
        prompt: "一只赛博朋克风格的猫，霓虹灯背景，高细节",
        config: imageConfig
    );

    // 保存图片
    if (imageResponse.GeneratedImages.Count > 0)
    {
        byte[] imageBytes = Convert.FromBase64String(imageResponse.GeneratedImages[0].Image.ImageBytes);
        await File.WriteAllBytesAsync("cyber_cat.png", imageBytes);
        Console.WriteLine("图片已保存！");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"生成失败: {ex.Message}");
}
```

---

## 3. Token 计数 (Token Counting)

在发送请求前预估成本，或确保不超出上下文窗口限制。

### 3.1 计算 Prompt Token
```csharp
var content = new Content 
{ 
    Parts = { new Part { Text = File.ReadAllText("huge_novel.txt") } } 
};

var countResponse = await client.Models.CountTokensAsync(
    model: "gemini-3-pro-preview",
    contents: new List<Content> { content }
);

Console.WriteLine($"预计消耗 Token: {countResponse.TotalTokens}");
```

---

## 4. Microsoft.Extensions.AI 集成

Google.GenAI SDK 实现了 .NET 官方的 AI 抽象接口 `IChatClient`。这意味着你可以将 Gemini 无缝集成到 Semantic Kernel 或其他标准 .NET AI 管道中。

### 4.1 依赖注入 (DI)
```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder();

// 将 Gemini 客户端注册为标准 IChatClient
builder.Services.AddChatClient(new Client().AsIChatClient("gemini-3-flash-preview"));

var app = builder.Build();

// 在服务中使用
var chatClient = app.Services.GetRequiredService<IChatClient>();
var response = await chatClient.CompleteAsync("你好，.NET AI！");
Console.WriteLine(response.Message.Text);
```

---

## 5. 核心模型 ID 速查表 (补充)

| 功能 | 推荐模型 ID | 说明 |
| :--- | :--- | :--- |
| **文本嵌入** | `models/text-embedding-004` | 当前最强文本嵌入模型 |
| **图像生成** | `models/imagen-3.0-generate-001` | Imagen 3 生图模型 |
| **多模态嵌入** | `models/multimodal-embedding-001` | 支持视频/图片的嵌入 (如果可用) |

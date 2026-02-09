---
name: gemini-sdk-expert
description: "当涉及 Gemini API 调用、SDK 配置、流式响应、Thinking 控制或安全设置时，自动激活此专家技能。触发关键词：Gemini、GenerateContent、ThinkingConfig、SafetySettings、流式响应。"
version: "1.1.0"
priority: MEDIUM
triggers:
  - pattern: "**/Services/GeminiService.cs"
  - pattern: "**/Services/ChatHistoryManager.cs"
  - keywords: ["GenerateContentAsync", "ThinkingConfig", "StreamChatAsync"]
---

# Gemini 3 SDK 使用专家

当你被要求编写或调试 Gemini API 相关代码时，使用此技能中的知识。

---

## 1. Model ID 速查表 (2026-02)

| 场景              | 推荐 Model ID                 | 说明           |
| ----------------- | ----------------------------- | -------------- |
| **标准/旗舰**     | `gemini-3-pro-preview`        | 最强推理能力   |
| **快速/低成本**   | `gemini-3-flash-preview`      | 速度优先       |
| **实时流 (Live)** | `gemini-3-flash-live-preview` | WebSocket 场景 |

---

## 2. 核心代码模式

### 2.1 流式响应（推荐模式）

```csharp
var responseStream = client.Models.GenerateContentStreamAsync(
    modelId: "gemini-3-flash-preview",
    prompt: userMessage
);

await foreach (var chunk in responseStream)
{
    if (!string.IsNullOrEmpty(chunk.Text))
    {
        // 实时输出到 UI
        yield return chunk.Text;
    }
}
```

### 2.2 Thinking 控制

```csharp
// ✅ 启用推理（复杂任务）
var config = new GenerateContentConfig
{
    ThinkingConfig = new ThinkingConfig
    {
        ThinkingBudget = 2048,      // >= 1024
        IncludeThoughts = true       // 返回思考过程
    }
};

// ✅ 禁用推理（简单聊天）
var fastConfig = new GenerateContentConfig
{
    ThinkingConfig = new ThinkingConfig
    {
        ThinkingBudget = 0,          // 0 = 禁用
        IncludeThoughts = false
    }
};
```

### 2.3 安全设置

```csharp
var config = new GenerateContentConfig
{
    SafetySettings = new List<SafetySetting>
    {
        new SafetySetting
        {
            Category = SafetyCategory.HARM_CATEGORY_HARASSMENT,
            Threshold = SafetyThreshold.BLOCK_ONLY_HIGH  // 仅拦截高风险
        }
    }
};
```

### 2.4 系统指令（System Instruction）

```csharp
var config = new GenerateContentConfig
{
    SystemInstruction = new Content
    {
        Parts = new List<Part>
        {
            new Part { Text = "你是一位 .NET 架构师，回答必须包含代码示例。" }
        }
    }
};
```

### 2.5 JSON 结构化输出

```csharp
var config = new GenerateContentConfig
{
    ResponseMimeType = "application/json",
    ResponseSchema = new Schema
    {
        Type = Type.Object,
        Properties = new Dictionary<string, Schema>
        {
            ["name"] = new Schema { Type = Type.String },
            ["score"] = new Schema { Type = Type.Number }
        },
        Required = new List<string> { "name" }
    }
};
```

---

## 3. 常见配置参数

| 参数              | 类型   | 推荐值                  | 说明         |
| ----------------- | ------ | ----------------------- | ------------ |
| `Temperature`     | double | 0.2 (代码) / 1.0 (创意) | 控制随机性   |
| `MaxOutputTokens` | int    | 8192                    | 限制输出长度 |
| `TopP`            | double | 0.95                    | 核采样概率   |

---

## 4. 错误处理

```csharp
try
{
    var response = await client.Models.GenerateContentAsync(...);

    // 检查安全拦截
    var candidate = response.Candidates[0];
    if (candidate.FinishReason == FinishReason.SAFETY)
    {
        foreach (var rating in candidate.SafetyRatings)
        {
            if (rating.Blocked)
                Console.WriteLine($"被 {rating.Category} 拦截");
        }
    }
}
catch (GoogleGenAIException ex)
{
    Console.WriteLine($"API 错误: {ex.Message}");
}
```

---

## 5. 参考文档

详细文档位于 `VertexAI/Gemini_3_SDK_Docs/` 目录：

- `Gemini3_TechDoc_01_Core_Models.md` - 核心生成与推理
- `Gemini3_TechDoc_02_Live_Realtime.md` - 实时会话
- `Gemini3_TechDoc_03_Resources_Caching.md` - 上下文缓存
- `Gemini3_TechDoc_04_Batching_Tuning.md` - 批处理与微调
- `Gemini3_TechDoc_05_Advanced_Config.md` - 高级配置
- `Gemini3_TechDoc_06_Embeddings_Images_Tokens.md` - Embeddings 与图像

---

## 6. Rate Limiting 与重试策略

### 判定逻辑

```
如果发生 429 Too Many Requests → 使用指数退避重试
如果发生 503 Service Unavailable → 等待后重试
如果重试超过 3 次 → 优雅降级或报错
```

### 代码模式

```csharp
// ✅ 推荐：使用 Polly 实现重试
using Polly;
using Polly.Retry;

var retryPolicy = Policy
    .Handle<HttpRequestException>()
    .Or<GoogleGenAIException>(ex => ex.Message.Contains("429"))
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
        onRetry: (ex, delay, attempt, _) =>
        {
            _logger.LogWarning("API 限流，{Attempt}/3 次重试，等待 {Delay}s", attempt, delay.TotalSeconds);
        }
    );

var response = await retryPolicy.ExecuteAsync(() =>
    client.Models.GenerateContentAsync(modelId, prompt, config)
);
```

---

## 7. 常见错误 Few-shot

```csharp
// ❌ 错误：忘记判空
var text = response.Text;  // 可能为 null

// ✅ 正确：安全访问
var text = response.Text ?? "";
if (string.IsNullOrEmpty(text))
{
    // 检查 FinishReason
}

// ❌ 错误：流式响应未处理空 chunk
await foreach (var chunk in stream)
{
    Console.Write(chunk.Text);  // Text 可能为 null
}

// ✅ 正确：判空后输出
await foreach (var chunk in stream)
{
    if (!string.IsNullOrEmpty(chunk.Text))
        Console.Write(chunk.Text);
}
```

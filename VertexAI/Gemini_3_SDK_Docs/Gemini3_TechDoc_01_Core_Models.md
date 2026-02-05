# Gemini 3.0 SDK 实战指南 - 01: 核心生成与推理 (Models)

**版本**: 3.0 (2026-02)
**目标**: 掌握文本生成、推理控制 (Thinking)、JSON 结构化输出及工具调用。

---

## 1. 基础环境搭建

所有操作均基于 `Google.GenAI.Client`。

```csharp
using Google.GenAI;
using Google.GenAI.Types;

// 1. 初始化客户端 (建议单例模式)
// 方式 A: 从环境变量 GOOGLE_API_KEY 读取
using var client = new Client(); 

// 方式 B: 显式传递 API Key
// using var client = new Client(new ClientConfig { ApiKey = "YOUR_API_KEY" });

// 2. 选择模型 (使用 2026 年 2 月的主流 Model ID)
string modelId = "gemini-3-pro-preview"; // 别名，指向最新的 Pro 预览版
// string modelId = "gemini-3-flash-preview"; // 别名，指向最新的 Flash 预览版
```

---

## 2. 文本生成与流式响应

### 2.1 简单问答 (非流式)
适用于短回复，代码最简单。

```csharp
try 
{
    var response = await client.Models.GenerateContentAsync(
        modelId: modelId,
        prompt: "用一句话解释量子纠缠。"
    );
    
    Console.WriteLine(response.Text);
}
catch (GoogleGenAIException ex)
{
    Console.WriteLine($"API 错误: {ex.Message}");
}
```

### 2.2 高性能流式响应 (推荐)
对于长文本，流式响应能显著降低用户的感知延迟 (TTFT)。

```csharp
var responseStream = client.Models.GenerateContentStreamAsync(
    modelId: modelId,
    prompt: "写一个关于 2026 年火星殖民的科幻短篇，800字。"
);

Console.Write("AI: ");
await foreach (var chunk in responseStream)
{
    // chunk.Text 可能为空（例如只包含 usage metadata 时），需判空
    if (!string.IsNullOrEmpty(chunk.Text))
    {
        Console.Write(chunk.Text);
    }
}
Console.WriteLine();
```

---

## 3. 深度推理 (Thinking / Reasoning) 控制

Gemini 3 的 Thinking 模型默认会输出推理过程。你可以完全掌控这一行为。

### 3.1 场景：复杂数学/逻辑题 (启用推理)
需要查看 AI 是如何一步步思考的。

```csharp
var config = new GenerateContentConfig
{
    ThinkingConfig = new ThinkingConfig
    {
        ThinkingBudget = 2048, // 设定思考预算 Token 数 (>= 1024)
        IncludeThoughts = true // 关键：要求返回思考内容
    }
};

var response = await client.Models.GenerateContentAsync(
    modelId: "gemini-3-pro-preview",
    prompt: "证明根号2是无理数。",
    config: config
);

// 解析响应
foreach (var part in response.Candidates[0].Content.Parts)
{
    if (part.Thought == true) // 假设 SDK 封装了 IsThought 属性，或检查 part.TextReasoningContent
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"[思考中]: {part.Text}");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"[回答]: {part.Text}");
    }
}
```

### 3.2 场景：快速聊天 (禁用推理)
**重点**：即便使用 Thinking 模型，也可以强制它“闭嘴快答”，节省 Token 和时间。

```csharp
var fastConfig = new GenerateContentConfig
{
    ThinkingConfig = new ThinkingConfig
    {
        ThinkingBudget = 0,      // <--- 0 = 彻底禁用推理
        IncludeThoughts = false
    }
};

var response = await client.Models.GenerateContentAsync(
    modelId: "gemini-3-pro-preview",
    prompt: "你好，最近怎么样？", // 简单寒暄不需要推理
    config: fastConfig
);
```

---

## 4. 结构化输出 (JSON Mode)

强制模型输出符合特定 Schema 的 JSON 数据，无需后续 Regex 解析。

### 4.1 定义 Schema
使用 `ResponseSchema` 定义结构。

```csharp
var config = new GenerateContentConfig
{
    ResponseMimeType = "application/json",
    ResponseSchema = new Schema
    {
        Type = Type.Object,
        Properties = new Dictionary<string, Schema>
        {
            ["product_name"] = new Schema { Type = Type.String },
            ["price"] = new Schema { Type = Type.Number },
            ["in_stock"] = new Schema { Type = Type.Boolean },
            ["tags"] = new Schema 
            { 
                Type = Type.Array, 
                Items = new Schema { Type = Type.String } 
            }
        },
        Required = new List<string> { "product_name", "price" }
    }
};

var response = await client.Models.GenerateContentAsync(
    modelId: "gemini-3-flash-preview",
    prompt: "从这段描述中提取商品信息：'我们新推出的 RTX 6090 显卡售价 1999 美元，现货充足，适合游戏和 AI 训练。'",
    config: config
);

// response.Text 将是纯净的 JSON 字符串
// {"product_name": "RTX 6090", "price": 1999, "in_stock": true, "tags": ["游戏", "AI 训练"]}
Console.WriteLine(response.Text);
```

---

## 5. 工具调用 (Function Calling) 全流程

让 AI 调用本地函数（如查询数据库、控制智能家居）。

### 步骤 1: 定义工具
```csharp
// 定义一个简单的加法工具
var addTool = new Tool
{
    FunctionDeclarations = new List<FunctionDeclaration>
    {
        new FunctionDeclaration
        {
            Name = "AddNumbers",
            Description = "计算两个数字的和",
            Parameters = new Schema
            {
                Type = Type.Object,
                Properties = new Dictionary<string, Schema>
                {
                    ["a"] = new Schema { Type = Type.Number },
                    ["b"] = new Schema { Type = Type.Number }
                },
                Required = new List<string> { "a", "b" }
            }
        }
    }
};
```

### 步骤 2: 发送请求并处理回调
这是一个多轮交互过程：
1. 用户提问。
2. 模型返回“请调用函数 X”。
3. 客户端执行函数 X。
4. 客户端将结果传回给模型。
5. 模型生成最终回答。

```csharp
var chat = client.GenerativeModel.StartChat(new StartChatConfig { Tools = new List<Tool> { addTool } });

// 1. 用户提问
var response1 = await chat.SendMessageAsync("55 加 102 等于多少？");

// 2. 检查是否有函数调用请求
var functionCall = response1.FunctionCalls?.FirstOrDefault();

if (functionCall != null)
{
    Console.WriteLine($"模型请求调用函数: {functionCall.Name}");
    
    // 3. 执行本地逻辑 (简单示例)
    if (functionCall.Name == "AddNumbers")
    {
        var args = functionCall.Args;
        double a = Convert.ToDouble(args["a"]);
        double b = Convert.ToDouble(args["b"]);
        double result = a + b;

        // 4. 将结果传回给模型
        var functionResponse = new Part 
        {
            FunctionResponse = new FunctionResponse
            {
                Name = "AddNumbers",
                Response = new { result = result } // 匿名对象序列化
            }
        };

        var finalResponse = await chat.SendMessageAsync(new List<Part> { functionResponse });
        
        // 5. 最终回答
        Console.WriteLine($"最终回答: {finalResponse.Text}"); // "55 加 102 等于 157。"
    }
}
```

---

## 6. 常见配置参数表 (`GenerateContentConfig`)

| 属性 | 类型 | 默认值 | 说明 |
| :--- | :--- | :--- | :--- |
| `Temperature` | `double?` | 模型相关 | 0.0 最稳定，2.0 最随机。代码生成推荐 0.2，创意写作推荐 1.0+。 |
| `MaxOutputTokens` | `int?` | - | 限制生成长度。Gemini 3 Pro 最大支持 8192 或更多。 |
| `TopP` | `double?` | 0.95 | 核采样概率。 |
| `ThinkingConfig` | `ThinkingConfig` | null | **重要**。用于控制 CoT 行为。 |
| `SafetySettings` | `List<SafetySetting>` | 默认过滤 | 设置 `BlockNone` 可关闭安全过滤（需申请权限）。 |
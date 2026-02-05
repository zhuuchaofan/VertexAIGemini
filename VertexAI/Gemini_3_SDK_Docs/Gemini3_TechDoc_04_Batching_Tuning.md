# Gemini 3.0 SDK 实战指南 - 04: 批处理与微调 (Batching & Tuning)

**版本**: 3.0 (2026-02)
**目标**: 处理大规模离线任务 (Batch) 及定制私有模型 (Tuning)。

---

## 1. 异步批处理 (Batch API)

当你需要处理 10,000 条数据且对实时性要求不高（允许 24 小时内返回）时，Batch API 可节省 50% 成本。

### 1.1 准备数据 (JSONL 格式)

Batch API 要求严格的 JSONL (JSON Lines) 格式。每一行是一个独立的 `GenerateContentRequest`。

**示例文件 `requests.jsonl` 内容**:
```json
{"request": {"contents": [{"parts": [{"text": "把这句话翻译成法语: Hello world"}]}]}}
{"request": {"contents": [{"parts": [{"text": "写一首关于春天的诗"}]}]}}
```

### 1.2 提交作业流程

```csharp
using Google.GenAI;
using Google.GenAI.Batches;

using var client = new Client();

// 1. 上传输入文件 (必须使用 Files API)
var inputFile = await client.Files.UploadAsync("requests.jsonl", "application/json");
// 等待文件变为 ACTIVE (参考文档 03)

// 2. 创建 Batch 任务
var batchJob = await client.Batches.CreateAsync(new Batch
{
    Source = new BatchSource { FileUri = inputFile.Uri },
    Model = "models/gemini-3-flash",
    DisplayName = "MyTranslationJob_001"
});

Console.WriteLine($"任务已提交: {batchJob.Name}"); // e.g. "batchJobs/123456"

// 3. 轮询等待完成
while (true)
{
    var jobState = await client.Batches.GetAsync(batchJob.Name);
    Console.WriteLine($"当前状态: {jobState.State}");

    if (jobState.State == BatchState.SUCCEEDED)
    {
        Console.WriteLine($"任务完成! 输出文件: {jobState.OutputFileUri}");
        
        // 4. 下载结果 (注意：OutputFileUri 是一个云端地址)
        // SDK 目前可能需要通过 HttpApiClient 下载，或直接通过 Files.GetContent 接口(如果支持)
        // 通常是一个 JSONL 文件，包含每一行的响应
        break;
    }
    else if (jobState.State == BatchState.FAILED)
    {
        Console.WriteLine($"失败原因: {jobState.Error.Message}");
        break;
    }
    
    await Task.Delay(30000); // 每 30 秒查一次
}
```

---

## 2. 模型微调 (Model Tuning)

### 2.1 适用场景
*   **适用**: 学习特定的输出格式（如特殊的医疗报告格式）、学习特定的语气（如模仿鲁迅风格）。
*   **不适用**: 灌输新知识（如“教”模型公司上周的财报数据）。知识类任务请用 **RAG (Retrieval)**。

### 2.2 提交微调任务

```csharp
using Google.GenAI.Tunings;

// 1. 准备训练数据 (格式通常为 CSV 或 JSONL，包含 text_input, text_output)
var trainingFile = await client.Files.UploadAsync("dataset.jsonl");

// 2. 配置超参数
var tuningTask = await client.Tunings.CreateTunedModelAsync(new TunedModel
{
    BaseModel = "models/gemini-3-flash-001",
    DisplayName = "Flash-Medical-Expert",
    TuningTask = new TuningTask
    {
        TrainingData = new TuningData { FileUri = trainingFile.Uri },
        Hyperparameters = new Hyperparameters
        {
            EpochCount = 5,
            BatchSize = 4,
            LearningRate = 0.001
        }
    }
});

// 3. 等待训练完成 (可能需要几小时)
// 轮询逻辑同上
```

### 2.3 使用微调后的模型

微调完成后，你会获得一个新的模型 ID。

```csharp
var response = await client.Models.GenerateContentAsync(
    modelId: "tunedModels/flash-medical-expert-xyz123", // <--- 使用你的私有模型 ID
    prompt: "病人主诉头痛..."
);
```

---

## 3. 关键差异表

| 特性 | Batch API | Tuning API |
| :--- | :--- | :--- |
| **目的** | 批量生成结果 | 创建新模型 |
| **产物** | 文本/JSON 文件 | 一个新的 Model ID |
| **成本** | 便宜 (50% off) | 较贵 (训练费 + 托管费) |
| **耗时** | 几分钟 ~ 24小时 | 几小时 ~ 几天 |
| **输入** | Prompt 列表 | (Input, Output) 对 |
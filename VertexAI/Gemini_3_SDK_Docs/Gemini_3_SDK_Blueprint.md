# Gemini 3.0 .NET SDK 核心架构大纲 (2026-02 版)

这份文档是 Gemini 3 时代 SDK 的核心导航。通过这套文档，开发者可以从零开始构建生产级的 AI 应用。

---

## 1. 文档结构
您可以在 `Gemini_3_SDK_Docs/` 目录下找到以下详细指南：

1.  📄 **[Gemini3_TechDoc_01_Core_Models.md](./Gemini3_TechDoc_01_Core_Models.md)**
    *   核心文本生成、流式响应、JSON 模式。
    *   **深度推理 (Thinking)**：如何控制、限制或禁用推理预算。
    *   **工具调用 (Function Calling)**：完整的闭环交互逻辑。

2.  📄 **[Gemini3_TechDoc_02_Live_Realtime.md](./Gemini3_TechDoc_02_Live_Realtime.md)**
    *   WebSocket 实时会话管理。
    *   实时音视频流输入、打断处理、低延迟语音回复。

3.  📄 **[Gemini3_TechDoc_03_Resources_Caching.md](./Gemini3_TechDoc_03_Resources_Caching.md)**
    *   千万级上下文缓存 (Context Caching) 的创建与维护。
    *   大文件 (Files API) 的上传与异步状态轮询。

4.  📄 **[Gemini3_TechDoc_04_Batching_Tuning.md](./Gemini3_TechDoc_04_Batching_Tuning.md)**
    *   异步批处理 (Batch API) 降低 50% 成本。
    *   模型微调 (SFT/Tuning) 定制私有模型。

5.  📄 **[Gemini3_TechDoc_05_Advanced_Config.md](./Gemini3_TechDoc_05_Advanced_Config.md)**
    *   **安全设置 (Safety)**：精细化控制内容过滤阈值。
    *   **视频元数据**：控制视频分析的时间窗口。
    *   **系统指令**：定义 AI 的永久人设与行为准则。
    *   **引用溯源**：处理 Grounding Metadata 联网搜索来源。

6.  📄 **[Gemini3_TechDoc_06_Embeddings_Images_Tokens.md](./Gemini3_TechDoc_06_Embeddings_Images_Tokens.md)**
    *   **Embeddings**：构建 RAG 应用的向量生成。
    *   **Imagen**：使用 `GenerateImagesAsync` 进行文生图。
    *   **.NET AI 集成**：使用 `Microsoft.Extensions.AI` 标准接口。

---

## 2. 2026 年核心 Model ID 参考
在代码调用中，请优先使用以下经 2026.02 测试通过的 ID：

| 功能 | 推荐 Model ID |
| :--- | :--- |
| **标准/旗舰** | `gemini-3-pro-preview` |
| **快速/低成本** | `gemini-3-flash-preview` |
| **实时流 (Live)** | `gemini-3-flash-live-preview` |
| **推理增强** | `gemini-3-pro-preview` (内置推理) |

---
**提示**：所有代码示例均已针对 .NET 10+ 和最新版本的 `Google.GenAI` SDK 进行了优化。
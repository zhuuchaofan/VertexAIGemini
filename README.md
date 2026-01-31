# Vertex AI Gemini .NET 练手项目

这是一个基于 Google `Google.GenAI` SDK 构建的简易终端聊天程序，旨在学习和测试 Vertex AI 的 Gemini 模型。

## 🚀 快速开始

### 1. 环境准备
- 安装 .NET 9.0 或更高版本 SDK。
- 拥有一个 Google Cloud 项目并启用了 Vertex AI API。
- 下载服务账号 JSON 密钥文件。

### 2. 配置
编辑 `VertexAI/appsettings.json` 文件，填入你的配置：
```json
{
  "VertexAI": {
    "ProjectId": "copper-affinity-467409-k7",
    "Location": "global",
    "ModelName": "gemini-3-flash-preview",
    "CredentialsPath": "你的JSON密钥绝对路径"
  }
}
```

### 3. 运行
在项目根目录下执行：
```bash
dotnet run --project VertexAI/VertexAI.csproj
```

## 📂 目录结构说明
- `VertexAI/`
  - `Program.cs`: 主程序入口，包含聊天循环逻辑。
  - `appsettings.json`: 配置文件（不建议上传到 Git）。
  - `Properties/launchSettings.json`: 调试环境配置。
  - `VertexAI.csproj`: 项目依赖管理。

## 🛠 核心功能
- [x] 基于 Vertex AI 的流式对话 (Streaming)
- [x] 自动读取本地配置
- [x] 维护对话上下文（多轮对话）
- [ ] 支持图片输入 (Multimodal) - *待实现*
- [ ] 函数调用 (Function Calling) - *待实现*

## 📝 学习笔记
- **环境变量**: SDK 默认查找 `GOOGLE_APPLICATION_CREDENTIALS`。
- **模型选择**: Vertex AI 模式下，模型名称通常选择 `gemini-3-flash-preview` 等。
- **流式处理**: 使用 `await foreach` 处理 `GenerateContentStreamAsync`。

---
name: project-architecture
description: "当需要理解项目结构、定位文件、新增功能或查看配置时，自动激活此技能。触发关键词：目录结构、在哪里、配置、部署、新增功能。"
version: "1.1.0"
priority: LOW
triggers:
  - pattern: "Program.cs"
  - pattern: "appsettings*.json"
  - keywords: ["目录", "结构", "在哪", "配置文件"]
---

# VertexAI Gemini Chat 项目架构导航

当你需要了解项目结构或定位文件时，使用此技能。

---

## 1. 项目概述

**技术栈**: Blazor Server (.NET 10) + Vertex AI Gemini API  
**核心功能**: 流式 AI 聊天、思考过程可视化、对话历史管理

---

## 2. 目录结构

```
VertexAI/
├── Components/
│   ├── App.razor              # 根组件
│   ├── Routes.razor           # 路由配置
│   ├── Layout/
│   │   └── MainLayout.razor   # 主布局
│   ├── Pages/
│   │   ├── Chat.razor         # 💬 聊天页面（主要 UI）
│   │   └── Login.razor        # 登录页面
│   └── Chat/
│       ├── ChatHeader.razor   # 聊天头部
│       ├── ChatInput.razor    # 输入框组件
│       └── MessageBubble.razor # 消息气泡
├── Services/
│   ├── GeminiService.cs       # 🤖 Gemini API 封装
│   ├── ChatHistoryManager.cs  # 对话历史管理
│   ├── ConversationService.cs # 会话持久化
│   └── AuthService.cs         # 用户认证
├── Data/
│   ├── Entities/              # 数据库实体
│   └── AppDbContext.cs        # EF Core 上下文
├── wwwroot/
│   └── css/                   # 样式文件
├── Program.cs                 # 应用入口
├── Dockerfile                 # Docker 构建
├── run-docker.sh              # 一键部署脚本
└── appsettings.json           # 配置文件
```

---

## 3. 核心组件职责

| 组件/文件                | 职责                 | 修改场景         |
| ------------------------ | -------------------- | ---------------- |
| `Chat.razor`             | 聊天 UI 主页面       | 添加 UI 功能     |
| `GeminiService.cs`       | Gemini API 调用封装  | 修改 AI 行为     |
| `ChatHistoryManager.cs`  | Token 计数、滑动窗口 | 调整历史管理策略 |
| `ConversationService.cs` | 会话 CRUD、持久化    | 数据库相关修改   |

---

## 4. 配置项速查

配置文件位于 `appsettings.json`：

| 配置项             | 默认值                   | 说明              |
| ------------------ | ------------------------ | ----------------- |
| `ProjectId`        | -                        | GCP 项目 ID       |
| `Location`         | `global`                 | Vertex AI 区域    |
| `ModelName`        | `gemini-3-flash-preview` | 使用的模型        |
| `MaxHistoryTokens` | `100000`                 | 最大历史 Token 数 |
| `MaxHistoryRounds` | `20`                     | 最大对话轮数      |
| `SummaryThreshold` | `80000`                  | 触发摘要的阈值    |

---

## 5. 本地开发命令

```bash
# 设置环境变量
export GOOGLE_APPLICATION_CREDENTIALS=/path/to/key.json

# 运行开发服务器
cd VertexAI
ASPNETCORE_ENVIRONMENT=Development dotnet run --urls "http://localhost:5000"
```

---

## 6. Docker 部署

```bash
# 一键构建并运行
./run-docker.sh

# 自定义配置
GCP_KEY_PATH=/your/key.json \
PROJECT_ID=your-project \
SYSTEM_PROMPT="自定义提示词" \
./run-docker.sh
```

---

## 7. 添加新功能指南

### 新增 Service

1. 在 `Services/` 创建 `XxxService.cs`
2. 定义接口 `IXxxService`
3. 在 `Program.cs` 注册 DI
4. 在 Razor 组件中 `@inject`

### 新增页面

1. 在 `Components/Pages/` 创建 `Xxx.razor`
2. 添加 `@page "/xxx"` 路由
3. 使用 `@rendermode InteractiveServer`

### 新增数据库实体

1. 在 `Data/Entities/` 创建实体类
2. 在 `AppDbContext.cs` 添加 `DbSet<T>`
3. 运行 `dotnet ef migrations add XxxMigration`

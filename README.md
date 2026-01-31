# Gemini Chat - Blazor Web App

基于 Vertex AI Gemini 的 Blazor Web 聊天应用，支持思考过程可视化和对话历史管理。

## 功能

- 流式 AI 回复
- 思考过程可折叠显示
- **系统提示词预设选择**（5种内置人设 + 自定义）
- **对话历史管理**（Token 计数、滑动窗口、自动摘要）
- Docker 一键部署

## 系统提示词预设

| 预设     | 说明             |
| -------- | ---------------- |
| 默认助手 | 通用对话助手     |
| 陕西老哥 | 暴躁陕西方言     |
| 编程专家 | 技术问答专家     |
| 翻译官   | 中英互译         |
| 文案写手 | 创意写作         |
| 自定义   | 输入自定义提示词 |

## 对话历史管理

- **Token 计数**：实时显示当前对话 Token 使用量
- **滑动窗口**：自动保留最近 20 轮对话
- **自动摘要**：超出阈值时自动压缩历史，保持上下文

## 架构

```mermaid
graph TB
    subgraph Browser["浏览器"]
        UI["Chat.razor<br>聊天界面"]
    end

    subgraph Server["Blazor Server"]
        SignalR["SignalR<br>实时通信"]
        Service["GeminiService<br>AI 服务"]
    end

    subgraph Cloud["Google Cloud"]
        VertexAI["Vertex AI<br>Gemini API"]
    end

    UI <-->|WebSocket| SignalR
    SignalR --> Service
    Service -->|Streaming| VertexAI
```

**数据流**：用户输入 → SignalR → GeminiService → Vertex AI → 流式返回 → UI 实时更新

## 本地开发

```bash
# 设置环境变量
export GOOGLE_APPLICATION_CREDENTIALS=/path/to/key.json

# 运行
cd VertexAI
ASPNETCORE_ENVIRONMENT=Development dotnet run --urls "http://localhost:5000"
```

访问 http://localhost:5000

## Docker 部署

```bash
cd VertexAI

# 一键构建并运行（默认）
./run-docker.sh

# 跳过构建，直接运行
./run-docker.sh -s
```

访问 http://localhost:8880

### 自定义配置

```bash
GCP_KEY_PATH=/your/key.json \
PROJECT_ID=your-project \
SYSTEM_PROMPT="自定义提示词" \
./run-docker.sh
```

## 配置说明

| 配置项           | 说明              | 默认值                 |
| ---------------- | ----------------- | ---------------------- |
| ProjectId        | GCP 项目 ID       | -                      |
| Location         | Vertex AI 区域    | global                 |
| ModelName        | 模型名称          | gemini-3-flash-preview |
| SystemPrompt     | 系统提示词        | -                      |
| MaxHistoryTokens | 最大历史 Token 数 | 100000                 |
| MaxHistoryRounds | 最大对话轮数      | 20                     |
| SummaryThreshold | 触发摘要阈值      | 80000                  |

配置文件：`appsettings.json` / `appsettings.Production.json`

## 目录结构

```
VertexAI/
├── Components/
│   ├── App.razor          # 根组件
│   ├── Routes.razor       # 路由配置
│   └── Pages/
│       └── Chat.razor     # 聊天页面
├── Services/
│   └── GeminiService.cs   # AI 服务封装（含历史管理）
├── Program.cs             # 应用入口
├── Dockerfile             # Docker 构建
├── run-docker.sh          # 一键部署脚本
└── appsettings.json       # 配置文件
```

## 发展路线

### Phase 1: 用户系统

- 用户注册/登录 (JWT)
- 对话历史持久化 (PostgreSQL)
- 每用户独立会话

### Phase 2: 多租户

- 组织/团队管理
- 角色权限控制 (Admin/User)
- API 配额管理

### Phase 3: 增强功能

- 多模态输入 (图片/文件)
- ~~自定义 AI 人设~~ ✅ 已实现
- 对话分享/导出
- RAG 知识库接入

### Phase 4: 企业级

- SSO 集成
- 审计日志
- 多区域部署
- 监控告警

```mermaid
graph LR
    subgraph Phase1["Phase 1"]
        Auth["用户认证"]
        History["对话历史"]
    end

    subgraph Phase2["Phase 2"]
        Org["组织管理"]
        RBAC["权限控制"]
    end

    subgraph Phase3["Phase 3"]
        Multi["多模态"]
        RAG["知识库"]
    end

    subgraph Phase4["Phase 4"]
        SSO["SSO"]
        Monitor["监控"]
    end

    Phase1 --> Phase2 --> Phase3 --> Phase4
```

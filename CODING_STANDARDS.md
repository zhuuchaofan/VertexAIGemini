# 代码规范 - Coding Standards

本文档定义了 VertexAI Gemini Chat 项目的代码规范，面向企业级标准。

---

## 1. 文件规模限制

| 文件类型      | 最大行数   | 说明                         |
| ------------- | ---------- | ---------------------------- |
| `.cs` 服务类  | **300 行** | 超出应拆分为多个职责单一的类 |
| `.cs` 模型类  | **150 行** | 包含 DTO、Entity、配置类     |
| `.razor` 页面 | **400 行** | 超出应提取子组件             |
| `.razor` 组件 | **200 行** | 可复用 UI 组件               |

> **原则**: 每个文件只做一件事。看到超限立即重构。

---

## 2. 命名规范

### C# 代码

| 类型     | 规范              | 示例                 |
| -------- | ----------------- | -------------------- |
| 类名     | PascalCase        | `GeminiService`      |
| 接口     | I + PascalCase    | `IGeminiService`     |
| 公共方法 | PascalCase + 动词 | `StreamChatAsync()`  |
| 私有字段 | \_camelCase       | `_chatHistory`       |
| 公共属性 | PascalCase        | `CurrentTokenCount`  |
| 常量     | PascalCase        | `MaxRetryCount`      |
| 异步方法 | 后缀 `Async`      | `SendMessageAsync()` |

### Blazor 组件

| 类型     | 规范        | 示例                    |
| -------- | ----------- | ----------------------- |
| 页面组件 | 名词 + Page | `ChatPage.razor`        |
| UI 组件  | 功能描述    | `MessageBubble.razor`   |
| 参数     | PascalCase  | `[Parameter] IsLoading` |
| 私有状态 | \_camelCase | `_messages`             |

---

## 3. 代码结构

### 服务类模板

```csharp
namespace VertexAI.Services;

/// <summary>
/// 服务描述（必须有 XML 注释）
/// </summary>
public class XxxService : IAsyncDisposable
{
    // 1. 私有字段（readonly 优先）
    private readonly Client _client;
    private readonly List<Item> _items = [];

    // 2. 公共属性
    public int Count => _items.Count;

    // 3. 构造函数
    public XxxService(IOptions<Settings> settings) { }

    // 4. 公共方法
    public async Task DoSomethingAsync() { }

    // 5. 私有方法
    private void Helper() { }

    // 6. IDisposable
    public ValueTask DisposeAsync() { }
}
```

### Razor 组件模板

```razor
@* 1. 指令 *@
@page "/xxx"
@rendermode InteractiveServer
@inject IService Service

@* 2. HTML 模板（尽量简洁） *@
<div class="container">
    @* 内容 *@
</div>

@* 3. CSS（仅组件特有的样式） *@
<style>
    .container { }
</style>

@* 4. C# 代码块 *@
@code {
    // 参数
    [Parameter] public string Title { get; set; } = "";

    // 私有状态
    private bool _isLoading;

    // 生命周期
    protected override async Task OnInitializedAsync() { }

    // 事件处理
    private async Task OnClick() { }

    // 帮助方法（提取到服务更佳）
}
```

---

## 4. 注释规范

| 场景        | 要求                         |
| ----------- | ---------------------------- |
| 公共类/方法 | **必须** XML 注释            |
| 复杂逻辑    | 行内注释解释"为什么"         |
| TODO        | 格式 `// TODO: 描述`         |
| 禁止        | 注释掉的代码（删除！用 Git） |

---

## 5. 架构规范

### 分层依赖

```
Components/Pages  →  Services  →  外部 API
         ↓             ↓
    Components/UI   Models/DTO
```

### 禁止

- ❌ Page 直接调用外部 API
- ❌ Service 持有 UI 状态
- ❌ 硬编码配置值（用 `appsettings.json`）

---

## 6. 安全规范

- 🔒 敏感配置使用环境变量 / Secret Manager
- 🔒 Markdown 渲染前清理 XSS（当前使用 Markdig 需注意）
- 🔒 所有 API 输入必须验证
- 🔒 日志禁止打印敏感信息

---

## 7. Git 提交规范

```
<type>(<scope>): <subject>

类型:
- feat: 新功能
- fix: Bug 修复
- refactor: 重构
- docs: 文档
- style: 格式调整
- chore: 构建/工具

示例:
feat(chat): add markdown rendering support
fix(service): handle empty response gracefully
```

---

_版本: 1.0 | 更新: 2026-02-01_

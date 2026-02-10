---
name: blazor-coding-standard
description: "当编写、重构或审查 C#/Blazor 代码时，自动应用项目代码规范。触发关键词：新建 Service、创建组件、代码审查、重构。"
version: "1.1.0"
priority: MEDIUM
triggers:
  - pattern: "*.cs"
  - pattern: "*.razor"
---

# Blazor/C# 代码规范守卫

当你被要求编写或修改 C#/Blazor 代码时，**必须**遵循以下规范。

---

## 1. 文件规模限制（硬性约束）

| 文件类型       | 最大行数   | 超出处理                   |
| -------------- | ---------- | -------------------------- |
| `.cs` 服务类   | **300 行** | 拆分为多个职责单一的类     |
| `.cs` 模型/DTO | **150 行** | 检查是否包含不应存在的逻辑 |
| `.razor` 页面  | **400 行** | 提取子组件到 `Components/` |
| `.razor` 组件  | **200 行** | 继续拆分为更小的可复用组件 |

> ⚠️ **判定逻辑**：生成代码前，先估算目标文件的最终行数。如果可能超限，主动提出拆分方案。

---

## 2. 命名规范

### C# 代码

```csharp
// ✅ 正确示例
public class GeminiService : IGeminiService
{
    private readonly Client _client;           // 私有字段: _camelCase
    public int CurrentTokenCount { get; }      // 公共属性: PascalCase

    public async Task SendMessageAsync() { }   // 异步方法: 动词 + Async
    private void ValidateInput() { }           // 私有方法: PascalCase
}

// ❌ 错误示例
public class gemini_service                    // 类名不是 PascalCase
{
    private Client client;                     // 缺少 _ 前缀
    public async Task SendMessage() { }        // 异步方法缺少 Async 后缀
}
```

### Blazor 组件

```razor
@* ✅ 正确示例 *@
@page "/chat"
@inject IGeminiService GeminiService

@code {
    [Parameter] public bool IsLoading { get; set; }  // 参数: PascalCase
    private List<Message> _messages = new();         // 私有状态: _camelCase
}
```

---

## 3. 分层架构禁止事项

```
Components/Pages  →  Services  →  外部 API
         ↓             ↓
    Components/UI   Models/DTO
```

| 操作                 | 禁止 ❌                               | 正确做法 ✅             |
| -------------------- | ------------------------------------- | ----------------------- |
| Page 调用外部 API    | `HttpClient.GetAsync()` 在 Razor 中   | 通过 Service 封装       |
| Service 持有 UI 状态 | `public bool IsLoading` 在 Service 中 | UI 状态留在组件内       |
| 硬编码配置           | `var url = "https://..."`             | 使用 `appsettings.json` |
| 注释掉的代码         | `// _client.Send(...)`                | 直接删除，使用 Git 历史 |

---

## 4. 代码结构模板

### Service 类标准结构

```csharp
namespace VertexAI.Services;

/// <summary>
/// [必须] 服务描述（XML 注释）
/// </summary>
public class XxxService : IAsyncDisposable
{
    // 1️⃣ 私有字段（readonly 优先）
    private readonly Client _client;

    // 2️⃣ 公共属性
    public int Count => _items.Count;

    // 3️⃣ 构造函数
    public XxxService(IOptions<Settings> settings) { }

    // 4️⃣ 公共方法
    public async Task DoSomethingAsync() { }

    // 5️⃣ 私有方法
    private void Helper() { }

    // 6️⃣ Dispose
    public ValueTask DisposeAsync() { }
}
```

---

## 5. Git 提交格式

```
<type>(<scope>): <subject>

类型:
- feat: 新功能
- fix: Bug 修复
- refactor: 重构
- docs: 文档
- chore: 构建/工具
```

**要求**:

- Must verify: 提交说明使用中文。
- Must verify: 遵循 Angular 规范。

**示例**: `feat(chat): 新增 Markdown 渲染支持`

---

## 6. 异常处理规范

### 判定逻辑

```
如果发现以下模式 → 要求修改：
  - 空的 catch 块（吞掉异常）
  - catch 后不记录日志
  - 暴露内部错误给用户
```

### Few-shot 示例

```csharp
// ❌ 严重错误：空 catch 块
try { await DoSomethingAsync(); }
catch { }  // 吞掉异常，难以调试

// ❌ 错误：只记录 Message
catch (Exception ex)
{
    _logger.LogError("错误: " + ex.Message);  // 丢失堆栈
}

// ✅ 正确：完整记录异常
catch (Exception ex)
{
    _logger.LogError(ex, "处理 {Action} 时发生错误", actionName);
    throw;  // 或返回错误响应
}

// ✅ 正确：优雅降级
catch (Exception ex)
{
    _logger.LogWarning(ex, "非关键操作失败，使用默认值");
    return defaultValue;
}
```

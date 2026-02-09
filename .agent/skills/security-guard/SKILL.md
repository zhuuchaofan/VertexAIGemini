---
name: security-guard
description: "当涉及密钥管理、用户输入处理、日志记录、Docker 配置或任何安全相关话题时，自动激活此技能。触发关键词：API Key、密码、secret、XSS、注入、安全、敏感数据。"
version: "1.0.0"
priority: HIGH
requires:
  - blazor-coding-standard
triggers:
  - pattern: "*.cs"
    keywords: ["password", "secret", "apikey", "connectionstring"]
  - pattern: "appsettings*.json"
  - pattern: "Dockerfile"
  - pattern: ".env"
---

# 安全规范守卫

当涉及安全相关代码时，**必须**遵循以下规范。安全问题零容忍。

---

## 1. 密钥管理（硬编码检测）

### 判定逻辑

```
如果发现以下模式 → 立即报告并要求修改：
  - 字符串字面量包含 "sk-", "AIza", "ghp_", "xoxb-"
  - 变量名包含 password/secret/key 且值为字符串字面量
  - appsettings.json 中存在非占位符的敏感值
```

### Few-shot 示例

```csharp
// ❌ 严重错误：硬编码 API Key
private readonly string _apiKey = "AIzaSyB...xyz";

// ❌ 错误：配置文件中写死密钥
// appsettings.json
{
  "GeminiApiKey": "AIzaSyB...xyz"  // 绝对禁止
}

// ✅ 正确：使用环境变量
private readonly string _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
    ?? throw new InvalidOperationException("GEMINI_API_KEY not set");

// ✅ 正确：使用 Secret Manager
builder.Configuration.AddUserSecrets<Program>();

// ✅ 正确：占位符 + 环境覆盖
// appsettings.json
{
  "GeminiApiKey": "${GEMINI_API_KEY}"  // 占位符
}
```

---

## 2. XSS 与注入防护

### 判定逻辑

```
如果发现以下模式 → 要求添加清理逻辑：
  - 用户输入直接拼接到 HTML/SQL
  - Markdown 渲染未经过 sanitizer
  - 动态构建 SQL 而非使用参数化查询
```

### Few-shot 示例

```csharp
// ❌ 危险：用户输入直接渲染
@((MarkupString)userInput)  // XSS 漏洞！

// ✅ 安全：使用 sanitizer
@((MarkupString)Markdig.Markdown.ToHtml(userInput, _pipeline))
// 其中 _pipeline 配置了 SanitizeHtml

// ❌ 危险：SQL 注入
$"SELECT * FROM Users WHERE Name = '{userName}'"

// ✅ 安全：参数化查询
db.Users.Where(u => u.Name == userName)
```

---

## 3. 日志脱敏

### 判定逻辑

```
如果日志内容可能包含 → 必须脱敏或移除：
  - 用户密码、Token
  - 信用卡号、身份证号
  - API 响应中的敏感字段
```

### Few-shot 示例

```csharp
// ❌ 危险：记录敏感信息
_logger.LogInformation("用户登录: {Email}, 密码: {Password}", email, password);

// ✅ 安全：脱敏处理
_logger.LogInformation("用户登录: {Email}", email);  // 不记录密码

// ✅ 安全：部分掩码
_logger.LogInformation("Token: {Token}", token[..8] + "***");
```

---

## 4. Docker 安全配置

### 必检项

| 配置项       | 要求      | 说明                          |
| ------------ | --------- | ----------------------------- |
| 非 root 用户 | ✅ 必须   | `USER app`                    |
| 最小化镜像   | ✅ 建议   | 使用 `alpine` 或 `distroless` |
| 密钥挂载     | ✅ 必须   | 使用 `-v` 挂载而非 `COPY`     |
| 端口暴露     | ⚠️ 最小化 | 仅暴露必要端口                |

### Few-shot 示例

```dockerfile
# ❌ 危险：以 root 运行
FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY . /app
ENTRYPOINT ["dotnet", "app.dll"]

# ✅ 安全：非 root 用户
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --chown=app:app . .
USER app
ENTRYPOINT ["dotnet", "app.dll"]
```

---

## 5. 异常处理安全

### 禁止事项

```csharp
// ❌ 危险：暴露内部错误给用户
catch (Exception ex)
{
    return Content($"错误: {ex.Message}\n{ex.StackTrace}");
}

// ✅ 安全：记录内部日志，返回通用消息
catch (Exception ex)
{
    _logger.LogError(ex, "处理请求时发生错误");
    return StatusCode(500, "服务暂时不可用，请稍后重试");
}
```

---

## 6. 安全文件清单

项目中的安全相关文件：

| 文件                 | 用途     | 检查项                   |
| -------------------- | -------- | ------------------------ |
| `.env`               | 环境变量 | 不应提交到 Git           |
| `.gitignore`         | Git 忽略 | 必须包含 `.env`, `*.key` |
| `appsettings.*.json` | 配置     | 不应包含真实密钥         |
| `Dockerfile`         | 容器     | 使用非 root 用户         |
| `GCPKey/`            | GCP 密钥 | 必须在 `.gitignore` 中   |

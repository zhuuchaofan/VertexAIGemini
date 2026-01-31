using Google.GenAI;
using Google.GenAI.Types;

// --------------------------------------------------------------------------------------
// 项目名称: Vertex AI Gemini 3 Pro Client
// 描述: 使用 Google.GenAI SDK 调用 Gemini 3 Pro Preview，并可视化展示 "思考过程"。
// --------------------------------------------------------------------------------------

namespace VertexAI;

public static class Program
{
    // 根据你上传的 PDF，这是目前的最新模型 ID
    private const string DefaultModel = "gemini-3-pro-preview"; 

    public static async Task Main(string[] args)
    {
        // 1. 设置一点氛围感 (Vlog 风格嘛)
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔════════════════════════════════════════════════════╗");
        Console.WriteLine("║        Gemini 3 Pro Preview - Thinking Mode        ║");
        Console.WriteLine("╚════════════════════════════════════════════════════╝");
        Console.ResetColor();

        // 2. 获取环境变量 (如果没有设置，会提示一下)
        var project = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        var location = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION") ?? "us-central1";
        var modelName = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_MODEL_NAME") ?? DefaultModel;

        if (string.IsNullOrEmpty(project))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n[!] 哎呀，找不到 GOOGLE_CLOUD_PROJECT 环境变量。");
            Console.WriteLine("    请在终端运行: set GOOGLE_CLOUD_PROJECT=your-project-id");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"\n📦 Project:  {project}");
        Console.WriteLine($"📍 Location: {location}");
        Console.WriteLine($"🧠 Model:    {modelName}");
        Console.WriteLine("\n正在连接到 Vertex AI... (喝口咖啡稍等一下)");

        // 3. 初始化客户端 (Google.GenAI SDK)
        // 注意: 确保你已经运行了 `gcloud auth application-default login`
        await using var client = new Client(project: project, location: location, vertexAI: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✅ 连接成功！现在我们可以开始聊天了。");
        Console.ResetColor();
        Console.WriteLine("(输入 'exit' 或 'quit' 退出)\n");

        // 4. 维护上下文历史
        var chatHistory = new List<Content>();

        // 配置思考模式
        var config = new GenerateContentConfig
        {
            ThinkingConfig = new ThinkingConfig
            {
                ThinkingLevel = ThinkingLevel.MEDIUM,
                IncludeThoughts = true
            }
        };
        
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("你 (User) > ");
            Console.ResetColor();
            
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.ToLower() is "exit" or "quit") break;

            // 添加用户消息到历史
            chatHistory.Add(new Content
            {
                Role = "user",
                Parts = [new Part { Text = input }]
            });

            try
            {
                Console.WriteLine(); // 空一行，好看点

                // 5. 调用 API (使用 GenerateContentAsync)
                // Gemini 3 是思考模型，它的 "Thoughts" 可能会作为单独的 Part 返回
                var response = await client.Models.GenerateContentAsync(
                    model: modelName,
                    contents: chatHistory,
                    config:config
                );

                if (response.Candidates is { Count: > 0 })
                {
                    var candidate = response.Candidates[0];
                    var contentParts = candidate.Content?.Parts;

                    if (contentParts != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Blue;
                        Console.WriteLine("Gemini (AI) >");
                        Console.ResetColor();

                        foreach (var part in contentParts)
                        {
                            // 🔍 核心逻辑: 区分 "思考" 和 "回答"
                            // SDK 会把思考过程标记为 Thought (如果 API 支持) 或者 Text
                            // Gemini 3 Pro 的思考内容通常会在 response 中以 explicit thought part 出现

                            if (string.IsNullOrEmpty(part.Text)) continue;
                            // 这是一个简单的判定逻辑，具体取决于 SDK 版本对 "Thinking" 字段的映射
                            // 目前预览版 SDK 有时会将思考内容放在 part.Thought (bool) 标记的 Text 中
                            // 或者直接就是一段 Text，但在元数据里标记。
                            // 这里我们假设 SDK 已经能够通过属性区分，或者我们在视觉上做个简单的区分。

                            var isThought = part.Thought == true; 

                            if (isThought)
                            {
                                // 💭 思考过程：用暗灰色显示，模拟大脑内部的低语
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                Console.WriteLine("\n[⚡ 正在思考逻辑...]");
                                Console.WriteLine(part.Text.Trim());
                                Console.WriteLine("[⚡ 思考结束]\n");
                                Console.ResetColor();
                            }
                            else
                            {
                                // 🗣️ 正式回答：用亮白色显示
                                Console.WriteLine(part.Text);
                            }
                        }

                        // 6. 将 AI 的完整回复加入历史，保持上下文连贯
                        // 注意：这里必须把包含 Thought 的完整 Content 加进去
                        // 这样 AI 才知道它之前"想"过什么，避免逻辑断裂
                        if (candidate.Content != null)
                        {
                            chatHistory.Add(candidate.Content);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[!] 出错了: {ex.Message}");
                Console.ResetColor();
            }
            
            Console.WriteLine("\n--------------------------------------------------");
        }

        Console.WriteLine("👋 下次见！加油写代码！");
    }
}
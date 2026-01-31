using System.Text;
using Google.GenAI;
using Google.GenAI.Types;

namespace VertexAI;
public static class ConsoleChat
{
    public static async Task Main()
    {
        Console.WriteLine("正在初始化 Vertex AI 客户端...");
        var project = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        var location = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION");
        var modelName = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_MODEL_NAME");
        if (string.IsNullOrEmpty(project))
        {
            Console.WriteLine("错误: 未设置 GOOGLE_CLOUD_PROJECT 环境变量。");
            return;
        }
        // 1. 初始化客户端 (假设已设置环境变量 GOOGLE_API_KEY)
        // 显式传入 project 和 location，确保使用 Vertex AI
        var client = new Client(project: project, location: location ?? "us-central1", vertexAI: true);

        Console.WriteLine($"客户端已就绪! (Project: {project}, Location: {location})");
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("开始聊天吧！(输入 'exit' 或 'quit' 退出)");
        Console.WriteLine("--------------------------------------------------");

        
        // 2. 创建历史记录列表 (用于保存上下文)
        var chatHistory = new List<Content>();

        Console.WriteLine("--- 开始对话 (输入 'exit' 退出) ---");

        while (true)
        {
            // 3. 获取用户输入
            Console.Write("\nUser: ");
            var userInput = Console.ReadLine();
            if (string.IsNullOrEmpty(userInput)) continue;
            if (userInput.Equals("exit", StringComparison.CurrentCultureIgnoreCase) || userInput.Equals("quit", StringComparison.CurrentCultureIgnoreCase)) break;

            // 4. 将用户消息添加到历史记录
            var userMessage = new Content
            {
                Role = "user",
                Parts = [new Part { Text = userInput}]
            };
            chatHistory.Add(userMessage);
            if (string.IsNullOrEmpty(modelName)) break; // 简单保护
            
            try
            {
                // 5. 调用 API，传入整个历史记录 (chatHistory)
                // 注意：这里使用的是 contents 参数传入列表
                var response = await client.Models.GenerateContentAsync(
                    model: modelName, 
                    contents: chatHistory 
                );
                
                Console.WriteLine("\nGemini:");
                
                var modelReply =  new StringBuilder();
                // 6. 获取模型回复并显示
                if (response.Candidates is { Count: > 0 })
                {
                    // 获取第一个最可能的候选回答
                    var candidate = response.Candidates[0];
    
                    if (candidate.Content?.Parts != null)
                    {
                        foreach (var part in candidate.Content.Parts)
                        {
                            // 检查是否包含文本（也有可能是 Thought 或 FunctionCall）
                            if (!string.IsNullOrEmpty(part.Text))
                            {
                                modelReply.Append(part.Text);
                            }
                        }
                    }
                }
                Console.WriteLine($"Gemini: {modelReply}");

                // 7. 将模型回复也添加到历史记录，保持上下文连贯
                // chatHistory.Add(response.Candidates[]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
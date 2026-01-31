using System.Text;
using Google.GenAI;
using Google.GenAI.Types;

namespace VertexAI;

public static class ConsoleChat
{
    public static async Task Main()
    {
        Console.WriteLine("正在初始化 Vertex AI 客户端...");
        string? project = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        string? location = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION");
        // 确保使用支持 Thinking 的模型，例如 "gemini-2.0-flash-thinking-exp-01-21"
        string? modelName = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_MODEL_NAME"); 

        if (string.IsNullOrEmpty(project))
        {
            Console.WriteLine("错误: 未设置 GOOGLE_CLOUD_PROJECT 环境变量。");
            return;
        }

        var client = new Client(project: project, location: location ?? "us-central1", vertexAI: true);

        Console.WriteLine($"客户端已就绪! (Project: {project}, Location: {location}, Model: {modelName})");
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine("开始聊天吧！(输入 'exit' 退出)");
        Console.WriteLine("--------------------------------------------------");

        var chatHistory = new List<Content>();

        while (true)
        {
            Console.Write("\nUser: ");
            var userInput = Console.ReadLine();
            if (string.IsNullOrEmpty(userInput)) continue;
            if (userInput.ToLower() == "exit" || userInput.ToLower() == "quit") break;

            var userMessage = new Content
            {
                Role = "user",
                Parts = [new Part { Text = userInput }]
            };
            chatHistory.Add(userMessage);

            if (string.IsNullOrEmpty(modelName)) break; // 简单保护

            try
            {
                var response = await client.Models.GenerateContentAsync(
                    model: modelName,
                    contents: chatHistory
                );

                Console.WriteLine("\nGemini:");

                if (response.Candidates is { Count: > 0 })
                {
                    var candidate = response.Candidates[0];

                    // --- [关键修改 1] 显示逻辑 ---
                    if (candidate.Content?.Parts != null)
                    {
                        foreach (var part in candidate.Content.Parts)
                        {
                            if (!string.IsNullOrEmpty(part.Text))
                            {
                                // 检查是否是思考内容
                                if (part.Thought == true)
                                {
                                    Console.ForegroundColor = ConsoleColor.DarkGray; // 用灰色显示思考
                                    Console.WriteLine($"[思考中] {part.Text}");
                                    Console.ResetColor();
                                }
                                else
                                {
                                    // 普通回答
                                    Console.WriteLine(part.Text);
                                }
                            }
                        }
                    }

                    // --- [关键修改 2] 保存历史 ---
                    // 必须把 API 返回的 *完整* Content 对象存入历史
                    // 这样下一轮对话时，Gemini 才知道 "哦，这是我刚才思考和回答的内容"
                    if (candidate.Content != null)
                    {
                        chatHistory.Add(candidate.Content);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
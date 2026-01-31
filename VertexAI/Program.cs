using Google.GenAI;
using Google.GenAI.Types;
using System.Text;

// 1. 初始化客户端
// 会自动读取环境变量: GOOGLE_CLOUD_PROJECT, GOOGLE_CLOUD_LOCATION, GOOGLE_APPLICATION_CREDENTIALS
// 因为我们使用的是 Vertex AI 模式，所以 vertexAI 参数为 true
Console.WriteLine("正在初始化 Vertex AI 客户端...");
string? project = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
string? location = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION");
string? modelName = System.Environment.GetEnvironmentVariable("GOOGLE_CLOUD_MODEL_NAME");

if (string.IsNullOrEmpty(project))
{
    Console.WriteLine("错误: 未设置 GOOGLE_CLOUD_PROJECT 环境变量。");
    return;
}

// 显式传入 project 和 location，确保使用 Vertex AI
var client = new Client(project: project, location: location ?? "us-central1", vertexAI: true);

Console.WriteLine($"客户端已就绪! (Project: {project}, Location: {location})");
Console.WriteLine("--------------------------------------------------");
Console.WriteLine("开始聊天吧！(输入 'exit' 或 'quit' 退出)");
Console.WriteLine("--------------------------------------------------");

// 2. 维护对话历史
var chatHistory = new List<Content>();

// 可选：添加系统指令
// chatHistory.Add(new Content { Role = "system", Parts = new List<Part> { new Part { Text = "你是一个乐于助人的中文 AI 助手。" } } });

while (true)
{
    // 3. 读取用户输入
    Console.Write("\n你: ");
    string? input = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Trim().ToLower() is "exit" or "quit") break;

    // 将用户输入加入历史
    var userContent = new Content
    {
        Role = "user",
        Parts = new List<Part> { new Part { Text = input } }
    };
    chatHistory.Add(userContent);

    Console.Write("AI: ");
    var fullResponseBuilder = new StringBuilder();

    try
    {
        var responseStream = client.Models.GenerateContentStreamAsync(
            model: modelName,
            contents: chatHistory
        );

        await foreach (var chunk in responseStream)
        {
            if (chunk.Candidates != null && chunk.Candidates.Count > 0)
            {
                foreach (var part in chunk.Candidates[0].Content.Parts)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        Console.Write(part.Text);
                        fullResponseBuilder.Append(part.Text);
                    }
                }
            }
        }
        Console.WriteLine(); // 换行

        // 5. 将 AI 回复加入历史，以便下次对话有上下文
        var modelContent = new Content
        {
            Role = "model",
            Parts = new List<Part> { new Part { Text = fullResponseBuilder.ToString() } }
        };
        chatHistory.Add(modelContent);

    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[Error] 调用 API 失败: {ex.Message}");
        // 如果是 404，可能是模型名称不对
        if (ex.Message.Contains("404") || ex.Message.Contains("Not Found"))
        {
             Console.WriteLine("提示: 可能是模型名称不对，或者该区域不支持此模型。");
        }
    }
}

Console.WriteLine("再见!");

# Gemini 3.0 SDK 实战指南 - 02: 实时多模态 (Live Real-time)

**版本**: 3.0 (2026-02)
**目标**: 构建基于 WebSocket 的实时语音/视频对话应用。

---

## 1. 核心概念与架构

Gemini Live API 不同于传统的 Request-Response 模式，它基于 **WebSocket** 双向流。

*   **Session**: 一次连接即为一个会话。
*   **BidiStreaming**: 客户端可以随时说话（打断），模型也可以随时说话。
*   **Modality**: 原生支持 Audio (PCM) 和 Video (Image Frames) 输入。

---

## 2. 完整实战：实时语音对话控制台

以下代码演示了一个最小化的实时语音客户端框架。
*(注：为了运行此代码，你需要配合 NAudio 或类似的库来采集/播放音频，此处重点展示 SDK 调用逻辑)*

### 2.1 初始化与连接

```csharp
using Google.GenAI;
using Google.GenAI.Live;

// 1. 初始化
using var client = new Client();
var liveClient = client.Live;

// 2. 建立连接 (使用 2026 年 2 月最新的实时流模型 ID)
string modelId = "gemini-3-flash-live-preview"; 
await using var session = await liveClient.ConnectAsync(modelId);

Console.WriteLine("已连接到 Gemini Live!");
```

### 2.2 发送配置 (Setup)

连接后的**第一件事**必须是发送配置。否则服务器会断开。

```csharp
var config = new LiveConnectConfig
{
    // 配置生成参数
    GenerationConfig = new GenerationConfig { Temperature = 0.7 },
    
    // 配置语音输出音色
    SpeechConfig = new SpeechConfig 
    { 
        VoiceConfig = new VoiceConfig { PrebuiltVoiceConfig = new PrebuiltVoiceConfig { VoiceName = "Puck" } } 
    },
    
    // 系统指令 (定义角色)
    SystemInstruction = new Content 
    {
        Parts = new List<Part> { new Part { Text = "你是一个像贾维斯一样的AI助手，说话简练。" } }
    }
};

// 发送 Setup 消息
await session.SendAsync(new LiveClientMessage 
{
    Setup = new LiveSetup { Model = modelId, GenerationConfig = config.GenerationConfig, SystemInstruction = config.SystemInstruction }
});
```

### 2.3 双工循环：接收与发送

这是最关键的部分。你需要同时处理“听”和“说”。

```csharp
var cancellationSource = new CancellationTokenSource();

// 任务 A: 接收循环 (Receive Loop)
var receiveTask = Task.Run(async () =>
{
    try 
    {
        await foreach (var response in session.ReceiveAsync(cancellationSource.Token))
        {
            // 1. 处理服务器的文本/音频内容
            if (response.ServerContent != null)
            {
                // 模型可能一边返回文本，一边返回音频数据
                if (response.ServerContent.ModelTurn != null)
                {
                    foreach (var part in response.ServerContent.ModelTurn.Parts)
                    {
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            Console.Write(part.Text); // 打印文本流
                        }
                    }
                }
            }

            // 2. 处理工具调用 (ToolCall)
            if (response.ToolCall != null)
            {
                // ... 执行工具逻辑并回传 ToolResponse ...
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"接收循环断开: {ex.Message}");
    }
});

// 任务 B: 发送循环 (Send Loop) - 模拟从麦克风读取
var sendTask = Task.Run(async () =>
{
    // 假设你有某种方式获取 PCM 音频流 (16kHz, 1ch, 16bit PCM)
    // var audioStream = GetMicrophoneStream(); 
    
    byte[] buffer = new byte[1024]; 
    while (!cancellationSource.Token.IsCancellationRequested)
    {
        // int bytesRead = audioStream.Read(buffer, 0, buffer.Length);
        // if (bytesRead > 0) 
        // {
        //     // 将 PCM 数据转为 Base64 并发送
        //     string base64Audio = Convert.ToBase64String(buffer, 0, bytesRead);
        //     
        //     await session.SendAsync(new LiveClientMessage
        //     {
        //         RealtimeInput = new RealtimeInput
        //         {
        //             MediaChunks = new List<Blob> 
        //             { 
        //                 new Blob { MimeType = "audio/pcm;rate=16000", Data = base64Audio } 
        //             }
        //         }
        //     });
        // }
        await Task.Delay(10); // 模拟
    }
});

// 等待结束
await Task.WhenAny(receiveTask, sendTask);
```

---

## 3. 进阶技巧：处理打断 (Interruption)

在真实对话中，用户可能会打断 AI。

*   **客户端职责**: 当 VAD (语音活动检测) 发现用户开始说话时，客户端应立即停止播放当前的 AI 音频。
*   **发送打断信号**: SDK 并没有专门的 "Interrupt" 消息，只需发送新的 `RealtimeInput` (用户的语音)，服务端接收到新语音后，会自动截断之前的回复并处理新的输入。
*   **接收端处理**: 注意监听 `server_content` 中的 `interrupted` 标志（如果 API 版本支持），或者简单地在收到新的 `model_turn` 时清空本地播放缓冲区。

---

## 4. 视频流输入

你可以像发送音频一样发送图片帧。

```csharp
// 发送单帧图片
await session.SendAsync(new LiveClientMessage
{
    RealtimeInput = new RealtimeInput
    {
        MediaChunks = new List<Blob>
        {
            new Blob 
            { 
                MimeType = "image/jpeg", 
                Data = Convert.ToBase64String(imageBytes) 
            }
        }
    }
});
```
建议帧率：**1 fps - 5 fps**。过高的帧率会浪费带宽且模型处理不过来。Gemini 重点在于理解关键帧的变化。

---

## 5. 常见错误对照表

| 错误现象 | 可能原因 | 解决方案 |
| :--- | :--- | :--- |
| **连接立即断开 (WebSocket Closed)** | 未发送 Setup 消息 | 确保连接后第一条消息是 `LiveSetup`。 |
| **API Error 400** | 音频格式错误 | 检查 MimeType 是否为 `audio/pcm;rate=16000` (或 24000)。 |
| **无声音回复** | 忘记配置语音 | 在 `Setup` 中检查 `SpeechConfig` 是否包含有效的 `VoiceName`。 |
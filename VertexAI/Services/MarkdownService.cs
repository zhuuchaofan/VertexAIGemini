using Markdig;

namespace VertexAI.Services;

/// <summary>
/// Markdown 渲染服务 - 将 Markdown 文本转换为 HTML
/// </summary>
public class MarkdownService
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()  // 启用 GFM 表格、任务列表、自动链接等
            .Build();
    }

    /// <summary>
    /// 将 Markdown 转换为 HTML
    /// </summary>
    public string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;
        
        return Markdown.ToHtml(markdown, _pipeline);
    }
}

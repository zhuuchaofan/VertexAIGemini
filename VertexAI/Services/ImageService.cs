using Microsoft.AspNetCore.Components.Forms;

namespace VertexAI.Services;

/// <summary>
/// 图片处理服务 - 验证、转换和优化上传的图片
/// </summary>
public class ImageService
{
    // 支持的图片格式
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    // 最大文件大小 (4MB)
    private const long MaxFileSizeBytes = 4 * 1024 * 1024;

    /// <summary>
    /// 处理上传的图片文件
    /// </summary>
    public async Task<ImageResult> ProcessImageAsync(IBrowserFile file)
    {
        // 验证 MIME 类型
        if (!AllowedMimeTypes.Contains(file.ContentType))
        {
            return ImageResult.Fail($"不支持的图片格式: {file.ContentType}");
        }

        // 验证文件大小
        if (file.Size > MaxFileSizeBytes)
        {
            return ImageResult.Fail($"图片过大，最大支持 4MB (当前 {file.Size / 1024 / 1024:F1}MB)");
        }

        try
        {
            // 读取文件内容
            using var stream = file.OpenReadStream(MaxFileSizeBytes);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            // 转换为 Base64
            var base64 = Convert.ToBase64String(bytes);

            return ImageResult.Success(base64, file.ContentType, file.Name);
        }
        catch (Exception ex)
        {
            return ImageResult.Fail($"图片处理失败: {ex.Message}");
        }
    }
}

/// <summary>
/// 图片处理结果
/// </summary>
public class ImageResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Base64Data { get; init; }
    public string? MimeType { get; init; }
    public string? FileName { get; init; }

    public static ImageResult Success(string base64, string mimeType, string fileName) => new()
    {
        IsSuccess = true,
        Base64Data = base64,
        MimeType = mimeType,
        FileName = fileName
    };

    public static ImageResult Fail(string error) => new()
    {
        IsSuccess = false,
        ErrorMessage = error
    };
}

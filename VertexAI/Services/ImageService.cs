using Microsoft.AspNetCore.Components.Forms;
using SkiaSharp;

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

    // Magic Number 签名 (文件头)
    private static readonly Dictionary<string, byte[][]> MagicNumbers = new()
    {
        { "image/jpeg", new[] { new byte[] { 0xFF, 0xD8, 0xFF } } },
        { "image/png", new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } } },
        { "image/gif", new[] { new byte[] { 0x47, 0x49, 0x46, 0x38 } } },
        { "image/webp", new[] { new byte[] { 0x52, 0x49, 0x46, 0x46 } } } // RIFF header
    };

    // 最大文件大小 (4MB)
    private const long MaxFileSizeBytes = 4 * 1024 * 1024;

    // 压缩阈值 (2MB)
    private const long CompressionThreshold = 2 * 1024 * 1024;

    // 压缩目标质量
    private const int TargetQuality = 80;

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

            // Magic Number 验证
            if (!ValidateMagicNumber(bytes, file.ContentType))
            {
                return ImageResult.Fail("图片文件格式无效或已损坏");
            }

            // 压缩大图片 (>2MB)
            var finalBytes = bytes;
            var finalMimeType = file.ContentType;

            if (bytes.Length > CompressionThreshold)
            {
                var compressed = CompressImage(bytes);
                if (compressed != null)
                {
                    finalBytes = compressed;
                    finalMimeType = "image/jpeg"; // 压缩后统一为 JPEG
                }
            }

            // 转换为 Base64
            var base64 = Convert.ToBase64String(finalBytes);

            return ImageResult.Success(base64, finalMimeType, file.Name);
        }
        catch (Exception ex)
        {
            return ImageResult.Fail($"图片处理失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 验证文件头 (Magic Number) 防止伪装文件
    /// </summary>
    private static bool ValidateMagicNumber(byte[] data, string mimeType)
    {
        if (data.Length < 8) return false;

        if (!MagicNumbers.TryGetValue(mimeType, out var signatures))
            return false;

        foreach (var signature in signatures)
        {
            if (data.Length >= signature.Length)
            {
                var match = true;
                for (int i = 0; i < signature.Length; i++)
                {
                    if (data[i] != signature[i])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 压缩图片 (使用 SkiaSharp)
    /// </summary>
    private static byte[]? CompressImage(byte[] originalData)
    {
        try
        {
            using var inputStream = new MemoryStream(originalData);
            using var original = SKBitmap.Decode(inputStream);

            if (original == null) return null;

            // 计算缩放比例 (最大边不超过2048)
            const int maxDimension = 2048;
            var scale = 1.0f;
            if (original.Width > maxDimension || original.Height > maxDimension)
            {
                scale = Math.Min(
                    (float)maxDimension / original.Width,
                    (float)maxDimension / original.Height
                );
            }

            // 缩放图片
            var newWidth = (int)(original.Width * scale);
            var newHeight = (int)(original.Height * scale);

            using var resized = original.Resize(new SKImageInfo(newWidth, newHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized == null) return null;

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, TargetQuality);

            return data.ToArray();
        }
        catch
        {
            return null; // 压缩失败返回 null，使用原图
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


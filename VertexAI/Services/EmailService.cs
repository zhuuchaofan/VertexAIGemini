using MimeKit;
using MailKit.Net.Smtp;

namespace VertexAI.Services;

/// <summary>
/// SMTP 配置选项
/// </summary>
public class SmtpSettings
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromName { get; set; } = "Gemini Chat";
    public string BaseUrl { get; set; } = "http://localhost:8880";
}

/// <summary>
/// 邮件服务 - 通过 Gmail SMTP 发送验证邮件和密码重置邮件
/// </summary>
public class EmailService
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<EmailService> _logger;

    public EmailService(SmtpSettings settings, ILogger<EmailService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// 发送邮箱验证邮件
    /// </summary>
    public async Task<bool> SendVerificationEmailAsync(string toEmail, string token)
    {
        var verifyUrl = $"{_settings.BaseUrl}/verify-email?token={Uri.EscapeDataString(token)}";
        var subject = "验证您的邮箱 - Gemini Chat";
        var body = BuildEmailTemplate(
            "验证您的邮箱",
            "感谢您注册 Gemini Chat！请点击下方按钮验证您的邮箱地址。",
            "验证邮箱",
            verifyUrl,
            "此链接将在 24 小时后失效。如果您没有注册过 Gemini Chat，请忽略此邮件。"
        );

        return await SendEmailAsync(toEmail, subject, body);
    }

    /// <summary>
    /// 发送密码重置邮件
    /// </summary>
    public async Task<bool> SendPasswordResetEmailAsync(string toEmail, string token)
    {
        var resetUrl = $"{_settings.BaseUrl}/login?mode=reset&token={Uri.EscapeDataString(token)}";
        var subject = "重置您的密码 - Gemini Chat";
        var body = BuildEmailTemplate(
            "重置您的密码",
            "我们收到了重置您密码的请求。请点击下方按钮设置新密码。",
            "重置密码",
            resetUrl,
            "此链接将在 1 小时后失效。如果您没有请求重置密码，请忽略此邮件。"
        );

        return await SendEmailAsync(toEmail, subject, body);
    }

    /// <summary>
    /// 发送邮件（底层）
    /// </summary>
    private async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        if (string.IsNullOrEmpty(_settings.User) || string.IsNullOrEmpty(_settings.Password))
        {
            _logger.LogWarning("[Email] SMTP 未配置，跳过发送邮件到 {Email}", toEmail);
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.User));
            message.To.Add(new MailboxAddress("", toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = htmlBody
            };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new MailKit.Net.Smtp.SmtpClient();
            // 接受所有 SSL 证书（开发环境便利，生产环境建议配置更严格的验证）
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;

            await client.ConnectAsync(_settings.Host, _settings.Port, MailKit.Security.SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_settings.User, _settings.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("[Email] 邮件已发送到 {Email}", toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Email] 发送邮件到 {Email} 失败", toEmail);
            return false;
        }
    }

    /// <summary>
    /// 构建 HTML 邮件模板
    /// </summary>
    private static string BuildEmailTemplate(
        string title, string message, string buttonText, string buttonUrl, string footer)
    {
        return $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"></head>
        <body style="margin:0;padding:0;background:#f0f9ff;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
          <div style="max-width:480px;margin:40px auto;background:#ffffff;border-radius:16px;overflow:hidden;box-shadow:0 4px 24px rgba(0,0,0,0.08);">
            <div style="background:linear-gradient(135deg,#38bdf8,#34d399);padding:32px 24px;text-align:center;">
              <h1 style="color:#fff;margin:0;font-size:22px;font-weight:700;">{title}</h1>
            </div>
            <div style="padding:32px 24px;">
              <p style="color:#475569;font-size:15px;line-height:1.6;margin:0 0 24px;">{message}</p>
              <div style="text-align:center;margin:24px 0;">
                <a href="{buttonUrl}"
                   style="display:inline-block;padding:12px 32px;background:linear-gradient(135deg,#38bdf8,#34d399);color:#fff;text-decoration:none;border-radius:12px;font-weight:600;font-size:15px;">
                  {buttonText}
                </a>
              </div>
              <p style="color:#94a3b8;font-size:12px;line-height:1.5;margin:24px 0 0;border-top:1px solid #f1f5f9;padding-top:16px;">
                {footer}<br><br>
                如果按钮无法点击，请复制以下链接到浏览器：<br>
                <a href="{buttonUrl}" style="color:#38bdf8;word-break:break-all;">{buttonUrl}</a>
              </p>
            </div>
          </div>
        </body>
        </html>
        """;
    }
}

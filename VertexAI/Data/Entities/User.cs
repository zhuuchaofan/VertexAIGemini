using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VertexAI.Data.Entities;

/// <summary>
/// 用户实体
/// </summary>
[Table("users")]
public class User
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    [Column("email")]
    public string Email { get; set; } = "";

    [Required]
    [Column("password_hash")]
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// 邮箱是否已验证（预留：后期接入邮件服务后启用强制验证）
    /// </summary>
    [Column("email_verified")]
    public bool EmailVerified { get; set; }

    /// <summary>
    /// 邮箱验证 Token（预留：用于验证链接 /verify?token=xxx）
    /// </summary>
    [MaxLength(64)]
    [Column("verification_token")]
    public string? VerificationToken { get; set; }

    /// <summary>
    /// 密码重置 Token（有效期 1 小时）
    /// </summary>
    [MaxLength(64)]
    [Column("password_reset_token")]
    public string? PasswordResetToken { get; set; }

    /// <summary>
    /// 密码重置 Token 过期时间
    /// </summary>
    [Column("password_reset_expires_at")]
    public DateTime? PasswordResetExpiresAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("last_login_at")]
    public DateTime? LastLoginAt { get; set; }
}

namespace VertexAI.Services;

public record AuthRequest(string Email, string Password);
public record AuthResponse(bool Success, string? Error, UserInfo? User = null);
public record UserInfo(Guid Id, string Email, bool EmailVerified = false);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);
public record VerifyEmailRequest(string Token);

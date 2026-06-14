using Microsoft.EntityFrameworkCore;
using Serilog;

namespace VertexAI.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(this IServiceProvider services)
    {
        try
        {
            var dbFactory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();

            await db.Database.EnsureCreatedAsync();
            await ApplyCompatibilityMigrationsAsync(db);

            Log.Information("Database initialized");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Database initialization failed. The app will continue without persisted chat history.");
        }
    }

    private static async Task ApplyCompatibilityMigrationsAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE conversations ADD COLUMN IF NOT EXISTS token_count INTEGER DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE conversations ADD COLUMN IF NOT EXISTS provider_id VARCHAR(50) NOT NULL DEFAULT 'gemini'");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE conversations ADD COLUMN IF NOT EXISTS model_name VARCHAR(100) NOT NULL DEFAULT ''");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE messages ADD COLUMN IF NOT EXISTS attachments_json TEXT");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS ix_conversations_user_id_updated_at ON conversations (user_id, updated_at DESC)");

        await db.Database.ExecuteSqlRawAsync("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'conversations' AND column_name = 'TokenCount'
                ) THEN
                    UPDATE conversations
                    SET token_count = COALESCE(token_count, 0) + COALESCE("TokenCount", 0)
                    WHERE COALESCE(token_count, 0) = 0;

                    ALTER TABLE conversations DROP COLUMN "TokenCount";
                END IF;
            END $$;
            """);

        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS email_verified BOOLEAN DEFAULT FALSE");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS verification_token VARCHAR(64)");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS password_reset_token VARCHAR(64)");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE users ADD COLUMN IF NOT EXISTS password_reset_expires_at TIMESTAMPTZ");
    }
}

using Google.Cloud.Firestore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VertexAI.Services.Health;

public sealed class FirestoreHealthCheck : IHealthCheck
{
    private readonly FirestoreDb _db;

    public FirestoreHealthCheck(FirestoreDb db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.Collection("_health").Limit(1).GetSnapshotAsync(cancellationToken);
            return HealthCheckResult.Healthy("Firestore connection is available.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Firestore health check failed.", ex);
        }
    }
}

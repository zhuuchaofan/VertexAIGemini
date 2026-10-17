namespace VertexAI.Services.Runs;

/// <summary>
/// Run lifecycle settings. The defaults are the production values from the implementation plan; tests shorten them.
/// </summary>
public sealed class RunSettings
{
    /// <summary>The v2 run endpoints answer 404 until this is turned on.</summary>
    public bool Enabled { get; set; }

    /// <summary>A running run whose heartbeat is older than this is considered lost.</summary>
    public int LeaseSeconds { get; set; } = 30;

    public int HeartbeatSeconds { get; set; } = 5;
    public int StopPollMilliseconds { get; set; } = 1000;

    /// <summary>After the stream drops, wait this long for a stop request before calling it a lost connection.</summary>
    public int DisconnectGraceMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Hard ceiling for one generation. The deployment scripts derive it from the smaller of the web and API request
    /// timeouts minus a margin, so a timeout is reported as a timeout and not as a dropped connection.
    /// </summary>
    public int MaxDurationSeconds { get; set; } = 270;

    public int FinalizeTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Longest one admission attempt may wait on storage. Contended transactions can queue behind each other for a long
    /// time; past this the attempt is read back and, if it did not commit, reported as retryable with the same run id.
    /// </summary>
    public int AdmitTimeoutSeconds { get; set; } = 20;

    /// <summary>The newest N completed turns are sent to the model as context.</summary>
    public int MaxHistoryTurns { get; set; } = 10;

    public int MaxCheckpointChars { get; set; } = 200_000;
}

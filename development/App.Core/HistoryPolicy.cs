namespace HunterPdf.Core;

/// <summary>
/// Bounds undo history for one document. MaxSteps is the target number of undo steps (checkpoints after the opened one);
/// MaxBytes is the target total size of referenced snapshot files. The opened, saved and current checkpoints are never
/// pruned, so a document with a large saved checkpoint can stay above the limits until a later save or compaction.
/// </summary>
public sealed record HistoryPolicy
{
    public const int DefaultMaxSteps = 50;
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;
    static volatile HistoryPolicy current = new();

    public HistoryPolicy(int maxSteps = DefaultMaxSteps, long maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1L);
        MaxSteps = maxSteps;
        MaxBytes = maxBytes;
    }

    public int MaxSteps { get; init; }
    public long MaxBytes { get; init; }

    /// <summary>Policy applied to sessions opened or recovered afterwards; the app can set it from preferences.</summary>
    public static HistoryPolicy Default
    {
        get => current;
        set => current = value ?? throw new ArgumentNullException(nameof(value));
    }
}

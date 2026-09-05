namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Provider delivery has finished; local retention or workspace finalization remains retryable.</summary>
internal sealed class ReviewFinalizationException(Exception innerException)
    : InvalidOperationException("Review finalization could not complete.", innerException);

using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>One shared session; acquisition never owns an individual run or slot.</summary>
internal sealed class FakeReviewSessionProvisioner(ReviewRunSession session) : IReviewSessionProvisioner
{
    public int AcquisitionCount { get; private set; }

    public Task<ReviewRunSession> GetOrCreateSharedAsync(CancellationToken ct)
    {
        AcquisitionCount++;
        return Task.FromResult(session);
    }
}

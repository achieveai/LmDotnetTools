using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Host-owned slot pool and the existing host/SDK preparation capabilities.</summary>
internal sealed record ReviewSlotWorkspace(
    IReviewSlotPool Pool,
    IReviewSlotPreparer HostPreparer,
    Func<ReviewRunSession, string, IReviewSlotPreparer> CreateSessionPreparer,
    ISandboxCommandRunner HostRunner,
    ISandboxFileSystem HostFileSystem
);

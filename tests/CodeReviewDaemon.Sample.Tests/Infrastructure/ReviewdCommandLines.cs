extern alias Reviewd;

using System.Globalization;
using Reviewd::Reviewd.Sample;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>Reviewd command lines the scenarios run, on workspace <c>workspace</c> and session <c>session</c>.</summary>
internal static class ReviewdCommandLines
{
    /// <summary><c>review reclaim-slot</c> for one PR's slot, with <paramref name="flags"/> such as <c>--execute</c>.</summary>
    internal static CliOptions ReclaimSlot(string repo, string pr, int slot, string database, params string[] flags) =>
        CliOptions.Parse([
            "review",
            "reclaim-slot",
            "--repo",
            repo,
            "--pr",
            pr,
            "--slot",
            slot.ToString(CultureInfo.InvariantCulture),
            "--db",
            database,
            "--workspace",
            "workspace",
            "--session",
            "session",
            .. flags,
        ]);
}

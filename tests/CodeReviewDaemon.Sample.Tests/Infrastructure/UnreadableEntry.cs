using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Builds the one input that separates "there is nothing at this name" from "I could not look": an entry the
/// daemon can see listed but whose attributes it is denied. Both read as absent to
/// <see cref="FileSystemInfo.Exists"/>, which is exactly why a containment check written around
/// <c>Exists</c> answers "nothing to worry about" for a path it never managed to inspect.
/// <para>
/// Denial is applied to the entry's PARENT, because on Windows reading an entry's attributes is a permission
/// on the directory holding it, not on the entry. <see cref="Dispose"/> lifts the denial again — a temp tree
/// nobody can enumerate is a temp tree nobody can delete either.
/// </para>
/// </summary>
internal sealed class UnreadableEntry : IDisposable
{
    private readonly DirectoryInfo _parent;
    private readonly string? _link;

    private UnreadableEntry(DirectoryInfo parent, string path, string? link = null)
    {
        _parent = parent;
        Path = path;
        _link = link;
    }

    /// <summary>The entry whose attributes cannot be read.</summary>
    public string Path { get; }

    /// <summary>
    /// Whether this machine actually produces an unreadable entry. A deny ACE is refused by nothing in normal
    /// operation, but a process holding SeBackupPrivilege reads straight past one — and a test that silently
    /// got a perfectly readable file would assert the guard's happy path while claiming to cover its blind one.
    /// </summary>
    public static bool Supported { get; } = Probe();

    /// <summary>
    /// Creates the entry under a fresh subdirectory of <paramref name="root"/>, then asserts it really is
    /// unreadable — a setup that quietly produced a readable file would leave the test body proving nothing.
    /// </summary>
    public static UnreadableEntry Create(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Guard the test with RequiresUnreadableEntryFact.");
        }

        var parent = Directory.CreateDirectory(
            System.IO.Path.Combine(root, "denied-" + Guid.NewGuid().ToString("N")[..8])
        );
        var path = System.IO.Path.Combine(parent.FullName, "unreadable");
        File.WriteAllText(path, "protected");
        Deny(parent);

        new FileInfo(path)
            .Exists.Should()
            .BeFalse(
                "the whole point of this input is that it reads as absent — if it does not, the test is not "
                    + "exercising the case the guard gets wrong"
            );
        var read = () => new FileInfo(path).Attributes;
        _ = read.Should()
            .Throw<UnauthorizedAccessException>(
                "the attributes are the only place the difference between absent and unreadable survives"
            );

        return new UnreadableEntry(parent, path);
    }

    /// <summary>
    /// Denies LISTING of an existing <paramref name="directory"/>, leaving it visible and TRAVERSABLE, then
    /// asserts it really cannot be enumerated.
    /// <para>
    /// The sibling input to <see cref="Create"/>, one level up: that one is an entry whose attributes will not
    /// read, this one is a directory whose CONTENTS will not list. A host walk meets them at different calls
    /// and both mean the same thing — the walk cannot establish what is in there — so both must stop it.
    /// </para>
    /// <para>
    /// Only <see cref="FileSystemRights.ListDirectory"/> is denied, and that is deliberate rather than
    /// minimal-by-habit: traversal survives it, so git goes on opening paths underneath by name and
    /// succeeding. That is what makes this the dangerous shape instead of a merely broken store — the git
    /// steps that are supposed to notice a wedged store report nothing at all. Denying more would break git
    /// too, and a test built on that would prove only that a broken store gets reported.
    /// </para>
    /// </summary>
    public static UnreadableEntry UnlistableDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Guard the test with RequiresUnreadableEntryFact.");
        }

        var denied = new DirectoryInfo(directory);
        Deny(denied, FileSystemRights.ListDirectory, InheritanceFlags.None);

        var enumerate = () => Directory.GetFileSystemEntries(directory);
        _ = enumerate
            .Should()
            .Throw<UnauthorizedAccessException>(
                "a directory the test could still enumerate would leave the body proving nothing"
            );
        Directory
            .Exists(directory)
            .Should()
            .BeTrue(
                "the walk only descends into what it believes is a directory, so a denial that hid it entirely "
                    + "would route the test past the case instead of into it"
            );

        return new UnreadableEntry(denied, directory);
    }

    /// <summary>
    /// Plants a redirected entry the walk can see, classify, and refuse to follow — but is DENIED permission to
    /// REMOVE. The third shape in this file, and the only one that is readable on purpose: the other two stop
    /// the walk at "I could not look", this one lets it look, reach the right verdict, and then fail on the act
    /// the verdict chose.
    /// <para>
    /// The denial must be INHERITED rather than applied to the link, and that is the whole trick. Setting an ACL
    /// on a reparse point through <see cref="FileSystemInfo"/> opens it WITHOUT
    /// <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so the ACE lands on the link's TARGET and the link itself stays
    /// freely deletable — a "simpler" version that denies on the link directly builds a perfectly removable
    /// junction and leaves the test body proving nothing. Denying on the parent FIRST and creating the link
    /// AFTER makes the link inherit it at creation, which is the only way the ACE reaches the link object.
    /// </para>
    /// <para>
    /// Both delete rights are denied, and neither is redundant: <see cref="FileSystemRights.Delete"/> is the
    /// right on the link, and <see cref="FileSystemRights.DeleteSubdirectoriesAndFiles"/> is the right on the
    /// PARENT that lets a caller remove a child regardless of the child's own. Denying one alone leaves the
    /// other route open.
    /// </para>
    /// <para>
    /// Nothing else is denied, which is what keeps the entry on the path under test: attributes still read, so
    /// the guard still reaches its <c>Redirected</c> verdict, and the parent still enumerates, so the walk still
    /// reaches the entry. Widening the denial would stop the walk one call earlier, at <c>ChildrenOf</c>, and
    /// prove the case that is already covered.
    /// </para>
    /// <para>
    /// <see cref="Supported"/> probes whether a deny ACE bites for READS; a machine could in principle honour
    /// that and still let this process delete past a denial. That case does not skip — the assertions below
    /// fail it, loudly, which is the right outcome: it says the input was not built, rather than passing an
    /// unbuilt test.
    /// </para>
    /// </summary>
    public static UnreadableEntry UndeletableLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Guard the test with RequiresUnreadableEntryFact.");
        }

        var parent = new DirectoryInfo(System.IO.Path.GetDirectoryName(link)!);
        Deny(
            parent,
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit
        );
        DirectoryLink.Create(link, target);

        Directory
            .Exists(link)
            .Should()
            .BeTrue(
                "the walk only meets entries it can see, and a link the guard reads as absent would route the test "
                    + "past the case instead of into it"
            );
        Exception? refusedRemoval = null;
        try
        {
            Directory.Delete(link);
        }
        catch (Exception ex)
        {
            refusedRemoval = ex;
        }

        (refusedRemoval is IOException or UnauthorizedAccessException)
            .Should()
            .BeTrue(
                "the input IS the unremovable link, but removing it produced {0} — measured on Windows 11 the "
                    + "denial surfaces as IOException, not the UnauthorizedAccessException the File.Delete shape "
                    + "suggests, which is why the production catch has to keep both",
                refusedRemoval?.GetType().Name ?? "no exception at all"
            );
        Directory
            .Exists(link)
            .Should()
            .BeTrue("a link the delete actually removed would leave the walk with nothing to fail on");

        return new UnreadableEntry(parent, link, link);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Allow(_parent);
            if (_link is not null)
            {
                // Non-recursive on purpose: it takes the LINK and never its target. This has to happen BEFORE
                // the parent goes, because Directory.Delete(recursive: true) throws on a junction rather than
                // removing it — so without this the parent below cannot be deleted at all.
                Directory.Delete(_link);
            }

            _parent.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a temp directory left behind fails nothing, and throwing here would replace a real
            // assertion failure with a cleanup one.
        }
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var root = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crd-probe-" + Guid.NewGuid().ToString("N"))
        );
        try
        {
            var path = System.IO.Path.Combine(root.FullName, "probe");
            File.WriteAllText(path, "probe");
            Deny(root);
            _ = new FileInfo(path).Attributes;
            return false; // Readable through the denial, so this machine cannot build the input.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        finally
        {
            try
            {
                Allow(root);
                root.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort, as in Dispose.
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Deny(DirectoryInfo directory) =>
        Deny(
            directory,
            FileSystemRights.FullControl,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit
        );

    [SupportedOSPlatform("windows")]
    private static void Deny(DirectoryInfo directory, FileSystemRights rights, InheritanceFlags inheritance)
    {
        var security = directory.GetAccessControl();
        security.AddAccessRule(Rule(inheritance, rights));
        directory.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void Allow(DirectoryInfo directory)
    {
        var security = directory.GetAccessControl();
        // Matches on identity and access type, not on rights, so it lifts either denial shape above.
        security.RemoveAccessRuleAll(Rule(InheritanceFlags.None));
        directory.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule Rule(
        InheritanceFlags inheritance,
        FileSystemRights rights = FileSystemRights.FullControl
    ) => new(WindowsIdentity.GetCurrent().User!, rights, inheritance, PropagationFlags.None, AccessControlType.Deny);
}

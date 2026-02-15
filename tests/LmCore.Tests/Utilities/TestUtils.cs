namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;

public static class TestUtils
{
    /// <summary>
    ///     Finds the workspace root directory by looking for the .git directory, .env.test file, or solution file.
    /// </summary>
    /// <param name="startingPath">The directory to start searching from.</param>
    /// <returns>The path to the workspace root directory.</returns>
    public static string FindWorkspaceRoot(string startingPath)
    {
        var directory = new DirectoryInfo(startingPath);

        while (directory != null)
        {
            // Check if .git directory exists (most reliable indicator of repository root)
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            // Check if .env.test exists in this directory
            if (File.Exists(Path.Combine(directory.FullName, ".env.test")))
            {
                return directory.FullName;
            }

            // Check if solution file exists in this directory (alternative marker for workspace root)
            if (Directory.GetFiles(directory.FullName, "*.sln").Length > 0)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        // If we can't find the workspace root, return the current directory
        return AppDomain.CurrentDomain.BaseDirectory;
    }
}

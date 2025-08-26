using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.Example.ExamplePythonMCPClient
{
    /// <summary>
    /// Shared utility methods for MCP client examples
    /// </summary>
    public static class Utils
    {
        /// <summary>
        /// Handle and display the agent's reply
        /// </summary>
        public static void HandleReply(IMessage reply)
        {
            if (reply is TextMessage textReply)
            {
                Console.WriteLine("\nAgent Response:\n");
                Console.WriteLine(textReply.Text);
            }
            else if (reply is ToolsCallAggregateMessage toolsCallMessage)
            {
                Console.WriteLine("\nAgent is making tool calls:\n");
                foreach (var toolCallResult in toolsCallMessage.ToolsCallResult.ToolCallResults)
                {
                    Console.WriteLine($"Tool call ID: {toolCallResult.ToolCallId}");

                    Console.WriteLine("\nTool Response:\n");
                    Console.WriteLine(toolCallResult.Result);
                }
            }
        }

        /// <summary>
        /// Loads environment variables from .env file in the project root
        /// </summary>
        /// <remarks>
        /// Tries multiple locations to find the .env file:
        /// 1. Current directory
        /// 2. Project directory
        /// 3. Solution root directory
        /// </remarks>
        public static void LoadEnvironmentVariables()
        {
            var curPath = Environment.CurrentDirectory;
            while (
                curPath != null
                && !string.IsNullOrEmpty(curPath)
                && !File.Exists(Path.Combine(curPath, ".env"))
            )
            {
                curPath = Path.GetDirectoryName(curPath);
            }

            _ =
                curPath != null
                && !string.IsNullOrEmpty(curPath)
                && File.Exists(Path.Combine(curPath, ".env"))
                    ? DotNetEnv.Env.Load(Path.Combine(curPath, ".env"))
                    : throw new FileNotFoundException(
                        ".env file not found in the current directory or any parent directories."
                    );
        }

        /// <summary>
        /// Gets the workspace root directory by traversing up the directory tree.
        /// </summary>
        /// <returns>The path to the workspace root directory.</returns>
        public static string GetWorkspaceRoot()
        {
            var currentDirectory = Environment.CurrentDirectory;

            while (!string.IsNullOrEmpty(currentDirectory))
            {
                // Check for a marker file or folder that identifies the workspace root
                if (
                    Directory.Exists(Path.Combine(currentDirectory, ".git"))
                    || Directory.GetFiles(currentDirectory, "*.sln").Any()
                )
                {
                    return currentDirectory;
                }

                // Move up one directory
                currentDirectory = Path.GetDirectoryName(currentDirectory);
            }

            throw new DirectoryNotFoundException("Workspace root could not be determined.");
        }
    }
}

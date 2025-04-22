using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.TestUtils.MockTools;

/// <summary>
/// Mock Python execution tool for testing that mirrors tools in server.py
/// </summary>
[McpServerToolType]
public static class MockPythonExecutionTool
{
    /// <summary>
    /// Executes Python code in a simulated Docker container
    /// </summary>
    /// <param name="code">Python code to execute</param>
    /// <returns>Output from executed code</returns>
    [McpServerTool(Name = "execute_python_in_container"), 
     Description("Execute Python code in a Docker container. The environment is limited to the container.")]
    public static string ExecutePythonInContainer(string code)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Lists the contents of a directory
    /// </summary>
    /// <param name="relativePath">Relative path within the code directory</param>
    /// <returns>Directory listing as a string</returns>
    [McpServerTool(Name = "list_directory"), 
     Description("List the contents of a directory within the code directory where python code is executed")]
    public static string ListDirectory(string relativePath = "")
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Reads a file from the code directory
    /// </summary>
    /// <param name="relativePath">Relative path to the file</param>
    /// <returns>File contents as a string</returns>
    [McpServerTool(Name = "read_file"), 
     Description("Read a file from the code directory where python code is executed")]
    public static string ReadFile(string relativePath)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Writes content to a file in the code directory
    /// </summary>
    /// <param name="relativePath">Relative path to the file</param>
    /// <param name="content">Content to write to the file</param>
    /// <returns>Status message</returns>
    [McpServerTool(Name = "write_file"), 
     Description("Write content to a file in the code directory where python code is executed")]
    public static string WriteFile(string relativePath, string content)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Deletes a file from the code directory
    /// </summary>
    /// <param name="relativePath">Relative path to the file</param>
    /// <returns>Status message</returns>
    [McpServerTool(Name = "delete_file"), 
     Description("Delete a file from the code directory where python code is executed")]
    public static string DeleteFile(string relativePath)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Gets an ASCII tree representation of a directory structure
    /// </summary>
    /// <param name="relativePath">Relative path within the code directory</param>
    /// <returns>ASCII tree representation as a string</returns>
    [McpServerTool(Name = "get_directory_tree"), 
     Description("Get an ASCII tree representation of a directory structure where python code is executed")]
    public static string GetDirectoryTree(string relativePath = "")
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Cleans up the code directory by removing all files and subdirectories
    /// </summary>
    /// <returns>Status message</returns>
    [McpServerTool(Name = "cleanup_code_directory"), 
     Description("Clean up the code directory by removing all files and subdirectories")]
    public static string CleanupCodeDirectory()
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }
} 
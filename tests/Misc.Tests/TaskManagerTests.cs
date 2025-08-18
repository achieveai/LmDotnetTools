using AchieveAi.LmDotnetTools.Misc.Utils;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests;

public class TaskManagerTests
{
  [Fact]
  public void GetCurrentTaskContext_WithNoTasks_ReturnsEmpty()
  {
    // Arrange
    var taskManager = new TaskManager();

    // Act
    var context = taskManager.GetCurrentTaskContext();

    // Assert
    Assert.Equal("", context);
  }

  [Fact]
  public void GetCurrentTaskContext_WithActiveTasks_ReturnsFormattedContext()
  {
    // Arrange
    var taskManager = new TaskManager();
    taskManager.AddTask("Test main task");
    taskManager.AddTask("Test subtask", parentId: 1);

    // Act
    var context = taskManager.GetCurrentTaskContext();

    // Assert
    Assert.Contains("Current Tasks:", context);
    Assert.Contains("[ ] Task 1: Test main task", context);
    Assert.Contains("[ ] 2: Test subtask", context);
  }

  [Fact]
  public void GetCurrentTaskContext_WithCompletedTasks_ExcludesCompleted()
  {
    // Arrange
    var taskManager = new TaskManager();
    taskManager.AddTask("Active task");
    taskManager.AddTask("Completed task");
    taskManager.UpdateTaskStatus(2, "completed");

    // Act
    var context = taskManager.GetCurrentTaskContext();

    // Assert
    Assert.Contains("[ ] Task 1: Active task", context);
    Assert.DoesNotContain("Completed task", context);
  }

  [Fact]
  public void GetCurrentTaskContext_WithInProgressTasks_ShowsCorrectSymbol()
  {
    // Arrange
    var taskManager = new TaskManager();
    taskManager.AddTask("In progress task");
    taskManager.UpdateTaskStatus(1, "in progress");

    // Act
    var context = taskManager.GetCurrentTaskContext();

    // Assert
    Assert.Contains("[-] Task 1: In progress task", context);
  }

  [Fact]
  public void GetCurrentTaskContext_WithOnlyCompletedTasks_ReturnsEmpty()
  {
    // Arrange
    var taskManager = new TaskManager();
    taskManager.AddTask("Completed task");
    taskManager.UpdateTaskStatus(1, "completed");

    // Act
    var context = taskManager.GetCurrentTaskContext();

    // Assert
    Assert.Equal("", context);
  }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

public class TaskManagerTests
{
    private readonly ITestOutputHelper _output;
    private readonly TaskManager _taskManager;

    public TaskManagerTests(ITestOutputHelper output)
    {
        _output = output;
        _taskManager = new TaskManager();
    }

    #region AddTask Tests

    [Fact]
    public void AddTask_WithValidTitle_ShouldAddMainTask()
    {
        // Act
        var result = _taskManager.AddTask("Test task");

        // Assert
        result.Should().StartWith("Added task 1:");
        result.Should().Contain("Test task");

        var tasks = _taskManager.ListTasks();
        tasks.Should().Contain("Test task");
    }

    [Fact]
    public void AddTask_WithEmptyTitle_ShouldReturnError()
    {
        // Act
        var result1 = _taskManager.AddTask("");
        var result2 = _taskManager.AddTask("   ");
        var result3 = _taskManager.AddTask(null!);

        // Assert
        result1.Should().Be("Error: Title cannot be empty.");
        result2.Should().Be("Error: Title cannot be empty.");
        result3.Should().Be("Error: Title cannot be empty.");
    }

    [Fact]
    public void AddTask_WithValidParentId_ShouldAddSubtask()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);

        // Act
        var result = _taskManager.AddTask("Subtask", parentId);

        // Assert
        result.Should().Contain($"Added task {parentId}.1");
        result.Should().Contain("Subtask");
    }

    [Fact]
    public void AddTask_WithInvalidParentId_ShouldReturnError()
    {
        // Act
        var result = _taskManager.AddTask("Subtask", 999);

        // Assert
        result.Should().Be("Error: Task '999' not found.");
    }

    [Fact]
    public void AddTask_ToSubtask_ShouldAddNestedTask()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId);
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.AddTask("Sub-subtask", $"{parentId}.{subtaskId}");

        // Assert
        result.Should().Contain($"Added task {parentId}.{subtaskId}.1");
        result.Should().Contain("Sub-subtask");
    }

    #endregion

    #region BulkInitialize Tests

    [Fact]
    public void BulkInitialize_WithValidTasks_ShouldAddAllTasks()
    {
        // Arrange
        var tasks = new List<TaskManager.BulkTaskItem>
        {
            new()
            {
                Task = "Task 1",
                SubTasks = ["Subtask 1.1", "Subtask 1.2"],
                Notes = ["Note 1", "Note 2"],
            },
            new()
            {
                Task = "Task 2",
                SubTasks = ["Subtask 2.1"],
                Notes = ["Note A"],
            },
        };

        // Act
        var result = _taskManager.BulkInitialize(tasks);

        // Assert
        result.Should().Contain("Added 2 task(s)");
        result.Should().Contain("Task 1");
        result.Should().Contain("Task 2");

        var taskList = _taskManager.ListTasks();
        taskList.Should().Contain("Task 1");
        taskList.Should().Contain("Task 2");
        taskList.Should().Contain("Subtask 1.1");
        taskList.Should().Contain("Subtask 1.2");
        taskList.Should().Contain("Subtask 2.1");
    }

    [Fact]
    public void BulkInitialize_WithClearExisting_ShouldClearAndAdd()
    {
        // Arrange
        _taskManager.AddTask("Existing task");
        var tasks = new List<TaskManager.BulkTaskItem> { new() { Task = "New task" } };

        // Act
        var result = _taskManager.BulkInitialize(tasks, clearExisting: true);

        // Assert
        result.Should().Contain("Cleared existing tasks");
        result.Should().Contain("Added 1 task(s)");

        var taskList = _taskManager.ListTasks();
        taskList.Should().NotContain("Existing task");
        taskList.Should().Contain("New task");
    }

    [Fact]
    public void BulkInitialize_WithEmptyTaskTitles_ShouldSilentlySkip()
    {
        // Arrange
        var tasks = new List<TaskManager.BulkTaskItem>
        {
            new() { Task = "" },
            new() { Task = "   " },
            new() { Task = "Valid task" },
            new() { Task = null! },
        };

        // Act
        var result = _taskManager.BulkInitialize(tasks);

        // Assert
        result.Should().Contain("Added 1 task(s)");
        result.Should().Contain("Valid task");
        result.Should().NotContain("Error");
    }

    [Fact]
    public void BulkInitialize_WithEmptySubtasks_ShouldSilentlySkip()
    {
        // Arrange
        var tasks = new List<TaskManager.BulkTaskItem>
        {
            new()
            {
                Task = "Main task",
                SubTasks = ["", "Valid subtask", "   ", null!],
            },
        };

        // Act
        var result = _taskManager.BulkInitialize(tasks);

        // Assert
        var taskList = _taskManager.ListTasks();
        taskList.Should().Contain("Main task");
        taskList.Should().Contain("Valid subtask");
        taskList.Split('\n').Count(line => line.Contains("Valid subtask")).Should().Be(1);
    }

    [Fact]
    public void BulkInitialize_WithNullOrEmptyList_ShouldReturnError()
    {
        // Act
        var result1 = _taskManager.BulkInitialize(null!);
        var result2 = _taskManager.BulkInitialize([]);

        // Assert
        result1.Should().Be("Error: No tasks provided for initialization.");
        result2.Should().Be("Error: No tasks provided for initialization.");
    }

    #endregion

    #region UpdateTask Tests

    [Fact]
    public void UpdateTask_MainTask_ShouldUpdateStatus()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);

        // Act
        var result1 = _taskManager.UpdateTask(taskId, status: "in progress");
        var result2 = _taskManager.UpdateTask(taskId, status: "completed");

        // Assert
        result1.Should().Contain($"Updated task {taskId} status to 'in progress'");
        result2.Should().Contain($"Updated task {taskId} status to 'completed'");

        var taskDetails = _taskManager.GetTask(taskId);
        taskDetails.Should().Contain("Status: completed");
    }

    [Fact]
    public void UpdateTask_Subtask_ShouldUpdateStatus()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId);
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.UpdateTask(parentId, subtaskId, "completed");

        // Assert
        result.Should().Contain($"Updated task {parentId}.{subtaskId} status to 'completed'");

        var taskDetails = _taskManager.GetTask(parentId, subtaskId);
        taskDetails.Should().Contain("Status: completed");
    }

    [Fact]
    public void UpdateTask_WithInvalidStatus_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.UpdateTask(taskId, status: "invalid");

        // Assert
        result.Should().Be("Error: Invalid status. Use: not started, in progress, completed, removed.");
    }

    [Fact]
    public void UpdateTask_WithVariousStatusFormats_ShouldAccept()
    {
        // Arrange
        var tasks = new[]
        {
            ("not started", "not started"),
            ("not_started", "not started"),
            ("todo", "not started"),
            ("in progress", "in progress"),
            ("in_progress", "in progress"),
            ("doing", "in progress"),
            ("completed", "completed"),
            ("done", "completed"),
            ("removed", "removed"),
            ("deleted", "removed"),
        };

        foreach (var (input, expected) in tasks)
        {
            var addResult = _taskManager.AddTask($"Task for {input}");
            var taskId = ExtractTaskId(addResult);

            // Act
            var result = _taskManager.UpdateTask(taskId, status: input);

            // Assert
            result.Should().Contain($"status to '{expected}'");
        }
    }

    #endregion

    #region DeleteTask Tests

    [Fact]
    public void DeleteTask_MainTask_ShouldRemoveTaskAndSubtasks()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        _taskManager.AddTask("Subtask 1", parentId);
        _taskManager.AddTask("Subtask 2", parentId);

        // Act
        var result = _taskManager.DeleteTask(parentId);

        // Assert
        result.Should().Contain($"Deleted task {parentId} and all subtasks");
        result.Should().Contain("Parent task");

        var getResult = _taskManager.GetTask(parentId);
        getResult.Should().Contain("Error: Task");
        getResult.Should().Contain("not found");
    }

    [Fact]
    public void DeleteTask_Subtask_ShouldRemoveOnlySubtask()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        var subtask1Result = _taskManager.AddTask("Subtask 1", parentId);
        var subtask1Id = ExtractTaskId(subtask1Result);
        var subtask2Result = _taskManager.AddTask("Subtask 2", parentId);
        var subtask2Id = ExtractTaskId(subtask2Result);

        // Act
        var result = _taskManager.DeleteTask(parentId, subtask1Id);

        // Assert
        result.Should().Contain($"Deleted subtask {subtask1Id} from task {parentId}");
        result.Should().Contain("Subtask 1");

        var parentDetails = _taskManager.GetTask(parentId);
        parentDetails.Should().NotContain("Subtask 1");
        parentDetails.Should().Contain("Subtask 2");
    }

    [Fact]
    public void DeleteTask_NonExistentTask_ShouldReturnError()
    {
        // Act
        var result = _taskManager.DeleteTask(999);

        // Assert
        result.Should().Be("Error: Task 999 not found.");
    }

    #endregion

    #region GetTask Tests

    [Fact]
    public void GetTask_MainTask_ShouldReturnDetails()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Test note", action: "add");
        _taskManager.AddTask("Subtask", taskId);

        // Act
        var result = _taskManager.GetTask(taskId);

        // Assert
        result.Should().Contain($"Task {taskId}: Test task");
        result.Should().Contain("Status: not started");
        result.Should().Contain("Notes (1):");
        result.Should().Contain("Test note");
        result.Should().Contain("Subtasks (1):");
        result.Should().Contain("Subtask");
    }

    [Fact]
    public void GetTask_Subtask_ShouldReturnDetails()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId);
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.GetTask(parentId, subtaskId);

        // Assert
        result.Should().Contain($"Subtask {subtaskId} of task {parentId}: Subtask");
        result.Should().Contain("Status: not started");
    }

    #endregion

    #region ManageNotes Tests

    [Fact]
    public void ManageNotes_AddNote_ShouldAddToTask()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.ManageNotes(taskId, noteText: "Test note", action: "add");

        // Assert
        result.Should().Contain($"Added note to task {taskId}");

        var notes = _taskManager.ListNotes(taskId);
        notes.Should().Contain("Test note");
    }

    [Fact]
    public void ManageNotes_EditNote_ShouldUpdateNote()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Original note", action: "add");

        // Act
        var result = _taskManager.ManageNotes(taskId, noteText: "Updated note", noteIndex: 1, action: "edit");

        // Assert
        result.Should().Contain($"Edited note 1 on task {taskId}");

        var notes = _taskManager.ListNotes(taskId);
        notes.Should().NotContain("Original note");
        notes.Should().Contain("Updated note");
    }

    [Fact]
    public void ManageNotes_DeleteNote_ShouldRemoveNote()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Note 1", action: "add");
        _taskManager.ManageNotes(taskId, noteText: "Note 2", action: "add");

        // Act
        var result = _taskManager.ManageNotes(taskId, noteIndex: 1, action: "delete");

        // Assert
        result.Should().Contain($"Deleted note 1 from task {taskId}");

        var notes = _taskManager.ListNotes(taskId);
        notes.Should().NotContain("Note 1");
        notes.Should().Contain("Note 2");
    }

    [Fact]
    public void ManageNotes_InvalidAction_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.ManageNotes(taskId, action: "invalid");

        // Assert
        result.Should().Be("Error: Invalid action. Use: add, edit, delete.");
    }

    [Fact]
    public void ManageNotes_EditWithInvalidIndex_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Note 1", action: "add");

        // Act
        var result1 = _taskManager.ManageNotes(taskId, noteText: "Updated", noteIndex: 0, action: "edit");
        var result2 = _taskManager.ManageNotes(taskId, noteText: "Updated", noteIndex: 2, action: "edit");

        // Assert
        result1.Should().Contain("Error: Note index 0 out of range");
        result1.Should().Contain("has 1 note(s)");
        result2.Should().Contain("Error: Note index 2 out of range");
        result2.Should().Contain("has 1 note(s)");
    }

    #endregion

    #region ListTasks Tests

    [Fact]
    public void ListTasks_WithNoTasks_ShouldStillEmitHeader()
    {
        // Act
        var result = _taskManager.ListTasks();

        // Assert - a bare "No tasks found." leaves the model no clue which tool answered.
        result.Should().StartWith("# 📋 Task List");
        result.Should().EndWith("No tasks found.");
    }

    [Fact]
    public void ListTasks_WithFilterMatchingNothing_ShouldStillEmitHeader()
    {
        // Arrange
        _taskManager.AddTask("Task 1");

        // Act
        var result = _taskManager.ListTasks(status: "completed");

        // Assert
        result.Should().StartWith("# 📋 Task List");
        result.Should().EndWith("No tasks match the specified criteria.");
    }

    [Fact]
    public void ListTasks_WithStatusFilter_ShouldFilterTasks()
    {
        // Arrange
        var task1Result = _taskManager.AddTask("Task 1");
        var task1Id = ExtractTaskId(task1Result);
        var task2Result = _taskManager.AddTask("Task 2");
        var task2Id = ExtractTaskId(task2Result);
        var task3Result = _taskManager.AddTask("Task 3");
        var task3Id = ExtractTaskId(task3Result);

        _taskManager.UpdateTask(task1Id, status: "in progress");
        _taskManager.UpdateTask(task2Id, status: "completed");

        // Act
        var inProgressTasks = _taskManager.ListTasks(status: "in progress");
        var completedTasks = _taskManager.ListTasks(status: "completed");
        var notStartedTasks = _taskManager.ListTasks(status: "not started");

        // Assert
        inProgressTasks.Should().Contain("Task 1");
        inProgressTasks.Should().NotContain("Task 2");
        inProgressTasks.Should().NotContain("Task 3");

        completedTasks.Should().Contain("Task 2");
        completedTasks.Should().NotContain("Task 1");

        notStartedTasks.Should().Contain("Task 3");
        notStartedTasks.Should().NotContain("Task 1");
    }

    [Fact]
    public void ListTasks_WithMainOnly_ShouldExcludeSubtasks()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task");
        var parentId = ExtractTaskId(parentResult);
        _taskManager.AddTask("Subtask 1", parentId);
        _taskManager.AddTask("Subtask 2", parentId);

        // Act
        var allTasks = _taskManager.ListTasks();
        var mainOnly = _taskManager.ListTasks(mainOnly: true);

        // Assert
        allTasks.Should().Contain("Parent task");
        allTasks.Should().Contain("Subtask 1");
        allTasks.Should().Contain("Subtask 2");

        mainOnly.Should().Contain("Parent task");
        mainOnly.Should().NotContain("Subtask 1");
        mainOnly.Should().NotContain("Subtask 2");
    }

    #endregion

    #region SearchTasks Tests

    [Fact]
    public void SearchTasks_WithSearchTerm_ShouldFindMatchingTasks()
    {
        // Arrange
        _taskManager.AddTask("Design API");
        _taskManager.AddTask("Implement API");
        _taskManager.AddTask("Test database");

        // Act
        var result = _taskManager.SearchTasks(searchTerm: "API");

        // Assert
        result.Should().Contain("Found 2 task(s) matching 'API'");
        result.Should().Contain("Design API");
        result.Should().Contain("Implement API");
        result.Should().NotContain("Test database");
    }

    [Fact]
    public void SearchTasks_WithCountType_ShouldReturnCounts()
    {
        // Arrange
        var task1Result = _taskManager.AddTask("Task 1");
        var task1Id = ExtractTaskId(task1Result);
        var task2Result = _taskManager.AddTask("Task 2");
        var task2Id = ExtractTaskId(task2Result);
        _taskManager.AddTask("Subtask", task1Id);

        _taskManager.UpdateTask(task1Id, status: "completed");
        _taskManager.UpdateTask(task2Id, status: "removed");

        // Act
        var totalCount = _taskManager.SearchTasks(countType: "total");
        var completedCount = _taskManager.SearchTasks(countType: "completed");
        var pendingCount = _taskManager.SearchTasks(countType: "pending");
        var removedCount = _taskManager.SearchTasks(countType: "removed");

        // Assert
        totalCount.Should().Be("Total tasks: 3");
        completedCount.Should().Be("Completed tasks: 1");
        pendingCount.Should().Be("Pending tasks: 1");
        removedCount.Should().Be("Removed tasks: 1");
    }

    [Fact]
    public void SearchTasks_CaseInsensitive_ShouldFindTasks()
    {
        // Arrange
        _taskManager.AddTask("API Design");

        // Act
        var result1 = _taskManager.SearchTasks(searchTerm: "api");
        var result2 = _taskManager.SearchTasks(searchTerm: "API");
        var result3 = _taskManager.SearchTasks(searchTerm: "ApI");

        // Assert
        result1.Should().Contain("API Design");
        result2.Should().Contain("API Design");
        result3.Should().Contain("API Design");
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task AddTask_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var taskCount = 100;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < taskCount; i++)
        {
            var taskNum = i;
            tasks.Add(Task.Run(() => _taskManager.AddTask($"Task {taskNum}")));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(taskCount);
        results.Should().OnlyContain(r => r.StartsWith("Added task"));

        // Verify all task IDs are unique
        var taskIds = results.Select(r => ExtractTaskId(r)).ToList();
        taskIds.Should().OnlyHaveUniqueItems();
        taskIds.Should().HaveCount(taskCount);
    }

    [Fact]
    public async Task BulkInitialize_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var concurrentOps = 10;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < concurrentOps; i++)
        {
            var opNum = i;
            tasks.Add(
                Task.Run(() =>
                {
                    var bulkTasks = new List<TaskManager.BulkTaskItem>
                    {
                        new()
                        {
                            Task = $"Bulk {opNum} Task 1",
                            SubTasks = [$"Sub {opNum}.1"],
                        },
                        new()
                        {
                            Task = $"Bulk {opNum} Task 2",
                            SubTasks = [$"Sub {opNum}.2"],
                        },
                    };
                    return _taskManager.BulkInitialize(bulkTasks);
                })
            );
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(concurrentOps);
        results.Should().OnlyContain(r => r.Contains("Added 2 task(s)"));

        var allTasks = _taskManager.ListTasks();
        for (int i = 0; i < concurrentOps; i++)
        {
            allTasks.Should().Contain($"Bulk {i} Task 1");
            allTasks.Should().Contain($"Bulk {i} Task 2");
        }
    }

    [Fact]
    public async Task UpdateTask_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        var updateCount = 50;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < updateCount; i++)
        {
            var status =
                (i % 3) switch
                {
                    0 => "not started",
                    1 => "in progress",
                    _ => "completed",
                };
            tasks.Add(Task.Run(() => _taskManager.UpdateTask(taskId, status: status)));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(updateCount);
        results.Should().OnlyContain(r => r.Contains("Updated task"));

        // Final state should be one of the valid statuses
        var finalTask = _taskManager.GetTask(taskId);
        finalTask.Should().ContainAny("not started", "in progress", "completed");
    }

    [Fact]
    public async Task ManageNotes_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task");
        var taskId = ExtractTaskId(addResult);
        var noteCount = 50;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < noteCount; i++)
        {
            var noteNum = i;
            tasks.Add(Task.Run(() => _taskManager.ManageNotes(taskId, noteText: $"Note {noteNum}", action: "add")));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(noteCount);
        results.Should().OnlyContain(r => r.Contains("Added note"));

        var notes = _taskManager.ListNotes(taskId);
        var noteLines = notes
            .Split('\n')
            .Where(l =>
                l.Trim().StartsWith("1.")
                || l.Trim().StartsWith("2.")
                || l.Trim().StartsWith("3.")
                || l.Trim().StartsWith("4.")
                || l.Trim().StartsWith("5.")
            );
        noteLines.Count().Should().BeGreaterOrEqualTo(1); // At least some notes should be added
    }

    [Fact]
    public async Task MixedOperations_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var operations = new List<Task>();
        var results = new ConcurrentBag<string>();

        // Act - Mix of different operations running concurrently
        for (int i = 0; i < 20; i++)
        {
            var opNum = i;
            operations.Add(
                Task.Run(async () =>
                {
                    switch (opNum % 5)
                    {
                        case 0:
                            results.Add(await Task.Run(() => _taskManager.AddTask($"Task {opNum}")));
                            break;
                        case 1:
                            results.Add(await Task.Run(() => _taskManager.ListTasks()));
                            break;
                        case 2:
                            results.Add(await Task.Run(() => _taskManager.SearchTasks(countType: "total")));
                            break;
                        case 3:
                            var bulkTasks = new List<TaskManager.BulkTaskItem> { new() { Task = $"Bulk {opNum}" } };
                            results.Add(await Task.Run(() => _taskManager.BulkInitialize(bulkTasks)));
                            break;
                        case 4:
                            results.Add(await Task.Run(() => _taskManager.GetMarkdown()));
                            break;
                        default:
                            break;
                    }
                })
            );
        }

        await Task.WhenAll(operations);

        // Assert - All operations should complete without exceptions
        results.Should().HaveCount(20);
        results.Should().NotContainNulls();
    }

    #endregion

    #region Edge Cases Tests

    [Fact]
    public void TaskManager_LargeNumberOfTasks_ShouldHandleEfficiently()
    {
        // Arrange
        var stopwatch = Stopwatch.StartNew();
        var taskCount = 1000;

        // Act
        for (int i = 0; i < taskCount; i++)
        {
            _taskManager.AddTask($"Task {i}");
        }
        stopwatch.Stop();

        // Assert
        _output.WriteLine($"Added {taskCount} tasks in {stopwatch.ElapsedMilliseconds}ms");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000); // Should be fast

        var count = _taskManager.SearchTasks(countType: "total");
        count.Should().Be($"Total tasks: {taskCount}");
    }

    [Fact]
    public void TaskManager_DeepSubtaskHierarchy_ShouldAllowNestedTasks()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Level 1");
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Level 2", parentId);
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.AddTask("Level 3", $"{parentId}.{subtaskId}");

        // Assert
        result.Should().Contain($"Added task {parentId}.{subtaskId}.1");
        result.Should().Contain("Level 3");
    }

    [Fact]
    public void TaskManager_SpecialCharactersInTitles_ShouldHandle()
    {
        // Arrange
        var specialTitles = new[]
        {
            "Task with 'quotes'",
            "Task with \"double quotes\"",
            "Task with <html> tags",
            "Task with & ampersand",
            "Task with \n newline",
            "Task with \t tab",
            "Task with émojis 🎉",
        };

        // Act & Assert
        foreach (var title in specialTitles)
        {
            var result = _taskManager.AddTask(title);
            result.Should().Contain("Added task");
            result.Should().Contain(title.Trim());
        }

        var allTasks = _taskManager.ListTasks();
        foreach (var title in specialTitles)
        {
            allTasks.Should().Contain(title.Trim());
        }
    }

    [Fact]
    public void TaskManager_VeryLongTitlesAndNotes_ShouldHandle()
    {
        // Arrange
        var longTitle = new string('A', 1000);
        var longNote = new string('B', 5000);

        // Act
        var addResult = _taskManager.AddTask(longTitle);
        var taskId = ExtractTaskId(addResult);
        var noteResult = _taskManager.ManageNotes(taskId, noteText: longNote, action: "add");

        // Assert
        addResult.Should().Contain("Added task");
        noteResult.Should().Contain("Added note");

        var taskDetails = _taskManager.GetTask(taskId);
        taskDetails.Should().Contain(longTitle);
        taskDetails.Should().Contain(longNote);
    }

    #endregion

    #region GetMarkdown Tests

    [Fact]
    public void ListTasks_RendersTheDocumentedMarkdown()
    {
        // Arrange - one tree covering all four statuses, three levels of nesting,
        // and numbered notes. This is the only full-string assertion on the rendered
        // markdown, so it is what pins the format the spec documents.
        _taskManager.AddTask("Design API");
        _taskManager.AddTask("Define endpoints", "1");
        _taskManager.AddTask("Validate JWT", "1.1");
        _taskManager.AddTask("Draft schema", "1");
        _taskManager.AddTask("Ship it");
        _taskManager.AddNote("1", noteText: "Rate limit is 100/min");
        _taskManager.AddNote("1", noteText: "Auth via JWT");
        _taskManager.UpdateTask("1", "in progress");
        _taskManager.UpdateTask("1.1", "completed");
        _taskManager.UpdateTask("1.2", "removed");

        // Act
        var result = _taskManager.ListTasks();

        // Assert - LF throughout, so this doubles as the guard against
        // Environment.NewLine leaking CRLF into tool output on Windows.
        var expected =
            "# 📋 Task List\n"
            + "\n"
            + "**Status**: 1 in progress | 2 pending | 1 completed\n"
            + "**Total**: 3 active tasks\n"
            + "\n"
            + "[-] 1. Design API\n"
            + "  Notes:\n"
            + "  1. Rate limit is 100/min\n"
            + "  2. Auth via JWT\n"
            + "  [x] 1.1. Define endpoints\n"
            + "    [ ] 1.1.1. Validate JWT\n"
            + "  [~] 1.2. Draft schema (removed)\n"
            + "[ ] 2. Ship it";

        result.Should().Be(expected);
    }

    [Fact]
    public void GetMarkdown_ShouldReturnSameAsListTasks()
    {
        // Arrange
        _taskManager.AddTask("Task 1");
        _taskManager.AddTask("Task 2");

        // Act
        var markdown = _taskManager.GetMarkdown();
        var listTasks = _taskManager.ListTasks();

        // Assert
        markdown.Should().Be(listTasks);
    }

    #endregion

    #region Deep Hierarchy Addressing Tests

    [Fact]
    public void AddNote_AtDepthThree_ShouldReachTheTask()
    {
        // Arrange - add-task creates arbitrary depth, so every level must be addressable.
        _taskManager.AddTask("Level 1");
        _taskManager.AddTask("Level 2", "1");
        _taskManager.AddTask("Level 3", "1.1");

        // Act
        var result = _taskManager.AddNote("1.1.1", noteText: "Deep note");

        // Assert
        result.Should().Be("Added note to task 1.1.1.");
        _taskManager.ListNotes("1.1.1").Should().Contain("Deep note");
    }

    [Fact]
    public void GetTask_AtDepthThree_ShouldReturnDetails()
    {
        // Arrange
        _taskManager.AddTask("Level 1");
        _taskManager.AddTask("Level 2", "1");
        _taskManager.AddTask("Level 3", "1.1");

        // Act
        var result = _taskManager.GetTask("1.1.1");

        // Assert
        result.Should().Contain("Task 1.1.1: Level 3");
        result.Should().Contain("Status: not started");
    }

    [Fact]
    public void DeleteTask_AtDepthThree_ShouldDetachFromItsParent()
    {
        // Arrange
        _taskManager.AddTask("Level 1");
        _taskManager.AddTask("Level 2", "1");
        _taskManager.AddTask("Level 3", "1.1");

        // Act
        var result = _taskManager.DeleteTask("1.1.1");

        // Assert - removing only from RootTasks would report success and change nothing.
        result.Should().Contain("Deleted task 1.1.1 and all subtasks");
        _taskManager.ListTasks().Should().NotContain("Level 3");

        // Positive control. Under-deletion is only half the failure mode: detaching the root
        // ancestor instead of the target satisfies every negative assertion above, because the
        // success message is composed from taskId and Title before the removal happens. The
        // ancestors must survive.
        _taskManager.ListTasks().Should().Contain("Level 1").And.Contain("Level 2");
        _taskManager.GetTask("1.1").Should().Contain("Task 1.1: Level 2");

        // Exactly which absence this is matters: "not found" is emitted by three different
        // guards on this path (invalid format, root missing, node missing along the path), so
        // a substring match cannot tell "the leaf is gone" from "the tree is gone".
        _taskManager.GetTask("1.1.1").Should().Be("Error: Task '1.1.1' not found.");
    }

    [Fact]
    public void EditAndDeleteNote_AtDepthThree_ShouldReachTheTask()
    {
        // Arrange
        _taskManager.AddTask("Level 1");
        _taskManager.AddTask("Level 2", "1");
        _taskManager.AddTask("Level 3", "1.1");
        _taskManager.AddNote("1.1.1", noteText: "Original");

        // Act
        var edited = _taskManager.EditNote("1.1.1", noteIndex: 1, noteText: "Revised");
        var listed = _taskManager.ListNotes("1.1.1");
        var deleted = _taskManager.DeleteNote("1.1.1", noteIndex: 1);

        // Assert
        edited.Should().Be("Updated note #1 on task 1.1.1.");
        listed.Should().Contain("Revised");
        deleted.Should().Contain("Deleted note #1 from task 1.1.1");
        _taskManager.ListNotes("1.1.1").Should().Be("task 1.1.1 has no notes.");
    }

    #endregion

    #region Input Tolerance Tests

    [Fact]
    public void AddTask_WithBlankParentId_ShouldReturnErrorRatherThanCreateRootTask()
    {
        // Act - a supplied-but-blank parentId is a malformed call, not "no parent".
        var result = _taskManager.AddTask("Orphan", "   ");

        // Assert
        result.Should().Be("Error: Parent task ID cannot be blank. Omit parentId to add a main task.");
        _taskManager.ListTasks().Should().NotContain("Orphan");
    }

    [Fact]
    public void AddTask_WithOmittedParentId_ShouldStillCreateRootTask()
    {
        // Act
        var result = _taskManager.AddTask("Main");

        // Assert
        result.Should().Be("Added task 1: Main");
    }

    [Theory]
    [InlineData("not-started", "not started")]
    [InlineData("in-progress", "in progress")]
    [InlineData("to-do", "not started")]
    public void UpdateTask_WithHyphenatedStatus_ShouldAccept(string input, string expected)
    {
        // Arrange
        _taskManager.AddTask("Test task");

        // Act
        var result = _taskManager.UpdateTask("1", status: input);

        // Assert
        result.Should().Contain($"status to '{expected}'");
    }

    #endregion

    #region Concurrency Tests

    /// <summary>
    ///     Pins the lock coverage of the four read paths that walk <em>nested</em> SubTasks
    ///     lists after taking <c>_sync</c>: ListTasks, the GetMarkdown that delegates to it,
    ///     SearchTasks, and the GetTaskCounts branch of SearchTasks.
    ///     <para>
    ///         It discriminates by construction, and each of the three requirements matters:
    ///         the writer nests (<c>AddTask(title, "1")</c>) so it appends to a nested list
    ///         rather than to the root list; that same nested list is pre-seeded so a reader
    ///         spends real time inside it; and the readers are exactly the methods that
    ///         traverse it. Widen or drop any of those three <c>lock (_sync)</c> blocks and
    ///         this test fails within a handful of iterations — <c>GetAllTasksFlat</c>'s bare
    ///         <c>foreach</c> throws <see cref="InvalidOperationException" /> off the list's
    ///         version stamp, and a <c>[.. list]</c> copy of a concurrently grown list throws
    ///         <see cref="ArgumentException" /> from a Count that no longer matches the CopyTo.
    ///     </para>
    ///     <para>
    ///         GetTask is not driven here: its own <c>lock (_sync)</c> is redundant, because
    ///         the traversal it delegates to — <c>FindTaskByStringId</c> — takes <c>_sync</c>
    ///         itself, so removing GetTask's lock changes nothing to observe. The two JSON
    ///         serializers are not driven here either. Their locks are real and their removal
    ///         is observable, but not as an exception: see
    ///         <see cref="JsonSerializers_ConcurrentWithNestedAdds_ShouldEmitAConsistentSnapshot" />,
    ///         which pins them on a snapshot invariant instead.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task NestedReadOperations_ConcurrentWithNestedAdds_ShouldNotThrow()
    {
        // Arrange - a shallow spread of roots plus one deep list under task 1. The writer
        // appends to that deep list; the readers walk it. The writer is bounded by the
        // readers rather than by its own count: a fixed-count writer finishes in
        // milliseconds and the readers then run alone against a frozen tree.
        const int RootCount = 20;
        const int NestedSeedCount = 200;
        const int ReaderIterations = 100;
        const int WriterCap = 20000;

        for (var i = 0; i < RootCount; i++)
        {
            _ = _taskManager.AddTask($"Seed {i}");
        }

        for (var i = 0; i < NestedSeedCount; i++)
        {
            _ = _taskManager.AddTask($"Nested {i}", "1");
        }

        var failures = new ConcurrentBag<Exception>();
        using var startingGun = new ManualResetEventSlim(false);
        var stop = 0;

        // Act
        var writer = Task.Run(() =>
        {
            startingGun.Wait();
            var i = 0;
            while (Volatile.Read(ref stop) == 0 && i < WriterCap)
            {
                _ = _taskManager.AddTask($"Churn {i++}", "1");
            }
        });

        var readers = Enumerable
            .Range(0, 4)
            .Select(readerIndex =>
                Task.Run(() =>
                {
                    startingGun.Wait();
                    for (var i = 0; i < ReaderIterations; i++)
                    {
                        try
                        {
                            _ = _taskManager.ListTasks();
                            _ = _taskManager.SearchTasks("Nested");
                            _ = _taskManager.SearchTasks(countType: "total");
                        }
                        catch (Exception ex)
                        {
                            failures.Add(ex);
                        }
                    }
                })
            )
            .ToArray();

        startingGun.Set();
        await Task.WhenAll(readers);
        Volatile.Write(ref stop, 1);
        await writer;

        // Assert
        failures.Should().BeEmpty();
    }

    /// <summary>
    ///     Pins the lock on both JSON serializers. They walk the same tree as the readers
    ///     above but fail differently, so an exception assertion cannot see them: a torn read
    ///     here produces a well-formed document describing a state the manager was never in.
    ///     <para>
    ///         Two orderings create the window. <c>AddTask</c> raises
    ///         <c>parentTask.NextSubTaskId</c> before appending to <c>SubTasks</c>, and
    ///         <c>List&lt;T&gt;.Add</c> publishes the new <c>Count</c> before it stores the
    ///         element. So an unlocked serializer can emit a counter that runs ahead of the
    ///         list it counts, or a <c>null</c> where a task should be. Both are checked, on
    ///         every node, against invariants that hold for any consistent snapshot of an
    ///         append-only tree.
    ///     </para>
    ///     <para>
    ///         Runs against both serializers: <c>JsonSerializeTasksToJsonElements</c> had no
    ///         coverage of any kind.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonSerializers_ConcurrentWithNestedAdds_ShouldEmitAConsistentSnapshot(bool viaJsonElement)
    {
        // Arrange - many short rounds against a fresh manager, rather than one long run. The
        // writer is orders of magnitude faster than a reader that serializes the whole tree,
        // so a long run just inflates the document until every read is expensive and none of
        // them is contended: that shape takes minutes and still reports nothing. Short rounds
        // keep every read racing a live writer and keep the document small. The churn goes
        // under root 1, which ManagerState serializes before every other root and before
        // nextId, so the window spans the whole document.
        const int Rounds = 30;
        const int RootCount = 100;
        const int NestedSeedCount = 100;
        const int ReadsPerRound = 6;
        const int WriterCapPerRound = 50000;

        var violations = new ConcurrentBag<string>();

        // Act
        for (var round = 0; round < Rounds && violations.IsEmpty; round++)
        {
            var manager = new TaskManager();
            for (var i = 0; i < RootCount; i++)
            {
                _ = manager.AddTask($"Seed {i}");
            }

            for (var i = 0; i < NestedSeedCount; i++)
            {
                _ = manager.AddTask($"Nested {i}", "1");
            }

            using var startingGun = new ManualResetEventSlim(false);
            var stop = 0;

            var writer = Task.Run(() =>
            {
                startingGun.Wait();
                var i = 0;
                while (Volatile.Read(ref stop) == 0 && i < WriterCapPerRound)
                {
                    _ = manager.AddTask($"Churn {i++}", "1");
                }
            });

            startingGun.Set();
            for (var i = 0; i < ReadsPerRound; i++)
            {
                try
                {
                    var json = viaJsonElement
                        ? manager.JsonSerializeTasksToJsonElements().GetRawText()
                        : manager.JsonSerializeTasks();

                    using var document = JsonDocument.Parse(json);
                    CollectSnapshotViolations(document.RootElement, violations);
                }
                catch (Exception ex)
                {
                    violations.Add($"{ex.GetType().Name}: {ex.Message}");
                }
            }

            Volatile.Write(ref stop, 1);
            await writer;
        }

        // Assert
        violations.Should().BeEmpty();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     Invariants that hold for any consistent snapshot of an append-only tree, and only
    ///     for a consistent one: the id counters lead their lists by exactly one, and no slot
    ///     is empty.
    /// </summary>
    private static void CollectSnapshotViolations(JsonElement state, ConcurrentBag<string> violations)
    {
        var roots = state.GetProperty("rootTasks");
        var nextId = state.GetProperty("nextId").GetInt32();
        if (nextId != roots.GetArrayLength() + 1)
        {
            violations.Add($"nextId {nextId} against {roots.GetArrayLength()} root task(s)");
        }

        foreach (var root in roots.EnumerateArray())
        {
            CollectNodeViolations(root, violations);
        }
    }

    private static void CollectNodeViolations(JsonElement node, ConcurrentBag<string> violations)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            violations.Add($"a task serialized as {node.ValueKind}");
            return;
        }

        var subTasks = node.GetProperty("subTasks");
        var nextSubTaskId = node.GetProperty("nextSubTaskId").GetInt32();
        if (nextSubTaskId != subTasks.GetArrayLength() + 1)
        {
            violations.Add($"nextSubTaskId {nextSubTaskId} against {subTasks.GetArrayLength()} subtask(s)");
        }

        foreach (var subTask in subTasks.EnumerateArray())
        {
            CollectNodeViolations(subTask, violations);
        }
    }

    private static int ExtractTaskId(string result)
    {
        // Extract task ID from messages like "Added task 1: Title" or "Added subtask 2 under task 1: Title"
        var match = System.Text.RegularExpressions.Regex.Match(result, @"(?:task|subtask)\s+(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    #endregion
}

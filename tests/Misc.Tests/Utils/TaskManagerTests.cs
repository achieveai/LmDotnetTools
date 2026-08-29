using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
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
        var result = _taskManager.AddTask("Test task").Text;

        // Assert
        result.Should().StartWith("Added task 1:");
        result.Should().Contain("Test task");

        var tasks = _taskManager.ListTasks().Text;
        tasks.Should().Contain("Test task");
    }

    [Fact]
    public void AddTask_WithEmptyTitle_ShouldReturnError()
    {
        // Act
        var result1 = _taskManager.AddTask("").Text;
        var result2 = _taskManager.AddTask("   ").Text;
        var result3 = _taskManager.AddTask(null!).Text;

        // Assert
        result1.Should().Be("Error: Title cannot be empty.");
        result2.Should().Be("Error: Title cannot be empty.");
        result3.Should().Be("Error: Title cannot be empty.");
    }

    [Fact]
    public void AddTask_WithValidParentId_ShouldAddSubtask()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task").Text;
        var parentId = ExtractTaskId(parentResult);

        // Act
        var result = _taskManager.AddTask("Subtask", parentId).Text;

        // Assert
        result.Should().Contain($"Added task {parentId}.1");
        result.Should().Contain("Subtask");
    }

    [Fact]
    public void AddTask_WithInvalidParentId_ShouldReturnError()
    {
        // Act
        var result = _taskManager.AddTask("Subtask", 999).Text;

        // Assert
        result.Should().Be("Error: Task '999' not found.");
    }

    [Fact]
    public void AddTask_ToSubtask_ShouldAddNestedTask()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task").Text;
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId).Text;
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.AddTask("Sub-subtask", $"{parentId}.{subtaskId}").Text;

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
        var result = _taskManager.BulkInitialize(tasks).Text;

        // Assert
        result.Should().Contain("Added 2 task(s)");
        result.Should().Contain("Task 1");
        result.Should().Contain("Task 2");

        var taskList = _taskManager.ListTasks().Text;
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
        var result = _taskManager.BulkInitialize(tasks, clearExisting: true).Text;

        // Assert
        result.Should().Contain("Cleared existing tasks");
        result.Should().Contain("Added 1 task(s)");

        var taskList = _taskManager.ListTasks().Text;
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
        var result = _taskManager.BulkInitialize(tasks).Text;

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
            new() { Task = "Main task", SubTasks = ["", "Valid subtask", "   ", null!] },
        };

        // Act
        var result = _taskManager.BulkInitialize(tasks).Text;

        // Assert
        var taskList = _taskManager.ListTasks().Text;
        taskList.Should().Contain("Main task");
        taskList.Should().Contain("Valid subtask");
        taskList.Split('\n').Count(line => line.Contains("Valid subtask")).Should().Be(1);
    }

    [Fact]
    public void BulkInitialize_WithNullOrEmptyList_ShouldReturnError()
    {
        // Act
        var result1 = _taskManager.BulkInitialize(null!).Text;
        var result2 = _taskManager.BulkInitialize([]).Text;

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
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);

        // Act - claiming (passing agent) is now how 'in progress' records who is doing the
        // work; completing then requires that claim (PR4's claim discipline).
        var result1 = _taskManager.UpdateTask(taskId, status: "in progress", agent: "tester").Text;
        var result2 = _taskManager.UpdateTask(taskId, status: "completed").Text;

        // Assert
        result1.Should().Contain($"Updated task {taskId} status to 'in progress'");
        result2.Should().Contain($"Updated task {taskId} status to 'completed'");

        var taskDetails = _taskManager.GetTask(taskId).Text;
        taskDetails.Should().Contain("Status: completed");
    }

    [Fact]
    public void UpdateTask_Subtask_ShouldUpdateStatus()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task").Text;
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId).Text;
        var subtaskId = ExtractTaskId(subtaskResult);
        _taskManager.ClaimTask($"{parentId}.{subtaskId}", "tester");

        // Act
        var result = _taskManager.UpdateTask(parentId, subtaskId, "completed").Text;

        // Assert
        result.Should().Contain($"Updated task {parentId}.{subtaskId} status to 'completed'");

        var taskDetails = _taskManager.GetTask(parentId, subtaskId).Text;
        taskDetails.Should().Contain("Status: completed");
    }

    [Fact]
    public void UpdateTask_WithInvalidStatus_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.UpdateTask(taskId, status: "invalid").Text;

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
            var addResult = _taskManager.AddTask($"Task for {input}").Text;
            var taskId = ExtractTaskId(addResult);

            // A 'completed' target now requires the task be claimed first.
            if (expected == "completed")
            {
                _taskManager.ClaimTask(taskId.ToString(), "tester");
            }

            // Act
            var result = _taskManager.UpdateTask(taskId, status: input).Text;

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
        var parentResult = _taskManager.AddTask("Parent task").Text;
        var parentId = ExtractTaskId(parentResult);
        _taskManager.AddTask("Subtask 1", parentId);
        _taskManager.AddTask("Subtask 2", parentId);

        // Act
        var result = _taskManager.DeleteTask(parentId).Text;

        // Assert
        result.Should().Contain($"Deleted task {parentId} and all subtasks");
        result.Should().Contain("Parent task");

        var getResult = _taskManager.GetTask(parentId).Text;
        getResult.Should().Contain("Error: Task");
        getResult.Should().Contain("not found");
    }

    [Fact]
    public void DeleteTask_Subtask_ShouldRemoveOnlySubtask()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Parent task").Text;
        var parentId = ExtractTaskId(parentResult);
        var subtask1Result = _taskManager.AddTask("Subtask 1", parentId).Text;
        var subtask1Id = ExtractTaskId(subtask1Result);
        var subtask2Result = _taskManager.AddTask("Subtask 2", parentId).Text;
        var subtask2Id = ExtractTaskId(subtask2Result);

        // Act
        var result = _taskManager.DeleteTask(parentId, subtask1Id).Text;

        // Assert
        result.Should().Contain($"Deleted subtask {subtask1Id} from task {parentId}");
        result.Should().Contain("Subtask 1");

        var parentDetails = _taskManager.GetTask(parentId).Text;
        parentDetails.Should().NotContain("Subtask 1");
        parentDetails.Should().Contain("Subtask 2");
    }

    [Fact]
    public void DeleteTask_NonExistentTask_ShouldReturnError()
    {
        // Act
        var result = _taskManager.DeleteTask(999).Text;

        // Assert
        result.Should().Be("Error: Task 999 not found.");
    }

    #endregion

    #region GetTask Tests

    [Fact]
    public void GetTask_MainTask_ShouldReturnDetails()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Test note", action: "add");
        _taskManager.AddTask("Subtask", taskId);

        // Act
        var result = _taskManager.GetTask(taskId).Text;

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
        var parentResult = _taskManager.AddTask("Parent task").Text;
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Subtask", parentId).Text;
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.GetTask(parentId, subtaskId).Text;

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
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.ManageNotes(taskId, noteText: "Test note", action: "add").Text;

        // Assert
        result.Should().Contain($"Added note to task {taskId}");

        var notes = _taskManager.ListNotes(taskId).Text;
        notes.Should().Contain("Test note");
    }

    [Fact]
    public void ManageNotes_EditNote_ShouldUpdateNote()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Original note", action: "add");

        // Act
        var result = _taskManager.ManageNotes(taskId, noteText: "Updated note", noteIndex: 1, action: "edit").Text;

        // Assert
        result.Should().Contain($"Edited note 1 on task {taskId}");

        var notes = _taskManager.ListNotes(taskId).Text;
        notes.Should().NotContain("Original note");
        notes.Should().Contain("Updated note");
    }

    [Fact]
    public void ManageNotes_DeleteNote_ShouldRemoveNote()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Note 1", action: "add");
        _taskManager.ManageNotes(taskId, noteText: "Note 2", action: "add");

        // Act
        var result = _taskManager.ManageNotes(taskId, noteIndex: 1, action: "delete").Text;

        // Assert
        result.Should().Contain($"Deleted note 1 from task {taskId}");

        var notes = _taskManager.ListNotes(taskId).Text;
        notes.Should().NotContain("Note 1");
        notes.Should().Contain("Note 2");
    }

    [Fact]
    public void ManageNotes_InvalidAction_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);

        // Act
        var result = _taskManager.ManageNotes(taskId, action: "invalid").Text;

        // Assert
        result.Should().Be("Error: Invalid action. Use: add, edit, delete.");
    }

    [Fact]
    public void ManageNotes_EditWithInvalidIndex_ShouldReturnError()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);
        _taskManager.ManageNotes(taskId, noteText: "Note 1", action: "add");

        // Act
        var result1 = _taskManager.ManageNotes(taskId, noteText: "Updated", noteIndex: 0, action: "edit").Text;
        var result2 = _taskManager.ManageNotes(taskId, noteText: "Updated", noteIndex: 2, action: "edit").Text;

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
        var result = _taskManager.ListTasks().Text;

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
        var result = _taskManager.ListTasks(status: "completed").Text;

        // Assert
        result.Should().StartWith("# 📋 Task List");
        result.Should().EndWith("No tasks match the specified criteria.");
    }

    [Fact]
    public void ListTasks_WithStatusFilter_ShouldFilterTasks()
    {
        // Arrange
        var task1Result = _taskManager.AddTask("Task 1").Text;
        var task1Id = ExtractTaskId(task1Result);
        var task2Result = _taskManager.AddTask("Task 2").Text;
        var task2Id = ExtractTaskId(task2Result);
        var task3Result = _taskManager.AddTask("Task 3").Text;
        var task3Id = ExtractTaskId(task3Result);

        _taskManager.UpdateTask(task1Id, status: "in progress");
        _taskManager.ClaimTask(task2Id.ToString(), "tester");
        _taskManager.UpdateTask(task2Id, status: "completed");

        // Act
        var inProgressTasks = _taskManager.ListTasks(status: "in progress").Text;
        var completedTasks = _taskManager.ListTasks(status: "completed").Text;
        var notStartedTasks = _taskManager.ListTasks(status: "not started").Text;

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
        var parentResult = _taskManager.AddTask("Parent task").Text;
        var parentId = ExtractTaskId(parentResult);
        _taskManager.AddTask("Subtask 1", parentId);
        _taskManager.AddTask("Subtask 2", parentId);

        // Act
        var allTasks = _taskManager.ListTasks().Text;
        var mainOnly = _taskManager.ListTasks(mainOnly: true).Text;

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
        var result = _taskManager.SearchTasks(searchTerm: "API").Text;

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
        var task1Result = _taskManager.AddTask("Task 1").Text;
        var task1Id = ExtractTaskId(task1Result);
        var task2Result = _taskManager.AddTask("Task 2").Text;
        var task2Id = ExtractTaskId(task2Result);
        _taskManager.AddTask("Subtask", task1Id);

        _taskManager.ClaimTask(task1Id.ToString(), "tester");
        _taskManager.UpdateTask(task1Id, status: "completed");
        _taskManager.UpdateTask(task2Id, status: "removed");

        // Act
        var totalCount = _taskManager.SearchTasks(countType: "total").Text;
        var completedCount = _taskManager.SearchTasks(countType: "completed").Text;
        var pendingCount = _taskManager.SearchTasks(countType: "pending").Text;
        var removedCount = _taskManager.SearchTasks(countType: "removed").Text;

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
        var result1 = _taskManager.SearchTasks(searchTerm: "api").Text;
        var result2 = _taskManager.SearchTasks(searchTerm: "API").Text;
        var result3 = _taskManager.SearchTasks(searchTerm: "ApI").Text;

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
            tasks.Add(Task.Run(() => _taskManager.AddTask($"Task {taskNum}").Text));
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
                        new() { Task = $"Bulk {opNum} Task 1", SubTasks = [$"Sub {opNum}.1"] },
                        new() { Task = $"Bulk {opNum} Task 2", SubTasks = [$"Sub {opNum}.2"] },
                    };
                    return _taskManager.BulkInitialize(bulkTasks).Text;
                })
            );
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(concurrentOps);
        results.Should().OnlyContain(r => r.Contains("Added 2 task(s)"));

        var allTasks = _taskManager.ListTasks().Text;
        for (int i = 0; i < concurrentOps; i++)
        {
            allTasks.Should().Contain($"Bulk {i} Task 1");
            allTasks.Should().Contain($"Bulk {i} Task 2");
        }
    }

    [Fact]
    public async Task UpdateTask_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange - claimed up front so a concurrent 'completed' racing a concurrent
        // 'not started' has a real chance to succeed rather than always hitting the new
        // claim-required-to-complete rule; that rule tripping on some interleavings is
        // expected (see below) and not itself a thread-safety failure.
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);
        _taskManager.ClaimTask(taskId.ToString(), "tester");
        var updateCount = 50;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < updateCount; i++)
        {
            var status = (i % 3) switch
            {
                0 => "not started",
                1 => "in progress",
                _ => "completed",
            };
            tasks.Add(Task.Run(() => _taskManager.UpdateTask(taskId, status: status).Text));
        }

        var results = await Task.WhenAll(tasks);

        // Assert - every call returns cleanly (no exception, no corrupted/blank text). A
        // 'completed' call that lands while a concurrent writer has the task at 'not started'
        // legitimately reports the claim-required error instead of a silent, wrong success —
        // that is the invariant under test here, not a flake.
        results.Should().HaveCount(updateCount);
        results.Should().OnlyContain(r => r.Contains("Updated task") || r.Contains("must be claimed"));

        // Final state should be one of the valid statuses
        var finalTask = _taskManager.GetTask(taskId).Text;
        finalTask.Should().ContainAny("not started", "in progress", "completed");
    }

    [Fact]
    public async Task ManageNotes_Concurrent_ShouldBeThreadSafe()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Test task").Text;
        var taskId = ExtractTaskId(addResult);
        var noteCount = 50;
        var tasks = new List<Task<string>>();

        // Act
        for (int i = 0; i < noteCount; i++)
        {
            var noteNum = i;
            tasks.Add(
                Task.Run(() => _taskManager.ManageNotes(taskId, noteText: $"Note {noteNum}", action: "add").Text)
            );
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(noteCount);
        results.Should().OnlyContain(r => r.Contains("Added note"));

        var notes = _taskManager.ListNotes(taskId).Text;
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
                            results.Add(await Task.Run(() => _taskManager.AddTask($"Task {opNum}").Text));
                            break;
                        case 1:
                            results.Add(await Task.Run(() => _taskManager.ListTasks().Text));
                            break;
                        case 2:
                            results.Add(await Task.Run(() => _taskManager.SearchTasks(countType: "total").Text));
                            break;
                        case 3:
                            var bulkTasks = new List<TaskManager.BulkTaskItem> { new() { Task = $"Bulk {opNum}" } };
                            results.Add(await Task.Run(() => _taskManager.BulkInitialize(bulkTasks).Text));
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

        var count = _taskManager.SearchTasks(countType: "total").Text;
        count.Should().Be($"Total tasks: {taskCount}");
    }

    [Fact]
    public void TaskManager_DeepSubtaskHierarchy_ShouldAllowNestedTasks()
    {
        // Arrange
        var parentResult = _taskManager.AddTask("Level 1").Text;
        var parentId = ExtractTaskId(parentResult);
        var subtaskResult = _taskManager.AddTask("Level 2", parentId).Text;
        var subtaskId = ExtractTaskId(subtaskResult);

        // Act
        var result = _taskManager.AddTask("Level 3", $"{parentId}.{subtaskId}").Text;

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
            var result = _taskManager.AddTask(title).Text;
            result.Should().Contain("Added task");
            result.Should().Contain(title.Trim());
        }

        var allTasks = _taskManager.ListTasks().Text;
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
        var addResult = _taskManager.AddTask(longTitle).Text;
        var taskId = ExtractTaskId(addResult);
        var noteResult = _taskManager.ManageNotes(taskId, noteText: longNote, action: "add").Text;

        // Assert
        addResult.Should().Contain("Added task");
        noteResult.Should().Contain("Added note");

        var taskDetails = _taskManager.GetTask(taskId).Text;
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
        _taskManager.ClaimTask("1.1", "tester");
        _taskManager.UpdateTask("1.1", "completed");
        _taskManager.UpdateTask("1.2", "removed");

        // Act
        var result = _taskManager.ListTasks().Text;

        // Assert - LF throughout, so this doubles as the guard against
        // Environment.NewLine leaking CRLF into tool output on Windows. The status line now
        // names blocked tasks explicitly (0 here) rather than folding them silently into
        // "pending" — PR4's coordination fields (Blocked, assignee, blockedBy, elapsed) change
        // this rendering even when a tree, like this one, never touches them. Completing 1.1
        // now requires it to have been claimed first (see Requirement 8.8), and Assignee is
        // durable ownership that survives the Completed transition, so its row carries a
        // "[@tester]" tag. This byte-exact string and
        // docs/features/todo-manager/requirements.md's "Worked Example" must be kept in sync —
        // see the CRITICAL sync note there.
        var expected =
            "# 📋 Task List\n"
            + "\n"
            + "**Status**: 1 in progress | 2 pending | 0 blocked | 1 completed\n"
            + "**Total**: 3 active tasks\n"
            + "\n"
            + "[-] 1. Design API\n"
            + "  Notes:\n"
            + "  1. Rate limit is 100/min\n"
            + "  2. Auth via JWT\n"
            + "  [x] 1.1. Define endpoints [@tester]\n"
            + "    [ ] 1.1.1. Validate JWT\n"
            + "  [~] 1.2. Draft schema (removed)\n"
            + "[ ] 2. Ship it";

        result.Should().Be(expected);
    }

    [Fact]
    public void ListTasks_RendersAssigneeBlockedByAndElapsed_ForTheNewCoordinationFields()
    {
        // Arrange - a fixed clock so the elapsed suffix is deterministic. One tree exercising
        // every new rendering path: an assignee tag, a claimed-and-elapsed in-progress row, and
        // a blocked row naming its blocker.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var manager = new TaskManager(clock);
        manager.AddTask("Wire the SSE endpoint");
        manager.AddTask("Publish the frame");
        manager.ClaimTask("1", "rev-a");
        clock.Advance(TimeSpan.FromMinutes(4));
        manager.BlockTask("2", ["1"]);

        // Act
        var result = manager.ListTasks().Text;

        // Assert
        var expected =
            "# 📋 Task List\n"
            + "\n"
            + "**Status**: 1 in progress | 0 pending | 1 blocked | 0 completed\n"
            + "**Total**: 2 active tasks\n"
            + "\n"
            + "[-] 1. Wire the SSE endpoint [@rev-a] (4m)\n"
            + "[!] 2. Publish the frame (blocked by 1)";

        result.Should().Be(expected);
    }

    #endregion

    #region Coordination Fields Tests (claim/lease, assignee, blocked)

    [Fact]
    public void ClaimTask_OnAnUnclaimedTask_ShouldMoveToInProgressByName()
    {
        // Arrange
        var addResult = _taskManager.AddTask("Write the design doc").Text;
        var taskId = ExtractTaskId(addResult).ToString();

        // Act
        var result = _taskManager.ClaimTask(taskId, "rev-a");

        // Assert
        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("claimed by rev-a");

        var task = _taskManager.GetTasks().Single(t => t.Id == taskId);
        task.Status.Should().Be(TaskManager.TaskStatus.InProgress);
        task.Assignee.Should().Be("rev-a");
        task.Times.Should().NotBeNull();
        task.Times!.ClaimedAt.Should().NotBeNull();
    }

    [Fact]
    public void ClaimTask_ByADifferentAgentWhileFresh_IsRefused()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var manager = new TaskManager(clock);
        manager.AddTask("Ship the release");
        manager.ClaimTask("1", "rev-a");
        clock.Advance(TimeSpan.FromMinutes(5)); // well under the 15-minute default lease

        // Act
        var result = manager.ClaimTask("1", "rev-b");

        // Assert - a fresh lease is a real lock, not a takeover target
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("task_already_claimed");
        var task = manager.GetTasks().Single();
        task.Assignee.Should().Be("rev-a");
    }

    [Fact]
    public void ClaimTask_ByADifferentAgentAfterTheLeaseGoesStale_TakesItOver()
    {
        // Arrange - this is the "claim is a lease, not a hard lock" invariant: the design
        // explicitly rejects a permanent lock so a crashed/abandoned agent cannot wedge a task
        // forever. Default staleness is 15 minutes (TaskManager.DefaultLeaseStaleAfter).
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var manager = new TaskManager(clock);
        manager.AddTask("Ship the release");
        manager.ClaimTask("1", "rev-a");
        clock.Advance(TimeSpan.FromMinutes(16)); // past the default 15-minute lease

        // Act
        var result = manager.ClaimTask("1", "rev-b");

        // Assert
        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Took over a stale lease from rev-a");
        var task = manager.GetTasks().Single();
        task.Assignee.Should().Be("rev-b");
        task.Status.Should().Be(TaskManager.TaskStatus.InProgress);
    }

    [Fact]
    public void ClaimTask_AtExactlyTheStaleThreshold_IsStillLive_AndOneTickLaterIsStale()
    {
        // Arrange - Requirement 8.3 says a lease is stale once it is "older than" the default
        // 15-minute threshold, so a claim exactly 15 minutes old is still live and only a claim
        // past 15 minutes may be taken over. The 5m/16m tests above don't sit on this boundary;
        // this one pins the exact distinguishing case (14.999... vs. 15.000...+epsilon minutes).
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var manager = new TaskManager(clock);
        manager.AddTask("Ship the release");
        manager.ClaimTask("1", "rev-a");

        // Act - exactly at the threshold
        clock.Advance(TimeSpan.FromMinutes(15));
        var atThreshold = manager.ClaimTask("1", "rev-b");

        // Assert - still live
        atThreshold.IsError.Should().BeTrue();
        atThreshold.ErrorCode.Should().Be("task_already_claimed");
        manager.GetTasks().Single().Assignee.Should().Be("rev-a");

        // Act - one tick past the threshold
        clock.Advance(TimeSpan.FromTicks(1));
        var pastThreshold = manager.ClaimTask("1", "rev-b");

        // Assert - now stale
        pastThreshold.IsError.Should().BeFalse();
        manager.GetTasks().Single().Assignee.Should().Be("rev-b");
    }

    [Fact]
    public void ClaimTask_ASecondTaskForTheSameAgent_ReleasesTheFirstBackToNotStarted()
    {
        // Arrange - the one-in-progress-per-assignee invariant: an agent claiming a second
        // task is not allowed to have two active leases at once.
        _taskManager.AddTask("First task");
        _taskManager.AddTask("Second task");
        _taskManager.ClaimTask("1", "rev-a");

        // Act
        var result = _taskManager.ClaimTask("2", "rev-a");

        // Assert
        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Released task 1 back to 'not started'");

        var tasks = _taskManager.GetTasks();
        var first = tasks.Single(t => t.Id == "1");
        var second = tasks.Single(t => t.Id == "2");

        first.Status.Should().Be(TaskManager.TaskStatus.NotStarted);
        first.Assignee.Should().Be("rev-a"); // ownership persists even though it is no longer active
        second.Status.Should().Be(TaskManager.TaskStatus.InProgress);
        second.Assignee.Should().Be("rev-a");
    }

    [Fact]
    public void BlockTask_SetsStatusAndBlockedBy_AndRefusesAClaimUntilResolved()
    {
        // Arrange
        _taskManager.AddTask("The blocker");
        _taskManager.AddTask("The dependent");

        // Act
        var blockResult = _taskManager.BlockTask("2", ["1"]);
        var claimAttempt = _taskManager.ClaimTask("2", "rev-a");

        // Assert
        blockResult.IsError.Should().BeFalse();
        var dependent = _taskManager.GetTasks().Single(t => t.Id == "2");
        dependent.Status.Should().Be(TaskManager.TaskStatus.Blocked);
        dependent.BlockedBy.Should().ContainSingle().Which.Should().Be("1");

        claimAttempt.IsError.Should().BeTrue();
        claimAttempt.ErrorCode.Should().Be("task_blocked");
    }

    [Fact]
    public void GetTodoBoardSnapshot_ForABlockedTask_CarriesTodoTaskStatusBlocked()
    {
        // Pins ToBoardNode's status mapping for the newest TaskStatus member: a mutation that
        // maps Blocked to any other TodoTaskStatus (e.g. InProgress) must turn this test red, since
        // no other Misc.Tests test exercises GetTodoBoardSnapshot at all.
        _taskManager.AddTask("The blocker");
        _taskManager.AddTask("The dependent");
        _taskManager.BlockTask("2", ["1"]);

        var snapshot = _taskManager.GetTodoBoardSnapshot("thread-1");

        var dependent = snapshot.Tasks.Single(t => t.Id == "2");
        dependent.Status.Should().Be(TodoTaskStatus.Blocked);
    }

    [Fact]
    public void UpdateTask_ToInProgressWithNoAgent_CannotBypassAnUnresolvedBlock()
    {
        // Arrange - F-001: the agentless 'in progress' transition (agent == null) does not go
        // through ApplyClaim, so before this fix it inherited none of ApplyClaim's guards. This
        // reproduces the reported back door: block -> assign -> "in progress" (no agent) ->
        // "completed" must not be able to reach Completed while blockedBy is unresolved.
        _taskManager.AddTask("The blocker");
        _taskManager.AddTask("The dependent");
        _taskManager.BlockTask("2", ["1"]);
        _taskManager.AssignTask("2", "rev-a");

        // Act
        var inProgressResult = _taskManager.UpdateTask("2", "in progress");

        // Assert - refused, not silently accepted
        inProgressResult.IsError.Should().BeTrue();
        inProgressResult.ErrorCode.Should().Be("task_blocked");

        var dependent = _taskManager.GetTasks().Single(t => t.Id == "2");
        dependent.Status.Should().Be(TaskManager.TaskStatus.Blocked);

        // And completion is still unreachable, since Status never left Blocked.
        var completeResult = _taskManager.UpdateTask("2", "completed");
        completeResult.IsError.Should().BeTrue();
    }

    [Fact]
    public void ClaimTask_RefreshOnABlockedTask_IsRefused()
    {
        // Arrange - a holder cannot refresh their own claim once the task has been blocked out
        // from under them; the block review flagged that ClaimTask's same-holder refresh fast
        // path checked only Status == InProgress and the agent name, skipping the blockedBy
        // guard ApplyClaim otherwise enforces. block-task always flips Status to Blocked in the
        // same call it records blockedBy, so this scenario currently reaches the ApplyClaim path
        // rather than the fast path — the fast path's own RefuseIfBlocked call added alongside
        // this fix is defense-in-depth against that invariant changing later, not something a
        // reachable input can currently exercise directly.
        _taskManager.AddTask("The blocker");
        _taskManager.AddTask("The dependent");
        _taskManager.ClaimTask("2", "rev-a");
        _taskManager.BlockTask("2", ["1"]);

        // Act - same agent, same task, but it is now Blocked
        var result = _taskManager.ClaimTask("2", "rev-a");

        // Assert
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("task_blocked");
    }

    [Fact]
    public void CompletingABlocker_AutoUnblocksItsDependents()
    {
        // Arrange
        _taskManager.AddTask("The blocker");
        _taskManager.AddTask("The dependent");
        _taskManager.BlockTask("2", ["1"]);
        _taskManager.ClaimTask("1", "rev-a");

        // Act
        var completeResult = _taskManager.UpdateTask("1", "completed");

        // Assert
        completeResult.IsError.Should().BeFalse();
        completeResult.Text.Should().Contain("Unblocked: 2");

        var dependent = _taskManager.GetTasks().Single(t => t.Id == "2");
        dependent.Status.Should().Be(TaskManager.TaskStatus.NotStarted);
        dependent.BlockedBy.Should().BeEmpty();
    }

    [Fact]
    public void UpdateTask_ToBlockedDirectly_IsRefusedAndPointsAtBlockTask()
    {
        // Arrange
        _taskManager.AddTask("Some task");

        // Act
        var result = _taskManager.UpdateTask("1", "blocked");

        // Assert
        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("block-task");

        var task = _taskManager.GetTasks().Single();
        task.Status.Should().Be(TaskManager.TaskStatus.NotStarted);
    }

    [Fact]
    public void UpdateTask_ToCompletedWithoutAnActiveClaim_IsRefused()
    {
        // Arrange - a task that is both NotStarted and unassigned; either half of the
        // completion gate (Requirement 8.8) alone would already refuse this, which is exactly
        // why it does not distinguish the two conjuncts the review flagged (F-003) — the two
        // tests below each isolate one conjunct so a mutation dropping just that half turns red.
        _taskManager.AddTask("Never claimed");

        // Act
        var result = _taskManager.UpdateTask("1", "completed");

        // Assert
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("task_not_claimed");
    }

    [Fact]
    public void UpdateTask_ToCompletedWhileInProgressButUnassigned_IsRefused()
    {
        // Arrange - F-003 (M2): isolates the "Assignee == null" conjunct. The agentless
        // "in progress" transition never sets Assignee, so InProgress-but-unassigned is
        // reachable through the public API without any lease ever having existed. A mutation
        // that drops the Assignee-null check from the completion gate stays green against
        // UpdateTask_ToCompletedWithoutAnActiveClaim_IsRefused above (that task is also
        // NotStarted) but must turn red here, since Status alone is already InProgress.
        _taskManager.AddTask("Never assigned");
        _taskManager.UpdateTask("1", "in progress");

        // Act
        var result = _taskManager.UpdateTask("1", "completed");

        // Assert
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("task_not_claimed");
    }

    [Fact]
    public void UpdateTask_ToCompletedWhileAssignedButNotInProgress_IsRefused()
    {
        // Arrange - F-003 (M3): isolates the "Status != InProgress" conjunct. assign-task sets
        // Assignee without touching Status, so NotStarted-but-assigned is reachable through the
        // public API. A mutation that drops the Status check from the completion gate stays
        // green against UpdateTask_ToCompletedWithoutAnActiveClaim_IsRefused above (that task is
        // also unassigned) but must turn red here, since Assignee alone is already non-null.
        _taskManager.AddTask("Assigned but never claimed");
        _taskManager.AssignTask("1", "rev-a");

        // Act
        var result = _taskManager.UpdateTask("1", "completed");

        // Assert
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("task_not_claimed");
    }

    [Fact]
    public void AddTask_TimestampsCreatedAtFromTheInjectedClock()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        var manager = new TaskManager(clock);

        // Act
        manager.AddTask("Timestamped task");

        // Assert
        var task = manager.GetTasks().Single();
        task.Times.Should().NotBeNull();
        task.Times!.CreatedAt.Should().Be(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        task.Times.ClaimedAt.Should().BeNull();
        task.Times.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void FullLifecycle_StampsCreatedClaimedAndCompletedFromTheInjectedClock()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        var manager = new TaskManager(clock);
        manager.AddTask("Lifecycle task");

        clock.Advance(TimeSpan.FromMinutes(2));
        manager.ClaimTask("1", "rev-a");

        clock.Advance(TimeSpan.FromMinutes(3));
        manager.UpdateTask("1", "completed");

        // Act
        var task = manager.GetTasks().Single();

        // Assert
        task.Times.Should().NotBeNull();
        task.Times!.CreatedAt.Should().Be(DateTimeOffset.Parse("2026-03-01T12:00:00Z"));
        task.Times.ClaimedAt.Should().Be(DateTimeOffset.Parse("2026-03-01T12:02:00Z"));
        task.Times.CompletedAt.Should().Be(DateTimeOffset.Parse("2026-03-01T12:05:00Z"));
    }

    [Fact]
    public void AddTask_UnderAnAssignedParent_InheritsTheAssigneeUnlessOverridden()
    {
        // Arrange
        _taskManager.AddTask("Parent task", parentId: null, assignee: "rev-a");

        // Act
        _taskManager.AddTask("Inherits", "1");
        _taskManager.AddTask("Overrides", "1", assignee: "rev-b");

        // Assert
        var tasks = _taskManager.GetTasks();
        var parent = tasks.Single(t => t.Id == "1");
        var inherited = parent.SubTasks.Single(t => t.Id == "1.1");
        var overridden = parent.SubTasks.Single(t => t.Id == "1.2");

        parent.Assignee.Should().Be("rev-a");
        inherited.Assignee.Should().Be("rev-a");
        overridden.Assignee.Should().Be("rev-b");
    }

    [Fact]
    public void AssignTask_SetsAssigneeWithoutTouchingStatus()
    {
        // Arrange
        _taskManager.AddTask("Dispatch me");

        // Act
        var result = _taskManager.AssignTask("1", "rev-a");

        // Assert
        result.IsError.Should().BeFalse();
        var task = _taskManager.GetTasks().Single();
        task.Assignee.Should().Be("rev-a");
        task.Status.Should().Be(TaskManager.TaskStatus.NotStarted);
    }

    [Fact]
    public void AssignTask_OverALiveForeignClaim_IsRefusedRatherThanSilentlyTransferred()
    {
        // Arrange - F-002 instance A: assign-task sat outside every claim invariant, so
        // reassigning an InProgress task silently transferred the lease even while it was still
        // fresh — the new assignee could then complete it having never claimed anything, at the
        // exact moment claim-task would correctly refuse (task_already_claimed).
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var manager = new TaskManager(clock);
        manager.AddTask("Ship the release");
        manager.ClaimTask("1", "rev-a");
        clock.Advance(TimeSpan.FromMinutes(1)); // well under the 15-minute default lease

        // Act
        var result = manager.AssignTask("1", "rev-b");

        // Assert - refused, and the live claim is untouched
        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("task_already_claimed");

        var task = manager.GetTasks().Single();
        task.Assignee.Should().Be("rev-a");
        task.Status.Should().Be(TaskManager.TaskStatus.InProgress);
    }

    [Fact]
    public void AssignTask_OverAStaleForeignClaim_ReleasesToNotStartedRatherThanStayingInProgress()
    {
        // Arrange - once the lease is actually stale, assign-task may hand the task to a new
        // assignee, but it must not do so by leaving Status == InProgress under a name that
        // never claimed it (that would recreate the exact "complete without ever claiming" hole
        // F-002 closed for the live case, just past the staleness line). Assignment never
        // advances a task into InProgress; the new assignee must still claim-task it.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var manager = new TaskManager(clock);
        manager.AddTask("Ship the release");
        manager.ClaimTask("1", "rev-a");
        clock.Advance(TimeSpan.FromMinutes(16)); // past the default 15-minute lease

        // Act
        var result = manager.AssignTask("1", "rev-b");

        // Assert
        result.IsError.Should().BeFalse();
        var task = manager.GetTasks().Single();
        task.Assignee.Should().Be("rev-b");
        task.Status.Should().Be(TaskManager.TaskStatus.NotStarted);

        // And rev-b cannot complete it without an explicit claim.
        var completeAttempt = manager.UpdateTask("1", "completed");
        completeAttempt.IsError.Should().BeTrue();
        completeAttempt.ErrorCode.Should().Be("task_not_claimed");
    }

    [Fact]
    public void AssignTask_NeverLeavesOneAssigneeHoldingTwoInProgressTasks()
    {
        // Arrange - F-002 instance B: rev-a and rev-b each hold their own live InProgress task;
        // reassigning rev-b's task to rev-a must not silently give rev-a two active tasks at
        // once (Requirement 8.4), the way it did before assign-task respected the lease.
        _taskManager.AddTask("First task");
        _taskManager.AddTask("Second task");
        _taskManager.ClaimTask("1", "rev-a");
        _taskManager.ClaimTask("2", "rev-b");

        // Act
        var result = _taskManager.AssignTask("2", "rev-a");

        // Assert - refused outright (rev-b's lease on task 2 is fresh)
        result.IsError.Should().BeTrue();

        var tasks = _taskManager.GetTasks();
        var inProgressForRevA = tasks.Count(t =>
            t.Status == TaskManager.TaskStatus.InProgress && t.Assignee == "rev-a"
        );
        inProgressForRevA.Should().Be(1);
    }

    [Fact]
    public void DeserializeTasks_LoadsOldShapeJsonMissingCoordinationFields()
    {
        // Arrange - this is the exact shape TaskManager persisted before assignee/blockedBy/
        // times existed: no such properties at all, not even null placeholders.
        const string oldShapeJson = """
            {
              "rootTasks": [
                {
                  "id": 1,
                  "displayId": "1",
                  "title": "Pre-existing task",
                  "status": "NotStarted",
                  "notes": [],
                  "subTasks": [],
                  "nextSubTaskId": 1
                }
              ],
              "nextId": 2
            }
            """;

        // Act
        var manager = TaskManager.DeserializeTasks(oldShapeJson);

        // Assert
        var task = manager.GetTasks().Single();
        task.Title.Should().Be("Pre-existing task");
        task.Assignee.Should().BeNull();
        task.BlockedBy.Should().BeEmpty();
        task.Times.Should().BeNull();

        // And it keeps working going forward under the new rules.
        var claim = manager.ClaimTask("1", "rev-a");
        claim.IsError.Should().BeFalse();
    }

    [Fact]
    public void RoundTrip_SerializeThenDeserialize_PreservesCoordinationFields()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var manager = new TaskManager(clock);
        manager.AddTask("Blocker");
        manager.AddTask("Dependent");
        manager.ClaimTask("1", "rev-a");
        manager.BlockTask("2", ["1"]);

        // Act
        var json = manager.JsonSerializeTasks();
        var roundTripped = TaskManager.DeserializeTasks(json, clock);

        // Assert
        var tasks = roundTripped.GetTasks();
        var blocker = tasks.Single(t => t.Id == "1");
        var dependent = tasks.Single(t => t.Id == "2");

        blocker.Assignee.Should().Be("rev-a");
        blocker.Status.Should().Be(TaskManager.TaskStatus.InProgress);
        blocker.Times.Should().NotBeNull();
        blocker.Times!.CreatedAt.Should().NotBeNull();
        blocker.Times.ClaimedAt.Should().NotBeNull();

        dependent.Status.Should().Be(TaskManager.TaskStatus.Blocked);
        dependent.BlockedBy.Should().ContainSingle().Which.Should().Be("1");
    }

    #endregion

    #region GetMarkdown Tests

    [Fact]
    public void GetMarkdown_ShouldReturnSameAsListTasks()
    {
        // Arrange
        _taskManager.AddTask("Task 1");
        _taskManager.AddTask("Task 2");

        // Act
        var markdown = _taskManager.GetMarkdown();
        var listTasks = _taskManager.ListTasks().Text;

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
        var result = _taskManager.AddNote("1.1.1", noteText: "Deep note").Text;

        // Assert
        result.Should().Be("Added note to task 1.1.1.");
        _taskManager.ListNotes("1.1.1").Text.Should().Contain("Deep note");
    }

    [Fact]
    public void GetTask_AtDepthThree_ShouldReturnDetails()
    {
        // Arrange
        _taskManager.AddTask("Level 1");
        _taskManager.AddTask("Level 2", "1");
        _taskManager.AddTask("Level 3", "1.1");

        // Act
        var result = _taskManager.GetTask("1.1.1").Text;

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
        var result = _taskManager.DeleteTask("1.1.1").Text;

        // Assert - removing only from RootTasks would report success and change nothing.
        result.Should().Contain("Deleted task 1.1.1 and all subtasks");
        _taskManager.ListTasks().Text.Should().NotContain("Level 3");

        // Positive control. Under-deletion is only half the failure mode: detaching the root
        // ancestor instead of the target satisfies every negative assertion above. The
        // ancestors must survive.
        _taskManager.ListTasks().Text.Should().Contain("Level 1").And.Contain("Level 2");
        _taskManager.GetTask("1.1").Text.Should().Contain("Task 1.1: Level 2");

        // Exactly which absence this is matters: a substring match cannot tell "the leaf is
        // gone" from "the tree is gone".
        _taskManager.GetTask("1.1.1").Text.Should().Be("Error: Task '1.1.1' not found.");
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
        var edited = _taskManager.EditNote("1.1.1", noteIndex: 1, noteText: "Revised").Text;
        var listed = _taskManager.ListNotes("1.1.1").Text;
        var deleted = _taskManager.DeleteNote("1.1.1", noteIndex: 1).Text;

        // Assert
        edited.Should().Be("Updated note #1 on task 1.1.1.");
        listed.Should().Contain("Revised");
        deleted.Should().Contain("Deleted note #1 from task 1.1.1");
        _taskManager.ListNotes("1.1.1").Text.Should().Be("task 1.1.1 has no notes.");
    }

    #endregion

    #region Input Tolerance Tests

    [Fact]
    public void AddTask_WithBlankParentId_ShouldReturnErrorRatherThanCreateRootTask()
    {
        // Act - a supplied-but-blank parentId is a malformed call, not "no parent".
        var result = _taskManager.AddTask("Orphan", "   ").Text;

        // Assert
        result.Should().Be("Error: Parent task ID cannot be blank. Omit parentId to add a main task.");
        _taskManager.ListTasks().Text.Should().NotContain("Orphan");
    }

    [Fact]
    public void AddTask_WithOmittedParentId_ShouldStillCreateRootTask()
    {
        // Act
        var result = _taskManager.AddTask("Main").Text;

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
        var result = _taskManager.UpdateTask("1", status: input).Text;

        // Assert
        result.Should().Contain($"status to '{expected}'");
    }

    #endregion

    #region Concurrency Tests

    /// <summary>
    ///     Pins the <c>lock (_sync)</c> on every read path that touches a <em>nested</em>
    ///     SubTasks list: ListTasks (and the GetMarkdown that delegates to it), SearchTasks,
    ///     the GetTaskCounts branch of SearchTasks, and GetTask.
    ///     <para>
    ///         It discriminates by construction, and each of the three requirements matters:
    ///         the writer nests (<c>AddTask(title, "1")</c>) so it appends to a nested list
    ///         rather than to the root list; that same nested list is pre-seeded so a reader
    ///         spends real time inside it; and the readers are exactly the methods that
    ///         traverse it. Drop any one of those four locks and this test fails within a
    ///         handful of iterations — <c>GetAllTasksFlat</c>'s bare <c>foreach</c> throws
    ///         <see cref="InvalidOperationException" /> off the list's version stamp, and a
    ///         <c>[.. list]</c> copy of a concurrently grown list throws
    ///         <see cref="ArgumentException" /> from a Count that no longer matches the CopyTo.
    ///     </para>
    ///     <para>
    ///         GetTask is driven too, but for its own lock rather than its lookup: the lookup
    ///         is already covered because <c>FindTaskByStringId</c> takes <c>_sync</c> itself,
    ///         while <c>FormatTaskDetails</c> runs inside GetTask's lock and copies the target's
    ///         SubTasks list. Reading task "1" — the one the writer is growing — is what makes
    ///         that copy race.
    ///     </para>
    ///     <para>
    ///         The two JSON serializers are not driven here. Their locks are real and their
    ///         removal is observable, but not as an exception: see
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
            _ = _taskManager.AddTask($"Seed {i}").Text;
        }

        for (var i = 0; i < NestedSeedCount; i++)
        {
            _ = _taskManager.AddTask($"Nested {i}", "1").Text;
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
                _ = _taskManager.AddTask($"Churn {i++}", "1").Text;
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
                            _ = _taskManager.ListTasks().Text;
                            _ = _taskManager.SearchTasks("Nested").Text;
                            _ = _taskManager.SearchTasks(countType: "total").Text;
                            _ = _taskManager.GetTask("1").Text;
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
        // nextId.
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
                _ = manager.AddTask($"Seed {i}").Text;
            }

            for (var i = 0; i < NestedSeedCount; i++)
            {
                _ = manager.AddTask($"Nested {i}", "1").Text;
            }

            using var startingGun = new ManualResetEventSlim(false);
            var stop = 0;

            var writer = Task.Run(() =>
            {
                startingGun.Wait();
                var i = 0;
                while (Volatile.Read(ref stop) == 0 && i < WriterCapPerRound)
                {
                    _ = manager.AddTask($"Churn {i++}", "1").Text;
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

    #region Error Signalling Tests

    /// <summary>
    ///     The claim this whole change exists to make: a domain failure must not arrive at the
    ///     model wearing the same "succeeded" flag as a real answer. The assertion is made on the
    ///     handler's payload rather than on <see cref="TaskManager" /> directly, because the flag
    ///     the model sees is set by the reflective handler, not by the method.
    /// </summary>
    [Fact]
    public async Task ToolHandler_ForAMissingTask_ReportsAFailureNotASuccessfulString()
    {
        var getTask = new TypeFunctionProvider(new TaskManager())
            .GetFunctions()
            .First(f => f.Contract.Name == "get-task");

        var result = await getTask.Handler("""{"taskId":"999"}""", new ToolCallContext(), CancellationToken.None);

        var resolved = Assert.IsType<ToolHandlerResult.Resolved>(result);
        resolved.Payload.IsError.Should().BeTrue();
        resolved.Payload.ErrorCode.Should().Be("task_not_found");
        // The wire shape is unchanged: still the same bare JSON string it always was.
        JsonSerializer.Deserialize<string>(resolved.Payload.Text).Should().Be("Error: Task '999' not found.");
    }

    /// <summary>
    ///     The other half of the claim. Marking failures is only informative if successes stay
    ///     unmarked, so a passing call must still carry no error code.
    /// </summary>
    [Fact]
    public async Task ToolHandler_ForASuccessfulCall_StaysASuccess()
    {
        var addTask = new TypeFunctionProvider(new TaskManager())
            .GetFunctions()
            .First(f => f.Contract.Name == "add-task");

        var result = await addTask.Handler("""{"title":"Test task"}""", new ToolCallContext(), CancellationToken.None);

        var resolved = Assert.IsType<ToolHandlerResult.Resolved>(result);
        resolved.Payload.IsError.Should().BeFalse();
        resolved.Payload.ErrorCode.Should().BeNull();
        JsonSerializer.Deserialize<string>(resolved.Payload.Text).Should().Be("Added task 1: Test task");
    }

    /// <summary>
    ///     Every tool, not just the one the end-to-end test drives. A tool left on a bare string
    ///     keeps delivering its failures as successes, and nothing else here would notice.
    /// </summary>
    [Theory]
    [InlineData("add-task", "invalid_args")]
    [InlineData("bulk-initialize", "invalid_args")]
    [InlineData("update-task", "invalid_status")]
    [InlineData("delete-task", "task_not_found")]
    [InlineData("get-task", "task_not_found")]
    [InlineData("add-note", "task_not_found")]
    [InlineData("edit-note", "note_index_out_of_range")]
    [InlineData("delete-note", "note_index_out_of_range")]
    [InlineData("list-notes", "task_not_found")]
    [InlineData("list-tasks", "invalid_status")]
    [InlineData("search-tasks", "invalid_args")]
    [InlineData("assign-task", "invalid_args")]
    [InlineData("claim-task", "invalid_args")]
    [InlineData("block-task", "task_not_found")]
    public void EveryTool_ReportsItsDomainFailureWithACode(string tool, string expectedErrorCode)
    {
        var manager = SeededManager();

        var result = InvokeFailing(manager, tool);

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedErrorCode);
        result.Text.Should().StartWith("Error: ");
    }

    /// <summary>
    ///     Non-vacuity control for the theory above. Marking failures says nothing unless the
    ///     same tools leave their successes unmarked — an implementation that flagged everything
    ///     would satisfy the failure cases on its own.
    /// </summary>
    [Theory]
    [InlineData("add-task")]
    [InlineData("bulk-initialize")]
    [InlineData("update-task")]
    [InlineData("delete-task")]
    [InlineData("get-task")]
    [InlineData("add-note")]
    [InlineData("edit-note")]
    [InlineData("delete-note")]
    [InlineData("list-notes")]
    [InlineData("list-tasks")]
    [InlineData("search-tasks")]
    [InlineData("assign-task")]
    [InlineData("claim-task")]
    [InlineData("block-task")]
    public void EveryTool_LeavesItsSuccessUnmarked(string tool)
    {
        var manager = SeededManager();

        var result = InvokeSucceeding(manager, tool);

        result.IsError.Should().BeFalse();
        result.ErrorCode.Should().BeNull();
    }

    /// <summary>
    ///     The two finders sit under every tool above, and they are the only place a malformed
    ///     id is told apart from an id that simply names nothing.
    /// </summary>
    [Theory]
    [InlineData("abc", "invalid_task_id")]
    [InlineData("1.x", "invalid_task_id")]
    [InlineData("999", "task_not_found")]
    [InlineData("1.9", "task_not_found")]
    public void TheFinders_DistinguishAMalformedIdFromAMissingOne(string taskId, string expectedErrorCode)
    {
        var manager = SeededManager();

        var result = manager.GetTask(taskId);

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be(expectedErrorCode);
    }

    /// <summary>
    ///     <c>ManageNotes</c> rewords what the note tools return. Rewording the text through a
    ///     plain string would drop the code and hand the model a reworded failure marked success.
    /// </summary>
    [Fact]
    public void ManageNotes_RewordingAFailure_KeepsItAFailure()
    {
        var manager = SeededManager();

        var result = manager.ManageNotes("1", noteIndex: 9, noteText: "x", action: "edit");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("note_index_out_of_range");
    }

    [Fact]
    public void ManageNotes_WithAnUnknownAction_ReportsAFailure()
    {
        var result = SeededManager().ManageNotes("1", action: "sideways");

        result.IsError.Should().BeTrue();
        result.ErrorCode.Should().Be("invalid_action");
    }

    /// <summary>
    ///     Only <c>Text</c> reaches the wire, so the contract rendered into the system prompt has
    ///     to keep naming <see cref="string" />. Naming the wrapper would describe a shape the
    ///     model never receives.
    /// </summary>
    [Fact]
    public void EveryTool_AdvertisesTheStringItPutsOnTheWire()
    {
        var functions = new TypeFunctionProvider(new TaskManager()).GetFunctions().ToList();

        // The original eleven, plus assign-task, claim-task and block-task from PR4's
        // coordination fields.
        functions.Should().HaveCount(14);
        functions.Should().OnlyContain(f => f.Contract.ReturnType == typeof(string));
    }

    private static TaskManager SeededManager()
    {
        var manager = new TaskManager();
        _ = manager.AddTask("Seed task");
        _ = manager.AddNote("1", noteText: "Seed note");
        return manager;
    }

    private static FunctionResult InvokeFailing(TaskManager manager, string tool)
    {
        return tool switch
        {
            "add-task" => manager.AddTask(string.Empty),
            "bulk-initialize" => manager.BulkInitialize([]),
            "update-task" => manager.UpdateTask("1", "sideways"),
            "delete-task" => manager.DeleteTask("999"),
            "get-task" => manager.GetTask("999"),
            "add-note" => manager.AddNote("999", noteText: "text"),
            "edit-note" => manager.EditNote("1", noteIndex: 9, noteText: "text"),
            "delete-note" => manager.DeleteNote("1", noteIndex: 9),
            "list-notes" => manager.ListNotes("999"),
            "list-tasks" => manager.ListTasks("sideways"),
            "search-tasks" => manager.SearchTasks(),
            "assign-task" => manager.AssignTask("1", string.Empty),
            "claim-task" => manager.ClaimTask("1", string.Empty),
            "block-task" => manager.BlockTask("1", ["999"]),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "unknown tool"),
        };
    }

    private static FunctionResult InvokeSucceeding(TaskManager manager, string tool)
    {
        return tool switch
        {
            "add-task" => manager.AddTask("Another task"),
            "bulk-initialize" => manager.BulkInitialize([new TaskManager.BulkTaskItem { Task = "Bulk task" }]),
            "update-task" => ClaimThenComplete(manager),
            "delete-task" => manager.DeleteTask("1"),
            "get-task" => manager.GetTask("1"),
            "add-note" => manager.AddNote("1", noteText: "text"),
            "edit-note" => manager.EditNote("1", noteIndex: 1, noteText: "text"),
            "delete-note" => manager.DeleteNote("1", noteIndex: 1),
            "list-notes" => manager.ListNotes("1"),
            "list-tasks" => manager.ListTasks(),
            "search-tasks" => manager.SearchTasks("Seed"),
            "assign-task" => manager.AssignTask("1", "rev-a"),
            "claim-task" => manager.ClaimTask("1", "rev-a"),
            "block-task" => BlockOnASecondTask(manager),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "unknown tool"),
        };
    }

    /// <summary>
    ///     update-task can no longer complete a task that was never claimed (PR4's claim
    ///     discipline), so its success path here has to claim first.
    /// </summary>
    private static FunctionResult ClaimThenComplete(TaskManager manager)
    {
        _ = manager.ClaimTask("1", "tester");
        return manager.UpdateTask("1", "completed");
    }

    private static FunctionResult BlockOnASecondTask(TaskManager manager)
    {
        _ = manager.AddTask("Blocker");
        return manager.BlockTask("1", ["2"]);
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

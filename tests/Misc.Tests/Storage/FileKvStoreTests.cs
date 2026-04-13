using System.IO;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Misc.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Storage;

[TestClass]
public class FileKvStoreTests
{
    private string _testCacheDirectory = null!;
    private FileKvStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a unique test directory for each test
        _testCacheDirectory = Path.Combine(Path.GetTempPath(), "FileKvStoreTests", Guid.NewGuid().ToString());
        _store = new FileKvStore(_testCacheDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _store?.Dispose();

        // Clean up test directory
        if (Directory.Exists(_testCacheDirectory))
        {
            try
            {
                Directory.Delete(_testCacheDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #region Constructor Tests

    [TestMethod]
    public void Constructor_WithValidDirectory_CreatesDirectory()
    {
        // Arrange
        var testDir = Path.Combine(Path.GetTempPath(), "FileKvStoreTest_New", Guid.NewGuid().ToString());

        // Act
        using var store = new FileKvStore(testDir);

        // Assert
        Assert.IsTrue(Directory.Exists(testDir));
        Assert.AreEqual(Path.GetFullPath(testDir), store.CacheDirectory);

        // Cleanup
        Directory.Delete(testDir, recursive: true);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void Constructor_WithEmptyDirectory_ThrowsArgumentException()
    {
        // Act & Assert
        using var store = new FileKvStore("");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void Constructor_WithNullDirectory_ThrowsArgumentException()
    {
        // Act & Assert
        using var store = new FileKvStore(null!);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void Constructor_WithWhitespaceDirectory_ThrowsArgumentException()
    {
        // Act & Assert
        using var store = new FileKvStore("   ");
    }

    [TestMethod]
    public void Constructor_WithCustomJsonOptions_UsesOptions()
    {
        // Arrange
        var customOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        var testDir = Path.Combine(Path.GetTempPath(), "FileKvStoreTest_CustomJson", Guid.NewGuid().ToString());

        // Act & Assert (no exception should be thrown)
        using var store = new FileKvStore(testDir, customOptions);

        // Cleanup
        Directory.Delete(testDir, recursive: true);
    }

    #endregion

    #region Get/Set Basic Tests

    [TestMethod]
    public async Task SetAsync_WithValidKeyAndValue_StoresValue()
    {
        // Arrange
        const string key = "test_key";
        const string value = "test_value";

        // Act
        await _store.SetAsync(key, value);

        // Assert
        var retrievedValue = await _store.GetAsync<string>(key);
        Assert.AreEqual(value, retrievedValue);
    }

    [TestMethod]
    public async Task GetAsync_WithNonExistentKey_ReturnsDefault()
    {
        // Arrange
        const string key = "non_existent_key";

        // Act
        var result = await _store.GetAsync<string>(key);

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SetAsync_WithComplexObject_SerializesAndDeserializesCorrectly()
    {
        // Arrange
        var testObject = new TestData
        {
            Id = 123,
            Name = "Test Object",
            Values = new List<string> { "value1", "value2", "value3" },
            CreatedAt = DateTime.UtcNow,
        };

        // Act
        await _store.SetAsync("complex_object", testObject);
        var retrievedObject = await _store.GetAsync<TestData>("complex_object");

        // Assert
        Assert.IsNotNull(retrievedObject);
        Assert.AreEqual(testObject.Id, retrievedObject.Id);
        Assert.AreEqual(testObject.Name, retrievedObject.Name);
        CollectionAssert.AreEqual(testObject.Values, retrievedObject.Values);
        Assert.AreEqual(testObject.CreatedAt.ToString("O"), retrievedObject.CreatedAt.ToString("O"));
    }

    [TestMethod]
    public async Task SetAsync_OverwriteExistingKey_UpdatesValue()
    {
        // Arrange
        const string key = "overwrite_test";
        const string originalValue = "original";
        const string newValue = "updated";

        // Act
        await _store.SetAsync(key, originalValue);
        await _store.SetAsync(key, newValue);

        // Assert
        var retrievedValue = await _store.GetAsync<string>(key);
        Assert.AreEqual(newValue, retrievedValue);
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public async Task GetAsync_WithEmptyKey_ThrowsArgumentException()
    {
        // Act & Assert
        await _store.GetAsync<string>("");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public async Task GetAsync_WithNullKey_ThrowsArgumentException()
    {
        // Act & Assert
        await _store.GetAsync<string>(null!);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public async Task SetAsync_WithEmptyKey_ThrowsArgumentException()
    {
        // Act & Assert
        await _store.SetAsync("", "value");
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public async Task SetAsync_WithNullKey_ThrowsArgumentException()
    {
        // Act & Assert
        await _store.SetAsync(null!, "value");
    }

    [TestMethod]
    public async Task GetAsync_WithCorruptedFile_ReturnsDefaultAndDeletesFile()
    {
        // Arrange
        const string key = "corrupted_test";
        var filePath = GetExpectedFilePath(key);

        // Create a corrupted JSON file
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "{ invalid json content");

        // Act
        var result = await _store.GetAsync<string>(key);

        // Assert
        Assert.IsNull(result);
        Assert.IsFalse(File.Exists(filePath), "Corrupted file should be deleted");
    }

    #endregion

    #region File Management Tests

    [TestMethod]
    public async Task SetAsync_CreatesFileWithCorrectSHA256Name()
    {
        // Arrange
        const string key = "sha_test_key";
        const string value = "test_value";
        var expectedFilePath = GetExpectedFilePath(key);

        // Act
        await _store.SetAsync(key, value);

        // Assert
        Assert.IsTrue(File.Exists(expectedFilePath), "File should exist at expected SHA256 path");

        var fileContent = await File.ReadAllTextAsync(expectedFilePath);
        Assert.IsFalse(string.IsNullOrEmpty(fileContent));
    }

    [TestMethod]
    public async Task SetAsync_WithDeletedDirectory_RecreatesDirectory()
    {
        // Arrange
        const string key = "directory_test";
        const string value = "test_value";

        // Act
        await _store.SetAsync(key, value);
        Directory.Delete(_testCacheDirectory, recursive: true);
        await _store.SetAsync(key, value);

        // Assert
        Assert.IsTrue(Directory.Exists(_testCacheDirectory));
        var retrievedValue = await _store.GetAsync<string>(key);
        Assert.AreEqual(value, retrievedValue);
    }

    #endregion

    #region Enumeration Tests

    [TestMethod]
    public async Task EnumerateKeysAsync_WithEmptyStore_ReturnsNoKeys()
    {
        // Act
        var keys = new List<string>();
        await foreach (var key in await _store.EnumerateKeysAsync())
        {
            keys.Add(key);
        }

        // Assert
        Assert.AreEqual(0, keys.Count);
    }

    [TestMethod]
    public async Task EnumerateKeysAsync_WithStoredItems_ReturnsAllKeys()
    {
        // Arrange
        var testItems = new Dictionary<string, string>
        {
            { "key1", "value1" },
            { "key2", "value2" },
            { "key3", "value3" },
        };

        foreach (var item in testItems)
        {
            await _store.SetAsync(item.Key, item.Value);
        }

        // Act
        var keys = new List<string>();
        await foreach (var key in await _store.EnumerateKeysAsync())
        {
            keys.Add(key);
        }

        // Assert
        Assert.AreEqual(testItems.Count, keys.Count);

        // Verify each key maps to correct SHA256 hash
        foreach (var originalKey in testItems.Keys)
        {
            var expectedHash = GetSHA256Hash(originalKey);
            Assert.IsTrue(keys.Contains(expectedHash), $"Expected hash {expectedHash} for key {originalKey}");
        }
    }

    #endregion

    #region Utility Methods Tests

    [TestMethod]
    public async Task ClearAsync_RemovesAllFiles()
    {
        // Arrange
        await _store.SetAsync("key1", "value1");
        await _store.SetAsync("key2", "value2");
        await _store.SetAsync("key3", "value3");

        // Act
        await _store.ClearAsync();

        // Assert
        var count = await _store.GetCountAsync();
        Assert.AreEqual(0, count);

        var key1Result = await _store.GetAsync<string>("key1");
        var key2Result = await _store.GetAsync<string>("key2");
        var key3Result = await _store.GetAsync<string>("key3");

        Assert.IsNull(key1Result);
        Assert.IsNull(key2Result);
        Assert.IsNull(key3Result);
    }

    [TestMethod]
    public async Task GetCountAsync_ReturnsCorrectCount()
    {
        // Arrange & Act
        var initialCount = await _store.GetCountAsync();

        await _store.SetAsync("key1", "value1");
        var countAfterOne = await _store.GetCountAsync();

        await _store.SetAsync("key2", "value2");
        await _store.SetAsync("key3", "value3");
        var countAfterThree = await _store.GetCountAsync();

        // Assert
        Assert.AreEqual(0, initialCount);
        Assert.AreEqual(1, countAfterOne);
        Assert.AreEqual(3, countAfterThree);
    }

    [TestMethod]
    public async Task GetCountAsync_WithNonExistentDirectory_ReturnsZero()
    {
        // Arrange
        Directory.Delete(_testCacheDirectory, recursive: true);

        // Act
        var count = await _store.GetCountAsync();

        // Assert
        Assert.AreEqual(0, count);
    }

    #endregion

    #region Disposal Tests

    [TestMethod]
    [ExpectedException(typeof(ObjectDisposedException))]
    public async Task GetAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await _store.GetAsync<string>("test");
    }

    [TestMethod]
    [ExpectedException(typeof(ObjectDisposedException))]
    public async Task SetAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        _store.Dispose();

        // Act & Assert
        await _store.SetAsync("test", "value");
    }

    [TestMethod]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        // Act & Assert (should not throw)
        _store.Dispose();
        _store.Dispose();
        _store.Dispose();
    }

    #endregion

    #region Concurrency Tests

    [TestMethod]
    public async Task ConcurrentOperations_DoNotCorruptData()
    {
        // Arrange
        const int numberOfTasks = 10;
        const int operationsPerTask = 20;

        // Act
        var tasks = Enumerable
            .Range(0, numberOfTasks)
            .Select(taskId =>
                Task.Run(async () =>
                {
                    for (int i = 0; i < operationsPerTask; i++)
                    {
                        var key = $"task_{taskId}_item_{i}";
                        var value = $"value_{taskId}_{i}";

                        await _store.SetAsync(key, value);
                        var retrievedValue = await _store.GetAsync<string>(key);

                        Assert.AreEqual(value, retrievedValue);
                    }
                })
            )
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var finalCount = await _store.GetCountAsync();
        Assert.AreEqual(numberOfTasks * operationsPerTask, finalCount);
    }

    #endregion

    #region Cancellation Tests

    [TestMethod]
    public async Task Operations_WithCancellation_RespectCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        try
        {
            await _store.GetAsync<string>("test", cts.Token);
            Assert.Fail("Expected cancellation exception");
        }
        catch (OperationCanceledException)
        {
            // Expected - TaskCanceledException inherits from OperationCanceledException
        }

        try
        {
            await _store.SetAsync("test", "value", cts.Token);
            Assert.Fail("Expected cancellation exception");
        }
        catch (OperationCanceledException)
        {
            // Expected - TaskCanceledException inherits from OperationCanceledException
        }
    }

    #endregion

    #region Helper Methods

    private string GetExpectedFilePath(string key)
    {
        var hash = GetSHA256Hash(key);
        return Path.Combine(_testCacheDirectory, $"{hash}.json");
    }

    private static string GetSHA256Hash(string input)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    #endregion

    #region Test Data Classes

    public class TestData
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string> Values { get; set; } = new();
        public DateTime CreatedAt { get; set; }
    }

    #endregion
}

using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils;

namespace LmTestUtils.Tests
{
    public class MockHttpHandlerBuilderTests
    {
        [Fact]
        public async Task RespondWithStreamingFile_ShouldReturnSseContent()
        {
            // Arrange
            var tempFile = Path.GetTempFileName();
            var sseContent = """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_123","type":"message","role":"assistant","model":"claude-3-sonnet-20240229"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world!"}}

                event: message_stop
                data: {"type":"message_stop"}

                """;

            await File.WriteAllTextAsync(tempFile, sseContent);

            try
            {
                var handler = MockHttpHandlerBuilder.Create().RespondWithStreamingFile(tempFile).Build();

                var httpClient = new HttpClient(handler);

                // Act
                var response = await httpClient.GetAsync("https://api.anthropic.com/v1/messages");

                // Assert
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoCache);
                Assert.Contains("keep-alive", response.Headers.GetValues("Connection"));

                var content = await response.Content.ReadAsStringAsync();
                Assert.Contains("event: message_start", content);
                Assert.Contains("event: content_block_delta", content);
                Assert.Contains("event: message_stop", content);
                Assert.Contains("Hello", content);
                Assert.Contains("world!", content);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public void GenerateCacheKey_ShouldGenerateSHA256Hash_BasedOnRequestShape()
        {
            // Arrange
            var testRequest1 = new
            {
                model = "claude-3-sonnet-20240229",
                messages = new[] { new { role = "user", content = "Hello" } },
                max_tokens = 100,
            };

            var testRequest2 = new
            {
                model = "claude-3-sonnet-20240229",
                messages = new[] { new { role = "user", content = "Hello" } },
                max_tokens = 100,
            };

            var testRequest3 = new
            {
                model = "claude-3-sonnet-20240229",
                messages = new[] { new { role = "user", content = "Different content" } },
                max_tokens = 100,
            };

            // Create recorded interactions
            var interaction1 = new RecordedInteraction
            {
                SerializedRequest = JsonSerializer.SerializeToElement(testRequest1),
                SerializedResponse = JsonSerializer.SerializeToElement(new { response = "test1" }),
            };

            var interaction2 = new RecordedInteraction
            {
                SerializedRequest = JsonSerializer.SerializeToElement(testRequest2),
                SerializedResponse = JsonSerializer.SerializeToElement(new { response = "test2" }),
            };

            var interaction3 = new RecordedInteraction
            {
                SerializedRequest = JsonSerializer.SerializeToElement(testRequest3),
                SerializedResponse = JsonSerializer.SerializeToElement(new { response = "test3" }),
            };

            // Act - Use reflection to access the private GenerateCacheKey method
            var middlewareType = typeof(MockHttpHandlerBuilder).Assembly.GetType(
                "AchieveAi.LmDotnetTools.LmTestUtils.RecordPlaybackMiddleware"
            );
            var middleware = Activator.CreateInstance(middlewareType!, "test.json");
            var method = middlewareType!.GetMethod(
                "GenerateCacheKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );

            var key1 = (string)method!.Invoke(middleware, [interaction1])!;
            var key2 = (string)method!.Invoke(middleware, [interaction2])!;
            var key3 = (string)method!.Invoke(middleware, [interaction3])!;

            // Assert
            // Same request content should generate same hash
            Assert.Equal(key1, key2);

            // Different request content should generate different hash
            Assert.NotEqual(key1, key3);

            // Keys should be SHA256 hex strings (64 characters)
            Assert.Equal(64, key1.Length);
            Assert.Equal(64, key2.Length);
            Assert.Equal(64, key3.Length);

            // Keys should be valid hex strings
            Assert.True(IsValidHexString(key1));
            Assert.True(IsValidHexString(key2));
            Assert.True(IsValidHexString(key3));
        }

        [Fact]
        public void GenerateCacheKey_ShouldHandleUndefinedRequest()
        {
            // Arrange
            var interaction = new RecordedInteraction
            {
                SerializedRequest = default, // JsonElement with ValueKind.Undefined
                SerializedResponse = JsonSerializer.SerializeToElement(new { response = "test" }),
            };

            // Act - Use reflection to access the private GenerateCacheKey method
            var middlewareType = typeof(MockHttpHandlerBuilder).Assembly.GetType(
                "AchieveAi.LmDotnetTools.LmTestUtils.RecordPlaybackMiddleware"
            );
            var middleware = Activator.CreateInstance(middlewareType!, "test.json");
            var method = middlewareType!.GetMethod(
                "GenerateCacheKey",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );

            var key = (string)method!.Invoke(middleware, [interaction])!;

            // Assert
            Assert.Equal("undefined-request", key);
        }

        [Fact]
        public void RequestMatcher_JsonElementMatching_ShouldWorkCorrectly()
        {
            // Arrange
            var request1Json = """
                {
                    "model": "claude-3-sonnet-20240229",
                    "messages": [
                        {"role": "user", "content": "Hello"}
                    ],
                    "max_tokens": 100
                }
                """;

            var request2Json = """
                {
                    "model": "claude-3-sonnet-20240229",
                    "messages": [
                        {"role": "user", "content": "Hello"}
                    ],
                    "max_tokens": 100
                }
                """;

            var request3Json = """
                {
                    "model": "claude-3-sonnet-20240229",
                    "messages": [
                        {"role": "user", "content": "Different message"}
                    ],
                    "max_tokens": 100
                }
                """;

            var element1 = JsonDocument.Parse(request1Json).RootElement;
            var element2 = JsonDocument.Parse(request2Json).RootElement;
            var element3 = JsonDocument.Parse(request3Json).RootElement;

            // Act & Assert
            // Same content should match exactly
            Assert.True(RequestMatcher.MatchesRecordedRequest(element1, element2, exactMatch: true));

            // Same content should match flexibly
            Assert.True(RequestMatcher.MatchesRecordedRequest(element1, element2, exactMatch: false));

            // Different content should not match exactly
            Assert.False(RequestMatcher.MatchesRecordedRequest(element1, element3, exactMatch: true));

            // Different content should not match flexibly (different message content)
            Assert.False(RequestMatcher.MatchesRecordedRequest(element1, element3, exactMatch: false));
        }

        [Fact]
        public void RequestMatcher_MatchMessages_ShouldUseDeepEqualsForContentAndRole()
        {
            // Arrange - Create messages with complex content structures
            var incomingMessagesJson = """
                [
                    {
                        "role": "user",
                        "content": [
                            {
                                "type": "text",
                                "text": "Hello, how are you?"
                            },
                            {
                                "type": "image",
                                "source": {
                                    "type": "base64",
                                    "media_type": "image/jpeg",
                                    "data": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg=="
                                }
                            }
                        ],
                        "extra_property": "should_be_ignored"
                    },
                    {
                        "role": "assistant", 
                        "content": [
                            {
                                "type": "text",
                                "text": "I'm doing well, thank you!"
                            }
                        ],
                        "model": "claude-3-sonnet-20240229"
                    }
                ]
                """;

            var recordedMessagesJson = """
                [
                    {
                        "role": "user",
                        "content": [
                            {
                                "type": "text",
                                "text": "Hello, how are you?"
                            },
                            {
                                "type": "image",
                                "source": {
                                    "type": "base64",
                                    "media_type": "image/jpeg",
                                    "data": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg=="
                                }
                            }
                        ],
                        "different_extra_property": "should_be_ignored"
                    },
                    {
                        "role": "assistant",
                        "content": [
                            {
                                "type": "text", 
                                "text": "I'm doing well, thank you!"
                            }
                        ],
                        "different_model": "claude-3-haiku-20240307"
                    }
                ]
                """;

            var incomingMessages = JsonDocument.Parse(incomingMessagesJson).RootElement;
            var recordedMessages = JsonDocument.Parse(recordedMessagesJson).RootElement;

            // Act - This should match because role and content are identical, even though other properties differ
            var matches = RequestMatcher.MatchesRecordedRequest(
                JsonDocument.Parse($"{{\"messages\": {incomingMessagesJson}}}").RootElement,
                JsonDocument.Parse($"{{\"messages\": {recordedMessagesJson}}}").RootElement,
                exactMatch: false
            );

            // Assert
            Assert.True(matches);
        }

        [Fact]
        public void RequestMatcher_MatchMessages_ShouldFailWhenContentDiffers()
        {
            // Arrange - Create messages where content differs
            var incomingMessagesJson = """
                [
                    {
                        "role": "user",
                        "content": [
                            {
                                "type": "text",
                                "text": "Hello, how are you?"
                            }
                        ]
                    }
                ]
                """;

            var recordedMessagesJson = """
                [
                    {
                        "role": "user",
                        "content": [
                            {
                                "type": "text",
                                "text": "Hello, how are you today?"
                            }
                        ]
                    }
                ]
                """;

            // Act - This should NOT match because content text differs
            var matches = RequestMatcher.MatchesRecordedRequest(
                JsonDocument.Parse($"{{\"messages\": {incomingMessagesJson}}}").RootElement,
                JsonDocument.Parse($"{{\"messages\": {recordedMessagesJson}}}").RootElement,
                exactMatch: false
            );

            // Assert
            Assert.False(matches);
        }

        [Fact]
        public async Task MiddlewareChain_ShouldExecuteInCorrectOrder()
        {
            // Arrange
            var handler = MockHttpHandlerBuilder
                .Create()
                .CaptureRequests(out var capture)
                .RespondWithJson("""{"test": "response"}""")
                .Build();

            var httpClient = new HttpClient(handler);
            var content = new StringContent("""{"test": "request"}""", System.Text.Encoding.UTF8, "application/json");

            // Act
            var response = await httpClient.PostAsync("https://api.test.com/test", content);

            // Assert
            Assert.Equal(1, capture.RequestCount);
            Assert.NotNull(capture.LastRequestBody);
            Assert.Contains("test", capture.LastRequestBody);

            // Verify response was also generated
            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Contains("response", responseContent);
        }

        [Fact]
        public async Task RespondWithSSEArray_ShouldReturnSseContent()
        {
            // Arrange
            var testItems = new object[]
            {
                new { type = "message_start", id = "msg_1" },
                new { type = "content_block", text = "Hello" },
                new { type = "content_block", text = " world!" },
                new { type = "message_stop", id = "msg_1" },
            };

            var handler = MockHttpHandlerBuilder.Create().RespondWithSSEArray(testItems).Build();

            var httpClient = new HttpClient(handler);

            // Act
            var response = await httpClient.GetAsync("https://api.anthropic.com/v1/messages");
            var content = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoCache);
            Assert.Contains("keep-alive", response.Headers.GetValues("Connection"));

            // Verify SSE format
            Assert.Contains("data: {", content);
            Assert.Contains("\"type\":\"message_start\"", content);
            Assert.Contains("\"text\":\"Hello\"", content);
            Assert.Contains("\"text\":\" world!\"", content);
            Assert.Contains("data: [DONE]", content);
        }

        [Fact]
        public async Task RespondWithJsonOrSSE_WithArray_ShouldReturnSseContent()
        {
            // Arrange
            var jsonArray = """[{"id": 1, "name": "test1"}, {"id": 2, "name": "test2"}]""";

            var handler = MockHttpHandlerBuilder.Create().RespondWithJsonOrSSE(jsonArray).Build();

            var httpClient = new HttpClient(handler);

            // Act
            var response = await httpClient.GetAsync("https://api.test.com/items");
            var content = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            // The JSON array is treated as a single JsonElement, so it will be serialized as one data line
            Assert.Contains("data: [{\"id\":1", content);
            Assert.Contains("data: [DONE]", content);
        }

        [Fact]
        public async Task RespondWithSSEArray_WithIndividualItems_ShouldReturnSeparateEvents()
        {
            // Arrange - This is the correct way to get individual SSE events
            var items = new[] { new { id = 1, name = "test1" }, new { id = 2, name = "test2" } };

            var handler = MockHttpHandlerBuilder.Create().RespondWithSSEArray<object>(items).Build();

            var httpClient = new HttpClient(handler);

            // Act
            var response = await httpClient.GetAsync("https://api.test.com/items");
            var content = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            // Each item should be a separate SSE event
            Assert.Contains("data: {\"id\":1", content);
            Assert.Contains("data: {\"id\":2", content);
            Assert.Contains("data: [DONE]", content);
        }

        [Fact]
        public async Task RespondWithJsonOrSSE_WithObject_ShouldReturnJsonContent()
        {
            // Arrange
            var jsonObject = """{"id": 1, "name": "test"}""";

            var handler = MockHttpHandlerBuilder.Create().RespondWithJsonOrSSE(jsonObject).Build();

            var httpClient = new HttpClient(handler);

            // Act
            var response = await httpClient.GetAsync("https://api.test.com/item");

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }

        private static bool IsValidHexString(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }

            foreach (var c in input)
            {
                if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }
    }
}

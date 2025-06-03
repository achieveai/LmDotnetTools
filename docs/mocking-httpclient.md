## 1. Mocking the HttpMessageHandler

### Why this works

* `HttpClient` delegates all of its request/response logic to a private `HttpMessageHandler`.
* `HttpMessageHandler` has a protected, virtual method `SendAsync(HttpRequestMessage, CancellationToken)` that you can override.
* By providing your own fake/derived `HttpMessageHandler`, you can simulate any HTTP response without making real network calls.

### How to do it

1. **Create a “fake” or “stub” handler**
   Write a small class that derives from `HttpMessageHandler` and overrides `SendAsync(...)`. Inside `SendAsync`, you decide what `HttpResponseMessage` to return based on the incoming `HttpRequestMessage`.

   ```csharp
   public class FakeHttpMessageHandler : HttpMessageHandler
   {
       private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handlerFunc;

       public FakeHttpMessageHandler(
           Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
       {
           _handlerFunc = handlerFunc ?? throw new ArgumentNullException(nameof(handlerFunc));
       }

       protected override Task<HttpResponseMessage> SendAsync(
           HttpRequestMessage request,
           CancellationToken cancellationToken)
       {
           // Call whatever logic you passed in to determine the response
           return _handlerFunc(request, cancellationToken);
       }
   }
   ```

2. **Instantiate an `HttpClient` with your fake handler**
   When you construct `HttpClient`, you can pass your fake handler into the constructor. That means, in your service’s constructor (or wherever you normally inject an `HttpClient`), you can supply this fake one during testing.

   ```csharp
   // In your unit test:
   [Fact]
   public async Task MyService_ReturnsData_WhenServerRespondsCorrectly()
   {
       // Arrange: define how you want the fake to respond
       var fakeHandler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
       {
           // For example, if your service requests GET https://api.example.com/data
           if (request.Method == HttpMethod.Get &&
               request.RequestUri == new Uri("https://api.example.com/data"))
           {
               var json = "{ \"id\": 123, \"name\": \"Test Item\" }";
               return new HttpResponseMessage(HttpStatusCode.OK)
               {
                   Content = new StringContent(json, Encoding.UTF8, "application/json")
               };
           }

           // Default: return 404
           return new HttpResponseMessage(HttpStatusCode.NotFound);
       });

       var httpClient = new HttpClient(fakeHandler)
       {
           BaseAddress = new Uri("https://api.example.com")
       };

       // Suppose MyService depends on HttpClient via constructor
       var service = new MyService(httpClient);

       // Act
       var result = await service.GetDataAsync(); // whatever your method is

       // Assert: check that result matches the fake JSON above
       Assert.NotNull(result);
       Assert.Equal(123, result.Id);
       Assert.Equal("Test Item", result.Name);
   }
   ```

3. **Production vs. Test wiring**

   * **In production**, you register a “real” `HttpClient` (via DI, e.g. `services.AddHttpClient<MyService>()`).
   * **In tests**, you bypass DI or override it to inject `new HttpClient(fakeHandler)` instead. That way, your service never reaches out to the network—everything goes through your stubbed `SendAsync`.

---

## 2. Using `IHttpClientFactory` and a Typed Client

Since .NET Core 2.1+, Microsoft recommends that you register and consume `HttpClient` via `IHttpClientFactory`. This gives you better lifecycle management (avoids socket exhaustion) and makes it easier to swap handlers in tests.

### How it works in production

1. In `Startup.ConfigureServices(...)` (or wherever you set up DI), register a typed `HttpClient`:

   ```csharp
   services.AddHttpClient<MyService>(client =>
   {
       client.BaseAddress = new Uri("https://api.example.com");
       // e.g. client.DefaultRequestHeaders.Add("Accept", "application/json");
   });
   ```

   * Under the hood, this creates a named/typed client called `"MyService"` and injects an `HttpClient` instance into the constructor of `MyService`.

2. In your service’s constructor, you simply ask for `HttpClient`:

   ```csharp
   public class MyService
   {
       private readonly HttpClient _httpClient;

       public MyService(HttpClient httpClient)
       {
           _httpClient = httpClient;
       }

       public async Task<MyData> GetDataAsync()
       {
           // Uses _httpClient, which has BaseAddress set
           var response = await _httpClient.GetAsync("/data");
           response.EnsureSuccessStatusCode();

           var json = await response.Content.ReadAsStringAsync();
           return JsonSerializer.Deserialize<MyData>(json);
       }
   }
   ```

### How to override in tests

* When you use `AddHttpClient<MyService>()` under the hood, IServiceCollection actually lets you override the pipeline for that named client. In your unit test, you can register a fake `HttpMessageHandler` into the same pipeline so that your typed client calls your fake instead of the real handler.

1. **In your test setup**, create an `IServiceCollection` and tell it:

   * “Hey, when someone asks for `HttpMessageHandler` in this particular client pipeline, give them my fake handler.”
   * Then build an `IServiceProvider` and ask for `MyService`.

   ```csharp
   public class MyServiceTests
   {
       [Fact]
       public async Task GetDataAsync_ReturnsExpectedData_WithFakeHandler()
       {
           // 1) Create a fake HttpMessageHandler exactly as before
           var fakeHandler = new FakeHttpMessageHandler((request, cancellationToken) =>
           {
               // Simulate the endpoint
               if (request.Method == HttpMethod.Get &&
                   request.RequestUri.AbsolutePath == "/data")
               {
                   var json = "{ \"id\": 123, \"name\": \"Test Item\" }";
                   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent(json, Encoding.UTF8, "application/json")
                   });
               }

               return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
           });

           // 2) Build a service collection with the same configuration you’d use in Startup
           var services = new ServiceCollection();
           services.AddHttpClient<MyService>(client =>
           {
               client.BaseAddress = new Uri("https://api.example.com");
           })
           // 3) Replace the default handler with our fake, for just this named client
           .ConfigurePrimaryHttpMessageHandler(() => fakeHandler);

           var provider = services.BuildServiceProvider();

           // 4) Get MyService from DI; its HttpClient will use fakeHandler behind the scenes
           var myService = provider.GetRequiredService<MyService>();

           // 5) Call the method and assert
           var result = await myService.GetDataAsync();
           Assert.Equal(123, result.Id);
           Assert.Equal("Test Item", result.Name);
       }
   }
   ```

**Why this is recommended:**

* You’re still using `HttpClient` directly in your code, so there’s no extra “wrapper” abstraction.
* `IHttpClientFactory` lets you manage lifetime (sockets, connection pooling) in production.
* In tests, you still get to inject whatever fake `HttpMessageHandler` you want without changing your production code.

---

## 3. Wrapping `HttpClient` in Your Own Interface (Optional)

If you want an even more explicit abstraction—one that hides all `HttpClient` details behind your own interface—you can do something like:

1. Define an interface:

   ```csharp
   public interface IApiClient
   {
       Task<HttpResponseMessage> GetAsync(string relativeUrl);
       Task<HttpResponseMessage> PostAsync(string relativeUrl, HttpContent content);
       // … any other methods you need
   }
   ```

2. Implement it using `HttpClient`:

   ```csharp
   public class ApiClient : IApiClient
   {
       private readonly HttpClient _httpClient;

       public ApiClient(HttpClient httpClient)
       {
           _httpClient = httpClient;
       }

       public Task<HttpResponseMessage> GetAsync(string relativeUrl) =>
           _httpClient.GetAsync(relativeUrl);

       public Task<HttpResponseMessage> PostAsync(string relativeUrl, HttpContent content) =>
           _httpClient.PostAsync(relativeUrl, content);

       // … etc.
   }
   ```

3. Register it via DI (still using `IHttpClientFactory` behind the scenes):

   ```csharp
   services.AddHttpClient<IApiClient, ApiClient>(client =>
   {
       client.BaseAddress = new Uri("https://api.example.com");
   });
   ```

4. In your service where you used to directly consume `HttpClient`, now ask for `IApiClient`. In tests, you can mock `IApiClient` (since it’s just an interface) with your favorite mocking library (Moq, NSubstitute, etc.).

**Drawback:**

* You end up writing boilerplate methods that literally just forward calls to `HttpClient`.
* But the big advantage is that your service logic depends on an interface (`IApiClient`), which you can completely mock away using Moq/NSubstitute instead of fussing with message handlers.

---

## 4. Using a Dedicated HTTP‐Mocking Library

If you’d rather not write your own fake handler, there are dedicated libraries that make it very easy to specify “when the code does `GET https://foo/bar`, return this JSON.” Two popular choices:

1. **RichardSzalay.MockHttp** (a NuGet package)

   * You register expectations like:

     ```csharp
     var mockHttp = new MockHttpMessageHandler();
     mockHttp
         .When("https://api.example.com/data")
         .Respond("application/json", "{ \"id\": 123, \"name\": \"Test Item\" }");
     var client = mockHttp.ToHttpClient();
     client.BaseAddress = new Uri("https://api.example.com");

     var service = new MyService(client);
     ```
   * It even lets you verify “how many times was this URL called?” or “did we POST to /users?” It’s very convenient for straightforward REST tests.

2. **Flurl.Http.Testing** (another library)

   * If you use Flurl.Http in your project, its testing subpackage gives you a fluent way to say:

     ```csharp
     using var httpTest = new HttpTest();
     httpTest.RespondWithJson(new { id = 123, name = "Test Item" });

     var result = await MyService.GetDataAsync();
     httpTest.ShouldHaveCalled("https://api.example.com/data")
         .WithVerb(HttpMethod.Get);
     ```

Both of these libraries hide all the `HttpMessageHandler`-subclass complexity behind more descriptive APIs.

---

## So…which pattern should you pick?

1. **If you want minimal extra code:**

   * Use option 1 (custom `HttpMessageHandler`) or option 2 (`IHttpClientFactory` + custom handler).
   * This is often the *simplest* “no‐wrapper” approach. You keep your production code exactly the same; in tests you just swap out the handler.

2. **If you already use `IHttpClientFactory`:**

   * Go with option 2. It’s the “official” Microsoft-recommended way for any non-trivial app.
   * In tests, just call `.ConfigurePrimaryHttpMessageHandler(...)` to inject your fake logic.

3. **If you prefer mocking a clear interface:**

   * Wrap `HttpClient` behind an `IApiClient` (or whatever you name it) and register it via DI.
   * Then in tests you can hand‐mock or auto-mock `IApiClient` however you like.
   * This is more boilerplate up front but gives you an easy “plug and play” interface for all your HTTP calls.

4. **If you want the least friction in test code:**

   * Use a library like **RichardSzalay.MockHttp**.
   * It’s battle-tested, very readable, and you rarely have to write a custom handler yourself.

---

### A Concrete Example Using `IHttpClientFactory` + Fake Handler

Below is a minimal, complete example so you can see all the pieces together:

1. **Service definition (production code)**

   ```csharp
   // MyService.cs
   public class MyService
   {
       private readonly HttpClient _httpClient;

       public MyService(HttpClient httpClient)
       {
           _httpClient = httpClient;
       }

       public async Task<MyData> GetDataAsync()
       {
           // Calls https://api.example.com/data
           var response = await _httpClient.GetAsync("/data");
           response.EnsureSuccessStatusCode();

           var json = await response.Content.ReadAsStringAsync();
           return JsonSerializer.Deserialize<MyData>(json);
       }
   }

   public class MyData
   {
       public int Id { get; set; }
       public string Name { get; set; }
   }
   ```

2. **DI registration (production startup)**

   ```csharp
   // In Startup.cs or Program.cs
   services.AddHttpClient<MyService>(client =>
   {
       client.BaseAddress = new Uri("https://api.example.com");
       client.DefaultRequestHeaders.Accept
             .Add(new MediaTypeWithQualityHeaderValue("application/json"));
   });
   ```

3. **Unit test (test project)**

   ```csharp
   public class MyServiceTests
   {
       [Fact]
       public async Task GetDataAsync_ReturnsExpectedData()
       {
           // 1) Set up Fake handler
           var fakeHandler = new FakeHttpMessageHandler((request, cancellationToken) =>
           {
               // Only intercept GET /data
               if (request.Method == HttpMethod.Get &&
                   request.RequestUri.PathAndQuery == "/data")
               {
                   var json = "{ \"id\": 123, \"name\": \"Test Item\" }";
                   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                   {
                       Content = new StringContent(json, Encoding.UTF8, "application/json")
                   });
               }
               return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
           });

           // 2) Build a temporary DI container just for the test
           var services = new ServiceCollection();
           services.AddHttpClient<MyService>(client =>
           {
               client.BaseAddress = new Uri("https://api.example.com");
           })
           .ConfigurePrimaryHttpMessageHandler(() => fakeHandler);

           var provider = services.BuildServiceProvider();

           // 3) Resolve MyService (its HttpClient now uses fakeHandler)
           var service = provider.GetRequiredService<MyService>();

           // 4) Act
           var result = await service.GetDataAsync();

           // 5) Assert
           Assert.NotNull(result);
           Assert.Equal(123, result.Id);
           Assert.Equal("Test Item", result.Name);
       }
   }

   // FakeHttpMessageHandler from earlier
   public class FakeHttpMessageHandler : HttpMessageHandler
   {
       private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handlerFunc;

       public FakeHttpMessageHandler(
           Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
       {
           _handlerFunc = handlerFunc ?? throw new ArgumentNullException(nameof(handlerFunc));
       }

       protected override Task<HttpResponseMessage> SendAsync(
           HttpRequestMessage request,
           CancellationToken cancellationToken)
       {
           return _handlerFunc(request, cancellationToken);
       }
   }
   ```

This test exercises your real `MyService` code (unmodified), but every time it calls `_httpClient.GetAsync("/data")`, the message goes into your fake handler instead of the network. You can return whatever JSON or HTTP status you want, and write tests for all error conditions and happy paths.

---

## Summary

* **You can’t mock `HttpClient` directly**—it’s sealed.
* The **standard pattern** is to mock or fake `HttpMessageHandler` (its protected `SendAsync` method), then inject a new `HttpClient(fakeHandler)` into your code under test.
* If you’re on .NET Core (2.1+), use **`IHttpClientFactory`** with a typed client and override the primary handler in your unit tests via `.ConfigurePrimaryHttpMessageHandler(...)`.
* Optionally, you can hide all HTTP calls behind **your own interface** (e.g. `IApiClient`) and mock that interface.
* You can also let a **specialized library** (e.g. RichardSzalay.MockHttp) handle the fake‐handler boilerplate for you.

That is the recommended mechanism: either mock the underlying `HttpMessageHandler` (by hand or via a library), or wrap `HttpClient` behind an interface and mock that. Both approaches keep your production code unchanged and let you write fast, reliable unit tests without touching the network.

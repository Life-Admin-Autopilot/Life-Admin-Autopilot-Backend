using System.Net;
using System.Text;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Grounding;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// A <see cref="LangflowAiProvider"/> wired to a canned NDJSON body, for the suites
/// that need a whole turn to run rather than one component of it.
///
/// <para>
/// <b>Every provider test needs Mongo</b>, including the ones that look like pure HTTP
/// tests: a turn sweeps stale pending calls and reads its conversation before it opens
/// a socket. <see cref="Database"/> returns null when the parity instance is down and
/// callers return early, the convention the rest of the AI suite follows.
/// </para>
/// </summary>
internal static class StubbedLangflow
{
    /// <summary>NDJSON: one physical line per frame. A frame split across two lines is silently half a frame.</summary>
    internal static string Ndjson(params string[] frames) => string.Join("\n", frames) + "\n";

    /// <summary>A handler that serves the given frames and records what it was sent.</summary>
    internal static StubHttpMessageHandler Handler(params string[] frames)
    {
        var body = Ndjson(frames);

        return new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
        });
    }

    internal static LangflowAiProvider Provider(
        HttpMessageHandler handler,
        IMongoDatabase database,
        AiConversationRepository? conversations = null) =>
        new(
            new SingleHandlerFactory(handler),
            LangflowOptions.FromConfiguration(Settings()),
            LangflowInputBinding.FromConfiguration(Settings()),
            conversations ?? new AiConversationRepository(database),
            new AiGroundingRepository(database));

    internal static LangflowSessionMemory SessionMemory(HttpMessageHandler handler) =>
        new(new SingleHandlerFactory(handler), LangflowOptions.FromConfiguration(Settings()));

    internal static IConfiguration Settings() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Langflow:BaseUrl"] = "http://langflow.test",
                ["Ai:Langflow:FlowId"] = "flow-1",
                ["Ai:Langflow:ApiKey"] = "key-1",
            })
            .Build();

    internal static async Task<List<AiStreamEvent>> AskAsync(
        LangflowAiProvider provider,
        ObjectId userId,
        string question = "What is due?")
    {
        var events = new List<AiStreamEvent>();

        await foreach (var value in provider.AskAsync(new AiAskRequest(userId.ToString(), question, null)))
        {
            events.Add(value);
        }

        return events;
    }

    /// <summary>
    /// A private database per suite, so the suites running concurrently against one
    /// mongod cannot see each other's conversations (KERNEL.md §12).
    /// </summary>
    internal static IMongoDatabase? Database(string suite)
    {
        try
        {
            // MUST come before the first collection is resolved, or the class map is
            // built with PascalCase element names and every hand-written update path
            // misses.
            MongoKernelConventions.Register();

            var client = new MongoClient(
                $"{KernelWebApplicationFactory.ParityMongoUri}/?serverSelectionTimeoutMS=800");
            var database = client.GetDatabase($"kitto_parity_dotnet_m_{suite}");
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class SingleHandlerFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using MerchantAdmin.AI.API.Harness;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 会话历史序列化的**保真度**测试。
///
/// 这是能不能把会话放 Redis 的前提：如果往返之后发出去的请求体变了，
/// 就等于悄悄改写了模型的上下文，甚至可能因为丢工具调用序列被 OpenAI 直接拒绝。
/// 所以这里不比对象、不比字段，而是直接比对**实际发出的 HTTP 请求体**是否逐字节一致。
/// </summary>
public class ChatHistorySerializerTests
{
    [Fact]
    public async Task 工具调用序列往返后请求体逐字节一致()
    {
        using var endpoint = new FakeOpenAiEndpoint();
        var kernel = BuildKernel(endpoint.BaseUrl);

        var original = new ChatHistory();
        original.AddUserMessage("有哪些商品？");

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var reply = await chat.GetChatMessageContentAsync(original, Settings, kernel);
        original.Add(reply);

        // 第 2 次请求时才带上了 tool_calls + tool 结果，那才是要保真的部分
        var sentMessages = endpoint.Bodies[^1];
        var baseline = new ChatHistory();
        foreach (var message in original.Take(original.Count - 1)) baseline.Add(message);

        var wire = ChatHistorySerializer.Serialize(baseline);
        var restored = ChatHistorySerializer.Deserialize(wire);

        Assert.Equal(baseline.Count, restored.Count);

        await chat.GetChatMessageContentAsync(restored, Settings, kernel);
        var restoredMessages = endpoint.Bodies[^1];

        Assert.Equal(ExtractMessages(sentMessages), ExtractMessages(restoredMessages));
    }

    [Fact]
    public async Task 工具调用参数的数字类型不会退化成字符串()
    {
        using var endpoint = new FakeOpenAiEndpoint();
        var kernel = BuildKernel(endpoint.BaseUrl);

        var history = new ChatHistory();
        history.AddUserMessage("有哪些商品？");
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        history.Add(await chat.GetChatMessageContentAsync(history, Settings, kernel));

        var baseline = new ChatHistory();
        foreach (var message in history.Take(history.Count - 1)) baseline.Add(message);

        var wire = ChatHistorySerializer.Serialize(baseline);

        // SK 的 FunctionCallContent.Arguments 是解析视图，会把 1 变成 "1"；
        // 序列化必须取 provider 的原始 JSON，否则这里会是 "1"
        Assert.Contains("\"page\":1", wire);
        Assert.DoesNotContain("\"page\":\"1\"", wire);
    }

    [Fact]
    public void 坏数据返回空会话而不是抛异常()
    {
        Assert.Empty(ChatHistorySerializer.Deserialize("not json"));
        Assert.Empty(ChatHistorySerializer.Deserialize("{}"));
        Assert.Empty(ChatHistorySerializer.Deserialize(""));
        Assert.Empty(ChatHistorySerializer.Deserialize("""{"v":1,"messages":"oops"}"""));
    }

    [Fact]
    public void 未知内容类型被丢弃并计数()
    {
        var history = new ChatHistory();
        var message = new ChatMessageContent(AuthorRole.Assistant,
            new ChatMessageContentItemCollection { new TextContent("正文"), new ImageContent(new Uri("http://x/y.png")) });
        history.Add(message);

        var wire = ChatHistorySerializer.Serialize(history);

        Assert.Contains("\"dropped\":1", wire);
        Assert.Contains("正文", wire);
        Assert.DoesNotContain("y.png", wire);

        // 只剩文本，往返后仍然可用
        var restored = ChatHistorySerializer.Deserialize(wire);
        Assert.Single(restored);
        Assert.Equal("正文", restored[0].Content);
    }

    [Fact]
    public void 全是未知内容的消息不会被写进去()
    {
        var history = new ChatHistory();
        history.Add(new ChatMessageContent(AuthorRole.Assistant,
            new ChatMessageContentItemCollection { new ImageContent(new Uri("http://x/y.png")) }));

        var restored = ChatHistorySerializer.Deserialize(ChatHistorySerializer.Serialize(history));

        // 空消息会让 OpenAI 直接拒绝请求，必须丢掉
        Assert.Empty(restored);
    }

    private static OpenAIPromptExecutionSettings Settings => new()
    {
        FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
        MaxTokens = 100
    };

    private static Kernel BuildKernel(string baseUrl)
    {
        var builder = Kernel.CreateBuilder();
        builder.AddOpenAIChatCompletion("deepseek-chat", new Uri(baseUrl), "fake-key");
        var kernel = builder.Build();

        Task<string> ListProducts(KernelArguments args, CancellationToken ct)
            => Task.FromResult("""{"total":2,"items":[{"productId":1,"name":"雪碧"}]}""");

        kernel.Plugins.AddFromFunctions("Store", new[]
        {
            KernelFunctionFactory.CreateFromMethod(ListProducts, new KernelFunctionFromMethodOptions
            {
                FunctionName = "list_products",
                Description = "查询商品",
                Parameters = new[]
                {
                    new KernelParameterMetadata("page")
                    {
                        IsRequired = false,
                        ParameterType = typeof(object),
                        Schema = KernelJsonSchema.Parse("""{"type":"integer"}""")
                    }
                }
            })
        });

        return kernel;
    }

    private static string ExtractMessages(string body)
    {
        using var document = JsonDocument.Parse(body);
        return JsonSerializer.Serialize(document.RootElement.GetProperty("messages"));
    }
}

/// <summary>
/// 假的 OpenAI 兼容端点：第 1 次返回带数字参数的 tool_calls，之后返回最终文本。
/// 数字参数是刻意的 —— 用来暴露「参数类型在序列化时退化成字符串」这类问题。
/// </summary>
internal sealed class FakeOpenAiEndpoint : IDisposable
{
    private readonly HttpListener _listener;
    private int _calls;

    public FakeOpenAiEndpoint()
    {
        for (var port = 19300; port < 19380; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch { continue; }

            _listener = listener;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _ = Task.Run(LoopAsync);
            return;
        }
        throw new InvalidOperationException("找不到空闲端口启动假 OpenAI 端点");
    }

    public string BaseUrl { get; }

    private readonly List<string> _bodies = new();
    public IReadOnlyList<string> Bodies { get { lock (_bodies) return _bodies.ToList(); } }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            lock (_bodies) _bodies.Add(body);

            var round = Interlocked.Increment(ref _calls);
            var payload = round == 1
                ? """
                  {"id":"c1","object":"chat.completion","created":1,"model":"m","choices":[
                    {"index":0,"message":{"role":"assistant","content":null,"tool_calls":[
                      {"id":"call_1","type":"function","function":{"name":"Store-list_products","arguments":"{\"page\":1}"}}]},
                     "finish_reason":"tool_calls"}],
                   "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                  """
                : """
                  {"id":"c2","object":"chat.completion","created":2,"model":"m","choices":[
                    {"index":0,"message":{"role":"assistant","content":"共 2 个商品。"},"finish_reason":"stop"}],
                   "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                  """;

            var bytes = Encoding.UTF8.GetBytes(payload);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
    }
}

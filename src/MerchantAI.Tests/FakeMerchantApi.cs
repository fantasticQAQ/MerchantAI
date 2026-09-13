using System.Net;
using System.Text;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 假的 MerchantAdmin.API：返回 { code, message, data, success } 包装，
/// 并记录收到的请求，供断言「到底发了什么」。测试不应该依赖真的业务服务在跑。
/// </summary>
public sealed class FakeMerchantApi : IDisposable
{
    private readonly HttpListener _listener;

    public FakeMerchantApi()
    {
        for (var port = 19080; port < 19160; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch { continue; }

            _listener = listener;
            BaseUrl = $"http://127.0.0.1:{port}";
            _ = Task.Run(LoopAsync);
            return;
        }
        throw new InvalidOperationException("找不到空闲端口启动假业务服务");
    }

    public string BaseUrl { get; }
    public string? LastUrl { get; private set; }
    public string? LastMethod { get; private set; }
    public string? LastBody { get; private set; }
    public string? LastOrderBody { get; private set; }
    public int OrderPostCount { get; private set; }

    /// <summary>最后一次请求带上的 Authorization 头，用来断言 token 有没有挂上去。</summary>
    public string? LastAuthorization { get; private set; }

    /// <summary>业务接口返回 success=false 时用，用来验证错误映射。</summary>
    public bool ReturnBusinessFailure { get; set; }

    public void ResetOrderCounter() => OrderPostCount = 0;

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            LastUrl = ctx.Request.Url?.PathAndQuery;
            LastMethod = ctx.Request.HttpMethod;
            LastAuthorization = ctx.Request.Headers["Authorization"];

            string? body = null;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                body = await reader.ReadToEndAsync();
            }
            LastBody = body;

            var path = ctx.Request.Url!.AbsolutePath;
            var method = ctx.Request.HttpMethod;
            string payload;

            const string OkTrue = """{"code":0,"message":"success","data":true,"success":true}""";

            if (method == "POST" && PathIs(path, "/api/orders/create"))
            {
                OrderPostCount++;
                LastOrderBody = body;
                payload = ReturnBusinessFailure
                    ? """{"code":1001,"message":"库存不足","data":null,"success":false}"""
                    : """{"code":0,"message":"success","data":1001,"success":true}""";
            }
            else if (method == "GET" && PathIs(path, "/api/products"))
            {
                payload = """
                {"code":0,"message":"success","success":true,"data":{"total":2,"page":1,"pageSize":10,
                 "items":[{"productId":1,"name":"雪碧","price":3.00,"stock":10.00,"isActive":true},
                          {"productId":3,"name":"雪碧","price":3.00,"stock":80.00,"isActive":true}]}}
                """;
            }
            // 写操作：返回成功即可，测试真正关心的是「发出了什么请求」
            else if (method is "PUT" or "DELETE" or "POST" && IsWriteEndpoint(method, path))
            {
                payload = OkTrue;
            }
            else if (method == "GET" && PathIs(path, "/api/orders"))
            {
                payload = """{"code":0,"message":"success","success":true,"data":{"total":0,"page":1,"pageSize":10,"items":[]}}""";
            }
            else if (method == "GET" && PathIs(path, "/api/dashboard"))
            {
                payload = """{"code":0,"message":"success","success":true,"data":{"productCount":2,"orderCount":0,"paidOrderCount":0,"pendingOrderCount":0,"totalSales":0}}""";
            }
            else if (method == "GET" && PathIs(path, "/api/logs"))
            {
                payload = """{"code":0,"message":"success","success":true,"data":{"total":0,"page":1,"pageSize":10,"items":[]}}""";
            }
            else
            {
                payload = """{"code":404,"message":"not found","data":null,"success":false}""";
                ctx.Response.StatusCode = 404;
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    /// <summary>商品/订单的增删改端点。</summary>
    private static bool IsWriteEndpoint(string method, string path)
    {
        if (PathIs(path, "/api/products")) return true;                       // 新建商品
        if (path.StartsWith("/api/products/", StringComparison.OrdinalIgnoreCase)) return true;   // 改/删商品
        if (path.StartsWith("/api/orders/", StringComparison.OrdinalIgnoreCase)) return true;     // 取消/支付/退款/删除订单
        return false;
    }

    private static bool PathIs(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
    }
}

using System.Net;
using AirBlade.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AirBlade.Tests;

/// <summary>
/// 用可注入的 HttpMessageHandler 模拟 GitHub Releases API,验证版本比对逻辑。
/// </summary>
[TestClass]
public sealed class UpdateCheckerTests
{
    /// <summary>
    /// 构造一个固定响应的消息处理程序,便于测试。
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHttpMessageHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_response);
    }

    private static UpdateChecker CreateChecker(HttpStatusCode status, string body = "")
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        };
        return new UpdateChecker(new StubHttpMessageHandler(response));
    }

    private static string ReleaseJson(string tagName) =>
        $$"""{"tag_name":"{{tagName}}","html_url":"https://github.com/mejiro-rin/airblade-win/releases/tag/{{tagName}}"}""";

    [TestMethod]
    public async Task 远程版本更高时返回NewAvailable()
    {
        using var checker = CreateChecker(HttpStatusCode.OK, ReleaseJson("v0.5.0"));

        var result = await checker.CheckAsync();

        Assert.AreEqual(UpdateCheckStatus.NewAvailable, result.Status);
        Assert.AreEqual(new Version(0, 5, 0), result.RemoteVersion);
        Assert.IsNotNull(result.ReleaseUrl);
    }

    [TestMethod]
    public async Task 远程版本与当前相同时返回UpToDate()
    {
        using var checker = CreateChecker(HttpStatusCode.OK, ReleaseJson($"v{UpdateChecker.CurrentVersion}"));

        var result = await checker.CheckAsync();

        Assert.AreEqual(UpdateCheckStatus.UpToDate, result.Status);
    }

    [TestMethod]
    public async Task 远程版本更低时返回UpToDate()
    {
        using var checker = CreateChecker(HttpStatusCode.OK, ReleaseJson("v0.3.0"));

        var result = await checker.CheckAsync();

        Assert.AreEqual(UpdateCheckStatus.UpToDate, result.Status);
    }

    [TestMethod]
    public async Task 无Release时返回NoReleases()
    {
        using var checker = CreateChecker(HttpStatusCode.NotFound);

        var result = await checker.CheckAsync();

        Assert.AreEqual(UpdateCheckStatus.NoReleases, result.Status);
    }

    [TestMethod]
    public async Task 预发布标签按检查失败处理()
    {
        using var checker = CreateChecker(HttpStatusCode.OK, ReleaseJson("v0.4.0-rc1"));

        var result = await checker.CheckAsync();

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
    }

    [TestMethod]
    public async Task 非法标签按检查失败处理()
    {
        using var checker = CreateChecker(HttpStatusCode.OK, ReleaseJson("abc"));

        var result = await checker.CheckAsync();

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
    }

    [TestMethod]
    public async Task 服务端错误按检查失败处理()
    {
        using var checker = CreateChecker(HttpStatusCode.InternalServerError);

        var result = await checker.CheckAsync();

        Assert.AreEqual(UpdateCheckStatus.Failed, result.Status);
    }

    [TestMethod]
    public async Task 标签不带v前缀也能解析()
    {
        using var checker = CreateChecker(HttpStatusCode.OK, ReleaseJson("0.5.0"));

        var result = await checker.CheckAsync();

        Assert.AreEqual(UpdateCheckStatus.NewAvailable, result.Status);
    }
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirBlade.Services;

/// <summary>
/// 一次版本检查的结果状态。
/// </summary>
public enum UpdateCheckStatus
{
    /// <summary>当前版本已经是最新。</summary>
    UpToDate,

    /// <summary>GitHub 上有更新的版本。</summary>
    NewAvailable,

    /// <summary>仓库还没有任何 Release。</summary>
    NoReleases,

    /// <summary>检查失败(网络、解析或服务端错误)。</summary>
    Failed,
}

/// <summary>
/// 版本检查结果;Status 为 NewAvailable 时带远程版本与发布页地址。
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version? RemoteVersion = null,
    string? ReleaseUrl = null,
    string? ErrorMessage = null);

/// <summary>
/// 通过 GitHub Releases API 检查 AirBlade 是否有新版本。
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    // GitHub 仓库的 latest Release 接口;User-Agent 头必须携带,否则 GitHub 返回 403。
    private const string LatestReleaseUrl = "https://api.github.com/repos/mejiro-rin/airblade-win/releases/latest";

    private readonly HttpClient _httpClient;
    private bool _disposed;

    /// <summary>
    /// 创建检查器;默认使用系统代理与网络栈。
    /// </summary>
    public UpdateChecker()
        : this(new HttpClientHandler())
    {
    }

    /// <summary>
    /// 创建检查器并注入消息处理程序,便于单元测试模拟 GitHub 响应。
    /// </summary>
    public UpdateChecker(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"AirBlade/{CurrentVersion}");
    }

    /// <summary>
    /// 当前应用版本(程序集版本的三段版本号)。
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            var assemblyVersion = typeof(UpdateChecker).Assembly.GetName().Version;
            return assemblyVersion is null
                ? new Version(0, 0, 0)
                : new Version(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(assemblyVersion.Build, 0));
        }
    }

    /// <summary>
    /// 向 GitHub 查询最新 Release 并与当前版本比较。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);

            // 404 表示仓库还没有任何 Release。
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NoReleases);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: $"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release is null || !TryParseTag(release.TagName, out var remoteVersion))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: "无法解析版本标签");
            }

            return remoteVersion > CurrentVersion
                ? new UpdateCheckResult(UpdateCheckStatus.NewAvailable, remoteVersion, release.HtmlUrl)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, remoteVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 网络失败、超时或响应格式异常都按检查失败处理,由界面提示用户稍后重试。
            return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// 把形如 v0.4.0 或 0.4.0 的标签解析为三段版本号;带预发布后缀或格式非法时返回 false。
    /// </summary>
    private static bool TryParseTag(string? tagName, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        var text = tagName.Trim();
        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        // 只接受至少两段、且不含预发布后缀的纯数字版本。
        if (text.Split('.').Length < 2 || !Version.TryParse(text, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>
    /// GitHub Releases API 返回的 latest Release 的字段子集。
    /// </summary>
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }
}

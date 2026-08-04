using System.Reflection;
using AirBlade.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AirBlade.Tests;

/// <summary>
/// 校验关于页新增的本地化键中英文文案齐全。
/// </summary>
[TestClass]
public sealed class LocalizationConsistencyTests
{
    private static readonly string[] AboutKeys =
    [
        "Settings.About.Privacy",
        "Settings.About.PrivacyDescription",
        "Settings.About.OpenSourceLicenses",
        "Settings.About.OpenSourceLicensesDescription",
        "Settings.About.CheckForUpdates",
        "Settings.About.CheckForUpdatesDescription",
        "Settings.About.CurrentVersion",
        "Settings.About.Checking",
        "Settings.About.UpToDate",
        "Settings.About.FoundNewVersion",
        "Settings.About.NoReleases",
        "Settings.About.CheckFailed",
        "Settings.About.GoDownload",
        "Settings.About.CopyDiagnostics",
        "Settings.About.CopyDiagnosticsDescription",
        "Settings.About.ReportIssue",
        "Settings.About.ReportIssueDescription",
        "Settings.About.Copied",
        "Settings.About.Open",
        "Settings.About.View",
        "Settings.About.Copy",
        "Settings.About.Close",
        "Settings.About.LicensesError",
    ];

    [TestMethod]
    public void 关于页新增键均存在且中英文非空()
    {
        var field = typeof(LocalizationService).GetField(
            "Texts",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(field, "LocalizationService.Texts 字段应存在");

        var rawTexts = field!.GetValue(null);
        Assert.IsNotNull(rawTexts, "LocalizationService.Texts 字段值不应为空");
        var texts = (Dictionary<string, (string Chinese, string English)>)rawTexts!;
        foreach (var key in AboutKeys)
        {
            Assert.IsTrue(texts.TryGetValue(key, out var pair), $"缺少本地化键:{key}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(pair.Chinese), $"中文文案为空:{key}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(pair.English), $"英文文案为空:{key}");
        }
    }

    [TestMethod]
    public void 版权文案保留年份占位符()
    {
        var field = typeof(LocalizationService).GetField(
            "Texts",
            BindingFlags.NonPublic | BindingFlags.Static);
        var rawTexts = field!.GetValue(null);
        Assert.IsNotNull(rawTexts, "LocalizationService.Texts 字段值不应为空");
        var texts = (Dictionary<string, (string Chinese, string English)>)rawTexts!;

        var pair = texts["Settings.About.CopyrightDescription"];

        StringAssert.Contains(pair.Chinese, "{0}");
        StringAssert.Contains(pair.English, "{0}");
    }
}

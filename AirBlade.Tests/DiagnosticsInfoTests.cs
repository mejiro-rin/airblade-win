using AirBlade.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AirBlade.Tests;

/// <summary>
/// 验证诊断信息拼接结果包含所有关键字段且没有空值。
/// </summary>
[TestClass]
public sealed class DiagnosticsInfoTests
{
    [TestMethod]
    public void 诊断信息包含版本系统运行时与架构()
    {
        var text = DiagnosticsInfo.Build();

        StringAssert.Contains(text, "AirBlade 版本:");
        StringAssert.Contains(text, "Windows 版本:");
        StringAssert.Contains(text, ".NET 运行时:");
        StringAssert.Contains(text, "系统架构:");
    }

    [TestMethod]
    public void 诊断信息各字段均非空()
    {
        var text = DiagnosticsInfo.Build();
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.IsTrue(lines.Length >= 5, "诊断信息应至少包含 5 行");
        foreach (var line in lines)
        {
            var separatorIndex = line.IndexOf(':');
            Assert.IsTrue(separatorIndex >= 0, $"缺少字段分隔符:{line}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(line[(separatorIndex + 1)..]), $"字段值为空:{line}");
        }
    }
}

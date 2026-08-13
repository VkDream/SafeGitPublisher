using System.Globalization;
using System.Windows.Data;
using SafeGitPublisher.Converters;

namespace SafeGitPublisher.Tests;

/// <summary>提交类型下拉框的中文显示合同：只转换界面文字，不改写内部前缀。</summary>
public static class CommitPrefixDisplayTests
{
    [Test]
    public static void CommitPrefixConverter_DisplaysChinese_AndKeepsUnknownVisible()
    {
        var converter = new CommitPrefixToChineseConverter();
        var culture = CultureInfo.InvariantCulture;

        Assert.Equal("不使用前缀", converter.Convert(string.Empty, typeof(string), null, culture).ToString());
        Assert.Equal("新增功能", converter.Convert("feat: ", typeof(string), null, culture).ToString());
        Assert.Equal("问题修复", converter.Convert("fix: ", typeof(string), null, culture).ToString());
        Assert.Equal("文档更新", converter.Convert("docs: ", typeof(string), null, culture).ToString());
        Assert.Equal("代码重构", converter.Convert("refactor: ", typeof(string), null, culture).ToString());
        Assert.Equal("日常维护", converter.Convert("chore: ", typeof(string), null, culture).ToString());
        Assert.Equal("测试调整", converter.Convert("test: ", typeof(string), null, culture).ToString());
        Assert.Equal("custom: ", converter.Convert("custom: ", typeof(string), null, culture).ToString());
        Assert.True(ReferenceEquals(Binding.DoNothing,
            converter.ConvertBack("新增功能", typeof(string), null, culture)), "显示转换不得反向改写提交前缀");
    }
}

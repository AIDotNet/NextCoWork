using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>
/// 斜杠命令的展开(2.x <c>shared/domain/command.ts</c> 的 <c>parseCommandInvocation / expandCommandPrompt / applyCommand</c>,2.x 没有单测,这里按它的注释里的承诺补)。
/// 展开在客户端发送前完成,所以手机和桌面看到的输入一致;设备收到的永远是普通文本。
/// </summary>
public sealed class CommandExpansionTests
{
    [Fact]
    public void Parses_a_draft_that_is_entirely_one_invocation()
    {
        Assert.Equal(new CommandInvocation("init", "update --no-history"), CommandExpansion.Parse("/init update --no-history"));
        Assert.Equal(new CommandInvocation("init", ""), CommandExpansion.Parse("  /init  "));
        Assert.Equal(new CommandInvocation("review", "第一行\n第二行"), CommandExpansion.Parse("/review 第一行\n第二行"));
    }

    [Theory]
    [InlineData("帮我看下 /init 这个命令")] // 在说话,不是在调用:判据落在「/ 在开头」
    [InlineData("hello")]
    [InlineData("/")]
    [InlineData("/ init")]
    [InlineData("//init")]
    [InlineData("/path/to/file")]
    public void A_draft_that_is_not_an_invocation_parses_to_null(string draft)
    {
        Assert.Null(CommandExpansion.Parse(draft));
    }

    [Fact]
    public void Arguments_replaces_all_of_it_and_positionals_split_on_whitespace()
    {
        Assert.Equal("Fix a.ts and b.ts please", CommandExpansion.Expand("Fix $ARGUMENTS please", "a.ts and b.ts"));
        Assert.Equal("a.ts then b.ts", CommandExpansion.Expand("$1 then $2", "a.ts b.ts c.ts"));
        Assert.Equal("[x][]", CommandExpansion.Expand("[$1][$2]", "x"));
    }

    [Fact]
    public void Invariant_with_no_placeholder_the_arguments_are_appended_not_dropped()
    {
        // 丢掉的话用户敲的那句话会凭空消失,且没有任何反馈。
        Assert.Equal("Review the code\n\nfocus on security", CommandExpansion.Expand("Review the code", "focus on security"));
        Assert.Equal("Review the code", CommandExpansion.Expand("Review the code", ""));
    }

    [Fact]
    public void A_placeholder_is_only_a_placeholder_at_a_word_boundary()
    {
        Assert.Equal("$ARGUMENTSX stays\n\nz", CommandExpansion.Expand("$ARGUMENTSX stays", "z"));
        Assert.Equal("$10 is not $1x\n\nq", CommandExpansion.Expand("$10 is not $1x", "q")); // $10、$1x 都不是占位符,参数追加到末尾
        Assert.Equal("q 中文", CommandExpansion.Expand("$1 中文", "q")); // 中文紧挨着也算边界(与 2.x 一致:JS 的 \b 是 ASCII 的)
    }

    [Fact]
    public void Apply_expands_a_known_command_ignoring_case_and_leaves_everything_else_untouched()
    {
        (string, string)[] commands = [("init", "Init: $ARGUMENTS"), ("review", "Review $1")];

        Assert.Equal("Init: update", CommandExpansion.Apply("/INIT update", commands));
        Assert.Equal("Review a.ts", CommandExpansion.Apply("/review a.ts", commands));
        Assert.Equal("/unknown thing", CommandExpansion.Apply("/unknown thing", commands));   // 认不出的命令名原样返回
        Assert.Equal("say /init", CommandExpansion.Apply("say /init", commands));
        Assert.Equal("plain", CommandExpansion.Apply("plain", commands));
    }

    [Fact]
    public void The_name_rule_matches_the_command_file_names()
    {
        Assert.True(CommandExpansion.IsValidName("review"));
        Assert.True(CommandExpansion.IsValidName("Code_Review-2"));
        Assert.False(CommandExpansion.IsValidName("has space"));
        Assert.False(CommandExpansion.IsValidName("../x"));
        Assert.False(CommandExpansion.IsValidName("-lead"));
        Assert.False(CommandExpansion.IsValidName("a\n"));
        Assert.False(CommandExpansion.IsValidName(new string('a', 65)));
    }
}

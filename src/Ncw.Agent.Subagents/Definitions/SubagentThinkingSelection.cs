using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>设置里「子代理思考深度」那一栏:跟随对话,或固定某一档。</summary>
public readonly record struct SubagentThinking(ThinkingLevel? Level)
{
    /// <summary>「跟随本轮对话」——子 Run 沿用父 Run 这一轮的档位。★ 必须是一个<b>独立</b>的取值,不能复用 <see cref="ThinkingLevel.Auto"/>:那是「模型自己的默认档」这个另一个意思。</summary>
    public static SubagentThinking Inherit => default;

    public bool IsInherit => Level is null;

    public static SubagentThinking Of(ThinkingLevel level) => new(level);
}

/// <summary>
/// 子代理这一轮用哪个思考档位(2.x <c>shared/domain/subagent-thinking.ts</c>)。和 <c>ModelSelections.ForSubagent</c> 是<b>一对</b>:三档来源、越具体的越优先,而且必须由同一段代码算出来——
/// 界面显示的档位和真正下发的档位分头算的话,两边只是碰巧一致,一旦不一致就是「设置里写着高、请求里发的是中」,且零报错。
/// 取出来的档位还要按子 Run 自己的模型归一化一次(<see cref="ISubagentModels.NormalizeThinking"/>):档位是模型的属性,而子 Run 的模型未必和父 Run 是同一个。
/// </summary>
public static class SubagentThinkingSelection
{
    /// <summary>文件里 <c>thinking:</c> 的合法写法(小写,和线上写法一致)。</summary>
    public static IReadOnlyList<string> LevelNames { get; } = ["auto", "off", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static bool TryParseLevel(string lower, out ThinkingLevel level)
    {
        switch (lower)
        {
            case "auto": level = ThinkingLevel.Auto; return true;
            case "off": level = ThinkingLevel.Off; return true;
            case "minimal": level = ThinkingLevel.Minimal; return true;
            case "low": level = ThinkingLevel.Low; return true;
            case "medium": level = ThinkingLevel.Medium; return true;
            case "high": level = ThinkingLevel.High; return true;
            case "xhigh": level = ThinkingLevel.XHigh; return true;
            case "max": level = ThinkingLevel.Max; return true;
            default: level = default; return false;
        }
    }

    /// <param name="declared">定义文件里的 <c>thinking:</c>(认不出的值在加载期就被丢掉了)。null = 文件里没写这一行(不是「跟随」,「跟随」由 <paramref name="configured"/> 那一档表达)。</param>
    /// <param name="configured">设置里那一栏。「跟随」= 落到父 Run。★ 它排在父 Run 前面才是这一栏存在的理由:子代理是拿来跑量的,用户配它就是为了让那几路别跟着主力模型这一轮的选择走。</param>
    /// <param name="parent">父 Run 这一轮的档位。</param>
    public static ThinkingLevel Select(ThinkingLevel? declared, SubagentThinking configured, ThinkingLevel parent) =>
        declared ?? configured.Level ?? parent;
}

using Ncw.Agent.Llm;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 用户选中的那个模型:别名 + 可选的供应商。<see cref="ProviderId"/> 非空 = 用户显式固定了供应商(硬约束,不跨家兜底);空 = 没指定过(历史数据、
/// 子代理 frontmatter 里的裸别名),按供应商优先级择优。别名的主键是 <c>(providerId, alias)</c>,同一个别名可以挂在多家上,所以只有 alias 不足以定位一条绑定。
/// </summary>
public sealed record ModelSelection(string Alias, string? ProviderId = null)
{
    public ModelRef ToRef() => new(ProviderId, Alias);

    /// <summary>空别名 = 未指定模型(「跟随对话」)。</summary>
    public bool IsEmpty => Alias.Length == 0;
}

/// <summary>「用户选中的模型」的纯函数(2.x <c>shared/domain/model-selection.ts</c> 与 <c>ipc/provider.ts</c> 的 <c>repairModelSelection</c>)。</summary>
public static class ModelSelections
{
    /// <summary>
    /// 复合键 <c>providerId/alias</c>。分隔符是 <c>/</c> 且<b>只切第一个</b>:别名里真的会有斜杠(<c>openrouter/claude-sonnet-4</c>),
    /// 而供应商 id 不含斜杠(<see cref="ProviderRefs.IsValidProviderId"/>),所以第一个斜杠永远是分隔符。空别名对应空键。
    /// </summary>
    public static string Key(string? providerId, string alias) => alias.Length == 0 ? "" : $"{providerId ?? ""}/{alias}";

    public static ModelSelection ParseKey(string key)
    {
        var slash = key.IndexOf('/');
        if (slash < 0)
        {
            // 没有分隔符 = 一个裸别名(旧的持久化值),按「没指定」处理。
            return new ModelSelection(key);
        }

        var providerId = key[..slash];
        return new ModelSelection(key[(slash + 1)..], providerId.Length == 0 ? null : providerId);
    }

    /// <summary>
    /// 子代理该用哪个「别名 + 供应商」。这条规则必须成对决定:两条独立的 <c>??</c> 会拼出「A 家的别名 + B 家的锁」,候选集返回空,
    /// 然后报一条指着 B 的错,而 B 跟这次调用根本没关系。三档来源,越具体的越优先:子代理自己声明的 > 设置里的「默认子代理」(空别名 = 跟随) > 父 Run。
    /// 声明了别名但没声明供应商 = 只认别名,按优先级择优,<b>不是</b>沿用父亲锁的那家。
    /// </summary>
    public static ModelSelection ForSubagent(ModelSelection? declared, ModelSelection configured, ModelSelection parent)
    {
        if (declared is not null)
        {
            return declared;
        }

        return configured.Alias.Trim().Length > 0 ? configured : parent;
    }
}

/// <summary>
/// 别名表变动(删供应商、取消勾选、改名)之后,指向它的「模型 + 供应商」配对该怎么修。纯函数。
/// <b>一次都不做跨供应商的静默回退</b>:用户选的那家没了,唯一允许的降级是把供应商抹成 null——那不是「换一家给你」,
/// 是回到「没有指定」这个明确状态(它的语义——按优先级择优——全系统只有一份定义)。
/// 判据用的是「别名还在不在」,<b>不看供应商启没启用</b>:停用是可逆的,用户明天会开回来,这时改写他的选择等于他重新启用之后发现自己选的东西没了。
/// </summary>
public static class ModelSelectionRepair
{
    /// <returns>null = 不用改;否则是要写进设置的新配对。</returns>
    public static ModelSelection? Repair(
        ModelSelection current,
        IReadOnlySet<string> aliveBindings,
        IReadOnlySet<string> aliveAliases,
        ModelSelection fallback)
    {
        if (current.IsEmpty)
        {
            return null;
        }

        if (current.ProviderId is { } providerId)
        {
            if (aliveBindings.Contains(ModelSelections.Key(providerId, current.Alias)))
            {
                return null;
            }

            // 那家没了,但别名还在别处——只解锁,不改名。
            return aliveAliases.Contains(current.Alias) ? new ModelSelection(current.Alias) : fallback;
        }

        return aliveAliases.Contains(current.Alias) ? null : fallback;
    }

    /// <summary>从别名表算出「还活着的」两个集合(<see cref="Repair"/> 的入参)。</summary>
    public static (IReadOnlySet<string> Bindings, IReadOnlySet<string> Aliases) Alive(IEnumerable<ModelAliasRecord> aliases)
    {
        var list = aliases.ToList();
        return (
            list.Select(a => ModelSelections.Key(a.ProviderId, a.Alias)).ToHashSet(StringComparer.Ordinal),
            list.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal));
    }
}

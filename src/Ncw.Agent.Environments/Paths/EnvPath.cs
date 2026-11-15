namespace Ncw.Agent.Environments;

/// <summary>
/// 带路径风格的路径(06 §2):避免把本机路径和远程路径混用。
/// 构造时一律规范化;<see cref="Value"/> 是该风格下的原生写法(Windows 用 <c>\</c>)。
/// 路径守卫按环境根目录检查,见 <see cref="PathGuard"/>。
/// </summary>
public readonly record struct EnvPath
{
    private readonly string? _value;

    private EnvPath(string value, PathStyle style)
    {
        _value = value;
        Style = style;
    }

    public string Value => _value ?? "";

    public PathStyle Style { get; }

    public bool IsAbsolute => PathRules.IsAbsolute(Style, Value);

    /// <summary>解析并规范化。输入无效(含 NUL、Windows 盘符相对路径等)时抛 <see cref="EnvironmentErrorCode.InvalidPath"/>。</summary>
    public static EnvPath Parse(string value, PathStyle style) => new(PathRules.Normalize(style, value), style);

    /// <summary>同 <see cref="Parse"/>,但要求是绝对路径。</summary>
    public static EnvPath Absolute(string value, PathStyle style)
    {
        if (!PathRules.IsAbsolute(style, value))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidPath, "absolute path required");
        }

        return Parse(value, style);
    }

    public static EnvPath Posix(string value) => Parse(value, PathStyle.Posix);

    public static EnvPath Windows(string value) => Parse(value, PathStyle.Windows);

    /// <summary>接在后面(node 的 <c>join</c> 语义,不重置)。参数是相对写法;要处理「绝对输入优先」用 <see cref="PathGuard"/>。</summary>
    public EnvPath Join(params string[] parts) => new(PathRules.Join(Style, [Value, .. parts]), Style);

    public EnvPath Parent => new(PathRules.Dirname(Style, Value), Style);

    public string FileName => PathRules.Basename(Style, Value);

    public string Extension => PathRules.Extname(Style, Value);

    /// <summary>相对 <paramref name="root"/> 的写法,分隔符统一用 <c>/</c>。</summary>
    public string RelativeTo(EnvPath root)
    {
        RequireSameStyle(root);
        return PathRules.Relative(Style, root.Value, Value);
    }

    public bool IsWithin(EnvPath root)
    {
        RequireSameStyle(root);
        return PathRules.IsInside(Style, root.Value, Value);
    }

    public override string ToString() => Value;

    private void RequireSameStyle(EnvPath other)
    {
        if (other.Style != Style)
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidPath, "path style mismatch");
        }
    }
}

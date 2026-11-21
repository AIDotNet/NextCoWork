namespace Ncw.Core.Domain;

/// <summary>参与匹配的模版(地址、协议、所属服务与类型)。</summary>
public readonly record struct TemplateRef(string Id, string Service, string BaseUrl, string Protocol, string Kind = ProviderKinds.Text);

/// <summary>
/// 一个供应商(或自动发现的候选)对应哪个内置模版:列表栏 / 详情 / 编辑弹窗的 logo 和「获取 key」链接都由它派生(docs/design/07 §3「添加供应商」)。
/// 先比完整地址,再比主机名(同主机优先同协议、同类型,例如火山方舟同时有文本 / 图片 / 视频三个模版),本机地址按端口。认不出为 null。
/// </summary>
public static class ProviderTemplateMatch
{
    public static TemplateRef? Find(string baseUrl, string protocol, IReadOnlyList<TemplateRef> templates, string? kind = null)
    {
        if (templates.Count == 0 || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var wantedKind = ProviderKinds.Normalize(kind) ?? ProviderEndpoints.KindOfProtocol(protocol) ?? ProviderKinds.Text;
        var explicitKind = ProviderKinds.Normalize(kind) is not null;
        var url = Normalize(uri);
        TemplateRef? exactUrlSameKind = null, exactUrl = null, hostAndProtocol = null, hostSameKind = null, host = null;
        foreach (var t in templates)
        {
            if (!Uri.TryCreate(t.BaseUrl, UriKind.Absolute, out var other) || !SameOrigin(uri, other))
            {
                continue;
            }

            var sameKind = t.Kind == wantedKind;
            if (explicitKind && !sameKind)
            {
                continue;
            }

            if (Normalize(other) == url)
            {
                if (t.Protocol == protocol && sameKind)
                {
                    return t;
                }

                if (sameKind)
                {
                    exactUrlSameKind ??= t;
                }

                exactUrl ??= t;
            }

            if (t.Protocol == protocol && sameKind)
            {
                hostAndProtocol ??= t;
            }

            if (sameKind)
            {
                hostSameKind ??= t;
            }

            host ??= t;
        }

        return exactUrlSameKind ?? exactUrl ?? hostAndProtocol ?? hostSameKind ?? host;
    }

    /// <summary>
    /// 新建供应商时建议的 id:模版 id 没被占就用它,占了就加 <c>-2</c>、<c>-3</c>……(不覆盖已有的)。
    /// </summary>
    public static string SuggestId(string templateId, IReadOnlyCollection<string> existingIds)
    {
        if (!existingIds.Contains(templateId))
        {
            return templateId;
        }

        for (var n = 2; ; n++)
        {
            var id = $"{templateId}-{n}";
            if (!existingIds.Contains(id))
            {
                return id;
            }
        }
    }

    // 本机地址只看端口(localhost / 127.0.0.1 都算);其余看主机名(不分大小写)
    private static bool SameOrigin(Uri a, Uri b) =>
        IsLocal(a) || IsLocal(b)
            ? IsLocal(a) && IsLocal(b) && a.Port == b.Port
            : a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocal(Uri uri) => uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(Uri uri) =>
        $"{(IsLocal(uri) ? "localhost" : uri.Host.ToLowerInvariant())}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
}

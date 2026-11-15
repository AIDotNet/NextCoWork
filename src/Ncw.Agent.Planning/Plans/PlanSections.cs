using Ncw.Agent.Kernel.Session;

namespace Ncw.Agent.Extensions.Planning.Plans;

/// <summary>状态块里和计划有关的段(发给模型的英文常量)。路径和计划正文都按不可信文本处理:削控制字符、中和提醒标签。</summary>
internal static class PlanSections
{
    public static string Active(string path) =>
        $"Active Plan mode file: {Clean(path, 256)}\n"
        + "It is the only file Write and Edit may touch in this mode, and it stays active across turns until the plan is approved or rejected. "
        + "Read it before changing it: earlier tool output in this conversation may have been compacted away, so anything you remember about its contents can be stale.";

    /// <summary>
    /// 已批准的计划(执行这份计划的 Run 要看见全文)。状态块的每一段最多 4000 字符,而计划可以有 1 MiB:按行切成多段,每段原样保留(段之间多一个空行)。
    /// </summary>
    public static IReadOnlyList<string> Approved(PlanExecutionContext plan)
    {
        var sections = new List<string>();
        var body = PromptText.NeutralizeReminderTags(PromptText.StripControlChars(plan.Content));
        var first = true;
        foreach (var chunk in Chunk(body, PlanService.SectionChars))
        {
            sections.Add(first ? $"Approved plan file: {Clean(plan.Path, 256)}\n\n{chunk}" : chunk);
            first = false;
        }

        return sections;
    }

    public static string ApprovedUnavailable(string path, string reason) =>
        $"Approved plan file: {Clean(path, 256)}\nThe user approved this plan for execution, but it could not be loaded: {reason} "
        + "Tell the user, and do not guess what the plan said.";

    internal static IEnumerable<string> Chunk(string text, int size)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + size);
            if (end < text.Length)
            {
                // 尽量在换行处切;一行就超长时硬切(不切开代理对)
                var cut = text.LastIndexOf('\n', end - 1, end - start);
                if (cut > start)
                {
                    end = cut + 1;
                }
                else if (char.IsHighSurrogate(text[end - 1]))
                {
                    end--;
                }
            }

            yield return text[start..end];
            start = end;
        }
    }

    private static string Clean(string text, int max) => PromptText.Clamp(PromptText.NeutralizeReminderTags(PromptText.StripControlChars(text)), max);
}

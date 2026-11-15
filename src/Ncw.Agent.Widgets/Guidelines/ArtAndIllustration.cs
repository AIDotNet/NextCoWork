namespace Ncw.Agent.Widgets.Guidelines;

// 取自 2.x `src/main/kernel/tool/builtin/visualize-guidelines/art.ts`:这是发给模型的设计规范,正文逐字不改(2.x 的原则:改动任何一个数值都会让模型稳定地画坏图)。
// 来源与版权说明见 2.x 原文件头(Anthropic `visualize:read_me` 响应,经 pi-generative-ui(MIT)抽取)。常量字符串:NativeAOT 下不读资源、不用反射。
internal static partial class GuidelineText
{
    public const string ArtAndIllustration = """
## Art and illustration
*"Draw me a sunset" / "Create a geometric pattern"*

Use `imagine_svg`. Same technical rules (viewBox, safe area) but the aesthetic is different:
- Fill the canvas — art should feel rich, not sparse
- Bold colors: mix `--color-text-*` categories for variety (info blue, success green, warning amber)
- Art is the one place custom `<style>` color blocks are fine — freestyle colors, `prefers-color-scheme` for dark mode variants if you want them
- Layer overlapping opaque shapes for depth
- Organic forms with `<path>` curves, `<ellipse>`, `<circle>`
- Texture via repetition (parallel lines, dots, hatching) not raster effects
- Geometric patterns with `<g transform="rotate()">` for radial symmetry
""";
}

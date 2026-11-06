/**
 * 模型 / 供应商品牌图标 —— 直接用 lobehub 的 `@lobehub/icons-static-svg`。
 *
 * **为什么是 `icons-static-svg` 而不是 `@lobehub/icons`(React 版):**
 * React 版 peer-depends `@lobehub/ui@^5` + `antd@^6`,为了几个品牌字形把一整套
 * 组件库拖进来,会和我们手写的 Tailwind 设计系统正面打架。static-svg 是
 * **零依赖、零 peer**,只有 903 个 svg 文件。
 *
 * **为什么是 `?raw` 而不是 `?url` + `<img>`:**
 * lobehub 的单色图标全部 `fill="currentColor"` —— 这正是我们要的(图标跟着
 * 文字颜色走,深浅主题、hover、禁用态都不用各写一遍)。而 `<img src>` 里的 SVG
 * 是独立文档,**继承不到外部的 color**。所以必须内联。
 *
 * **关于 `dangerouslySetInnerHTML`:**
 * 注入的字符串全部来自**编译期常量** —— 下面 `MARK` 表里每一项都是
 * `import ... from '....svg?raw'`,由 Vite 在构建时读进 bundle。运行期输入
 * (`name`)只用来**挑选**是哪一个常量:`resolveBrand()` 的返回值域就是
 * `Brand`,而 `MARK` 的键正好是 `Brand` 去掉光栅那几个,不存在从运行期数据
 * 流进被注入字符串的路径。
 */
import { Sparkles } from 'lucide-react'
import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'
import { resolveBrand, type Brand } from './brands'

// 唯一一张自家的光栅图,理由见下面的 `RASTER`
import routinUrl from '../../../../../resources/images/routin-ai.webp'

import ai302 from '@lobehub/icons-static-svg/icons/ai302.svg?raw'
import aihubmix from '@lobehub/icons-static-svg/icons/aihubmix.svg?raw'
import atlascloud from '@lobehub/icons-static-svg/icons/atlascloud.svg?raw'
import anthropic from '@lobehub/icons-static-svg/icons/anthropic.svg?raw'
import azure from '@lobehub/icons-static-svg/icons/azure.svg?raw'
import baichuan from '@lobehub/icons-static-svg/icons/baichuan.svg?raw'
import baiducloud from '@lobehub/icons-static-svg/icons/baiducloud.svg?raw'
import bailian from '@lobehub/icons-static-svg/icons/bailian.svg?raw'
import bedrock from '@lobehub/icons-static-svg/icons/bedrock.svg?raw'
import claude from '@lobehub/icons-static-svg/icons/claude.svg?raw'
import cohere from '@lobehub/icons-static-svg/icons/cohere.svg?raw'
import deepinfra from '@lobehub/icons-static-svg/icons/deepinfra.svg?raw'
import deepseek from '@lobehub/icons-static-svg/icons/deepseek.svg?raw'
import doubao from '@lobehub/icons-static-svg/icons/doubao.svg?raw'
import fireworks from '@lobehub/icons-static-svg/icons/fireworks.svg?raw'
import gemini from '@lobehub/icons-static-svg/icons/gemini.svg?raw'
import groq from '@lobehub/icons-static-svg/icons/groq.svg?raw'
import hunyuan from '@lobehub/icons-static-svg/icons/hunyuan.svg?raw'
import kimi from '@lobehub/icons-static-svg/icons/kimi.svg?raw'
import lmstudio from '@lobehub/icons-static-svg/icons/lmstudio.svg?raw'
import menlo from '@lobehub/icons-static-svg/icons/menlo.svg?raw'
import meta from '@lobehub/icons-static-svg/icons/meta.svg?raw'
import minimax from '@lobehub/icons-static-svg/icons/minimax.svg?raw'
import mistral from '@lobehub/icons-static-svg/icons/mistral.svg?raw'
import moonshot from '@lobehub/icons-static-svg/icons/moonshot.svg?raw'
import ollama from '@lobehub/icons-static-svg/icons/ollama.svg?raw'
import openai from '@lobehub/icons-static-svg/icons/openai.svg?raw'
import opencode from '@lobehub/icons-static-svg/icons/opencode.svg?raw'
import openrouter from '@lobehub/icons-static-svg/icons/openrouter.svg?raw'
import perplexity from '@lobehub/icons-static-svg/icons/perplexity.svg?raw'
import qwen from '@lobehub/icons-static-svg/icons/qwen.svg?raw'
import sensenova from '@lobehub/icons-static-svg/icons/sensenova.svg?raw'
import siliconcloud from '@lobehub/icons-static-svg/icons/siliconcloud.svg?raw'
import spark from '@lobehub/icons-static-svg/icons/spark.svg?raw'
import stepfun from '@lobehub/icons-static-svg/icons/stepfun.svg?raw'
import together from '@lobehub/icons-static-svg/icons/together.svg?raw'
import vertexai from '@lobehub/icons-static-svg/icons/vertexai.svg?raw'
import vllm from '@lobehub/icons-static-svg/icons/vllm.svg?raw'
import volcengine from '@lobehub/icons-static-svg/icons/volcengine.svg?raw'
import xai from '@lobehub/icons-static-svg/icons/xai.svg?raw'
import xiaomimimo from '@lobehub/icons-static-svg/icons/xiaomimimo.svg?raw'
import yi from '@lobehub/icons-static-svg/icons/yi.svg?raw'
import zai from '@lobehub/icons-static-svg/icons/zai.svg?raw'
import zhipu from '@lobehub/icons-static-svg/icons/zhipu.svg?raw'

/**
 * ★ 光栅 logo —— 和上面那些 svg 不是一回事,所以单独一张表。
 *
 * RoutinAI 是内置上游(`BUILTIN_PROVIDER_ID`),lobehub 没收它的字形,而它的
 * logo 是一张**彩色** webp,不是单色剪影。于是两条现成的路都走不通:进不了
 * `MARK`(那些是 `?raw` 内联进来的 svg 源码),也不能照 `Mark.tsx` 那样用
 * CSS mask + `currentColor`(mask 只留形状、把颜色全丢掉)。就用 `<img>`。
 *
 * 拆成两张表、而不是把 `MARK` 的值类型放宽成联合,是为了保住下面那条
 * 「加了牌子忘了加图 = 编译错误」:`Exclude<Brand, RasterBrand>` 让两张表
 * **加起来恰好**盖满 `Brand` —— 少一个编不过,多一个也编不过。
 *
 * 将来再加一个光栅牌子:往 `RasterBrand` 里加一项之后,下面渲染处那个
 * `brand === 'routin'` 的窄化就不够了,`MARK[brand]` 当场报类型错。
 * 那正是想要的 —— 它逼着你把新那项也接上,而不是留一个空图标。
 */
type RasterBrand = 'routin'

const RASTER: Record<RasterBrand, string> = { routin: routinUrl }

/**
 * 静态列举而不是 `import.meta.glob` —— 全量 903 个图标是 1MB+,
 * 而我们真正认得的就这 42 个牌子(约 55KB)。
 *
 * 类型标成 `Record<…, string>`(不是 `as const` 反推),
 * 这样「在 brands.ts 加了一条规则却忘了在这里加导入」是**编译错误**,
 * 而不是运行时的一个空图标。
 */
const MARK: Record<Exclude<Brand, RasterBrand>, string> = {
  ai302, aihubmix, anthropic, atlascloud, azure, baichuan, baiducloud, bailian, bedrock,
  claude, cohere, deepinfra, deepseek, doubao, fireworks, gemini, groq,
  hunyuan, kimi, lmstudio, menlo, meta, minimax, mistral, moonshot, ollama,
  openai, opencode, openrouter, perplexity, qwen, sensenova, siliconcloud,
  spark, stepfun, together, vertexai, vllm, volcengine, xai, xiaomimimo, yi,
  zai, zhipu
}

/**
 * @param name  供应商名或模型别名;传数组则按顺序试,**最具体的放前面**。
 * @param label 传了就作为无障碍名字暴露;不传则整个图标 `aria-hidden` ——
 *              图标旁边通常已经有同样内容的文字了,读屏读两遍是噪音。
 * @param fallback 认不出牌子时显示它,而不是那颗默认的 `Sparkles`。
 *
 * ★ 认不出是**正常路径**:用户能配任何上游,内置预设里还剩三家
 * (OhMyGPT / LocalAI / llama.cpp)谁都没有字形 —— lobehub 没收,我们也没画。
 * 在一列供应商里让它们全变成同一颗 `Sparkles`,等于把「三个不同的家」显示成
 * 一模一样 —— 所以那些地方传首字母进来(见 `ProviderAvatar`)。`Sparkles`
 * 留作默认,是因为在正文流里(Composer / Thread)一颗星比一个孤零零的字母合适。
 *
 * fallback **不吃 `size`**:它是文字,该继承外面盒子的 `text-*`,
 * 而 `size` 是给 svg 的 `1em` 用的。
 */
export function ProviderIcon({
  name,
  size = 16,
  label,
  className,
  fallback
}: {
  name: string | readonly (string | undefined)[]
  size?: number
  label?: string
  className?: string
  fallback?: ReactNode
}): ReactNode {
  const brand = resolveBrand(...(typeof name === 'string' ? [name] : name))
  const a11y =
    label === undefined
      ? { 'aria-hidden': true as const }
      : { role: 'img' as const, 'aria-label': label }

  if (brand === null) {
    if (fallback === undefined) {
      return <Sparkles size={size} className={cn('shrink-0', className)} {...a11y} />
    }
    return (
      <span {...a11y} className={cn('shrink-0', className)}>
        {fallback}
      </span>
    )
  }

  if (brand === 'routin') {
    return (
      <img
        src={RASTER.routin}
        // `<img>` 的无障碍名就是 alt。空串 = 装饰性,读屏跳过 ——
        // 和上面那个 `aria-hidden` 是一回事,所以这里不铺 `a11y`(两套会打架)。
        alt={label ?? ''}
        width={size}
        height={size}
        className={cn('shrink-0', className)}
      />
    )
  }

  return (
    <span
      {...a11y}
      // lobehub 的 svg 是 width/height="1em",所以尺寸靠 font-size 给
      style={{ fontSize: `${size}px` }}
      className={cn('inline-flex shrink-0 items-center justify-center', className)}
      dangerouslySetInnerHTML={{ __html: MARK[brand] }}
    />
  )
}

/**
 * TSX widget 的**编译与静态校验** —— `visualize_show_widget` / `visualize_edit_widget` 共用。
 *
 * 需求:模型写 TSX,iframe 里跑的是 JS。编译放在**主进程、工具运行期**,而不是
 * iframe 里,理由只有一条:**错误要能在同一轮回到模型手上**。iframe 里编译失败,
 * 用户看到一块报错,而模型以为自己画完了;这里失败,模型拿到 `行:列` + 出错的那一行,
 * 下一次调用就能改对。
 *
 * ## 为什么是 sucrase
 *
 * 只要"去掉类型 + 转 JSX + 转 CJS"三件事,不要类型检查。sucrase 是纯 JS、
 * 能被 main 直接 require;`typescript` 是开发依赖且体积大,esbuild 是原生二进制
 * —— 两者都要另起一套打包/分发。
 *
 * ## 为什么转成 CJS
 *
 * iframe 的 CSP 禁 `unsafe-eval`、也不放 `blob:` 脚本,能执行的只有带 nonce 的内联
 * `<script>`。转成 CJS 后,外壳把它包进 `function (require, module, exports) {…}`,
 * `require` 是一张事先 import 好的模块表 —— 用户代码自己完全不碰 import map,
 * 白名单也就只需要在这里和那张表里各认一次。
 */
import { transform } from 'sucrase'
import { WIDGET_MODULES, type WidgetModule } from '../../../../shared/domain/widget'

export type CompileResult =
  | { ok: true; compiled: string; modules: WidgetModule[] }
  | { ok: false; error: string }

const ALLOWED: ReadonlySet<string> = new Set(WIDGET_MODULES)

/** 源码里出现的模块名(import / export from / 动态 import / require)。 */
function importedModules(source: string): string[] {
  const found = new Set<string>()
  const patterns = [
    /\bimport\s+(?:type\s+)?(?:[\w*{}\s,$]+\s+from\s+)?['"]([^'"]+)['"]/g,
    /\bexport\s+(?:type\s+)?[\w*{}\s,$]+\s+from\s+['"]([^'"]+)['"]/g,
    /\bimport\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
    /\brequire\s*\(\s*['"]([^'"]+)['"]\s*\)/g
  ]
  for (const pattern of patterns) {
    for (const match of source.matchAll(pattern)) found.add(match[1] as string)
  }
  return [...found]
}

/** 出错那一行带个 `^` —— 模型对"第 12 行第 40 列"的定位远不如直接看到那一行。 */
function excerpt(source: string, line: number, column: number): string {
  const text = source.split('\n')[line - 1]
  if (text === undefined) return ''
  const clipped = text.length > 160 ? `${text.slice(0, 160)}…` : text
  return `\n  ${clipped}\n  ${' '.repeat(Math.min(column, 160))}^`
}

export function compileWidgetTsx(source: string): CompileResult {
  const imports = importedModules(source)
  const rejected = imports.filter((name) => !ALLOWED.has(name))
  if (rejected.length > 0) {
    return {
      ok: false,
      error:
        `Cannot import ${rejected.map((name) => `"${name}"`).join(', ')}. TSX widgets can only import: ` +
        `${WIDGET_MODULES.join(', ')}. Everything else (including other npm packages and relative paths) is unavailable.`
    }
  }
  /*
    ★ 动态 import 在白名单之内也不放:外壳只给同步的 `require`,`import()` 会被
    sucrase 转成 `Promise.resolve().then(() => require(...))` 能跑,但模型写它
    通常是想按需加载别的东西 —— 那条路不存在,早说早改。
  */
  if (/\bimport\s*\(/.test(source)) {
    return { ok: false, error: 'Dynamic import() is not supported in TSX widgets. Use static imports at the top of the file.' }
  }
  if (!/\bexport\s+default\b/.test(source)) {
    return {
      ok: false,
      error: 'A TSX widget must have a default export: `export default function Widget() { … }`.'
    }
  }

  let compiled: string
  try {
    compiled = transform(source, {
      transforms: ['typescript', 'jsx', 'imports'],
      jsxRuntime: 'automatic',
      production: true,
      filePath: 'widget.tsx'
    }).code
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err)
    const loc = (err as { loc?: { line?: unknown; column?: unknown } }).loc
    const where =
      loc !== undefined && typeof loc.line === 'number' && typeof loc.column === 'number'
        ? excerpt(source, loc.line, loc.column)
        : ''
    return { ok: false, error: `TSX syntax error: ${message}${where}` }
  }

  /*
    ★ JSX 的自动运行时会**额外**引入 `react/jsx-runtime`,源码里看不到这条 import。
    模块表按编译产物重新数一遍,外壳据此决定要预先加载哪几份运行时 ——
    没用到 charts 的 widget 不该为它多读一个几百 KB 的文件。
  */
  const modules = importedModules(compiled).filter((name): name is WidgetModule => ALLOWED.has(name))
  return { ok: true, compiled, modules }
}

export { inferWidgetFormat } from '../../../../shared/domain/widget'

/**
 * 架构边界测试 —— 用 TypeScript AST 读 `src/**` 的**值** import,断言几条
 * 「少了就会安静地坏掉」的依赖方向,并把本次解耦掉的那几条环钉住。
 *
 * ## 为什么是 AST 而不是正则
 *
 * 正则会被注释里的 `import` 骗到(本仓库的注释里大量出现示例 import ——
 * `oauth/registry.ts` 的注释里就写着 `transport.ts`,正则会把那条**注释**当成
 * 一条真依赖,于是既漏报真环、又误报假环)。AST 不会。
 *
 * ## 只算「值」边
 *
 * - `import type { X } from …` / `import { type X } from …`(整条子句都是 type)
 *   编译后被擦除,**不构成运行时依赖** —— 例如 `oauth/registry.ts` 对
 *   `upstream/transport.ts` 就只有 type-only 边,那是有意为之,不算环。
 * - 动态 import/require 不算静态循环边，但必须计入独立 Agent 和跨端客户端的
 *   运行时可达检查。延迟加载不能成为把 Electron 偷带进无头运行时的出口。
 *
 * ## 范围
 *
 * 只扫 `src/`(跳过 `__tests__`、`node_modules`、`dist`、`out`、`build`、
 * `.vite`、`coverage` 等)。断言的**只有**下面列出的约束与本次真的修掉的环 ——
 * 不去要求「全仓一个环都不许有」(比如 `plugin ⇄ ipc` 那条由别处负责)。
 */
import { readFileSync, readdirSync, statSync, existsSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

const SRC = resolve(process.cwd(), 'src')
const SKIP_DIRS = new Set(['node_modules', '__tests__', 'dist', 'out', 'build', '.vite', 'coverage', '.next-cowork'])
const TS_EXTS = ['.ts', '.tsx']

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry)
    const info = statSync(full)
    if (info.isDirectory()) {
      if (SKIP_DIRS.has(entry)) continue
      walk(full, out)
    } else if ((entry.endsWith('.ts') || entry.endsWith('.tsx')) && !entry.endsWith('.d.ts')) {
      // 测试文件跑在 Node 里,可以用 node/electron builtin —— 架构约束只管生产代码。
      if (/\.test\.tsx?$/.test(entry)) continue
      out.push(full)
    }
  }
  return out
}

function resolveSpecifier(fromFile: string, spec: string): string | null {
  const aliased = spec.startsWith('@shared/') ? resolve(SRC, 'shared', spec.slice(8))
    : spec.startsWith('@main/') ? resolve(SRC, 'main', spec.slice(6))
      : spec.startsWith('@renderer/') ? resolve(SRC, 'renderer/src', spec.slice(10)) : null
  if (aliased === null && !spec.startsWith('.')) return null
  const base = (aliased ?? resolve(dirname(fromFile), spec)).replace(/\.(?:js|mjs)$/, '')
  for (const candidate of [
    base,
    ...TS_EXTS.map((ext) => base + ext),
    join(base, 'index.ts'),
    join(base, 'index.tsx')
  ]) {
    if (existsSync(candidate)) return candidate
  }
  return null
}

function isTypeOnlyImport(node: ts.ImportDeclaration): boolean {
  const clause = node.importClause
  if (clause === undefined) return false
  if (clause.isTypeOnly) return true
  const bindings = clause.namedBindings
  if (bindings !== undefined && ts.isNamedImports(bindings)) {
    return clause.name === undefined && bindings.elements.length > 0
      && bindings.elements.every((element) => element.isTypeOnly)
  }
  return false
}

interface Edges {
  /** 静态值 import(含 `export … from`)。 */
  value: Set<string>
  /** 静态 type-only import。 */
  type: Set<string>
  /** 裸模块说明符(非相对)的值 import,例如 `'electron'` / `'node:fs'`。 */
  bare: Set<string>
  /** 与静态循环分析分开，但无头边界检查必须遍历。 */
  dynamic?: Set<string>
  dynamicBare?: Set<string>
}

function edgesOf(file: string, text = readFileSync(file, 'utf8')): Edges {
  const kind = file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS
  const source = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, kind)
  const value = new Set<string>()
  const type = new Set<string>()
  const bare = new Set<string>()
  const dynamic = new Set<string>()
  const dynamicBare = new Set<string>()
  const requireNames = new Set(['require'])
  const factories = new Set<string>()
  // createRequire 的本地别名同样能加载模块，不能只认字面量 require。
  for (const statement of source.statements) {
    if (ts.isImportDeclaration(statement) && ts.isStringLiteral(statement.moduleSpecifier)
      && statement.moduleSpecifier.text === 'node:module' && statement.importClause?.namedBindings
      && ts.isNamedImports(statement.importClause.namedBindings)) {
      for (const binding of statement.importClause.namedBindings.elements) {
        if (!binding.isTypeOnly && (binding.propertyName?.text ?? binding.name.text) === 'createRequire') factories.add(binding.name.text)
      }
    }
  }
  const aliases = (node: ts.Node): void => {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer
      && ts.isCallExpression(node.initializer) && ts.isIdentifier(node.initializer.expression)
      && factories.has(node.initializer.expression.text)) requireNames.add(node.name.text)
    ts.forEachChild(node, aliases)
  }
  aliases(source)

  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      const spec = node.moduleSpecifier.text
      const target = resolveSpecifier(file, spec)
      if (target !== null) (isTypeOnlyImport(node) ? type : value).add(target)
      else if (isTypeOnlyImport(node)) { /* 类型化的裸模块 —— 运行时无依赖 */ }
      else bare.add(spec)
    } else if (ts.isExportDeclaration(node) && node.moduleSpecifier !== undefined && ts.isStringLiteral(node.moduleSpecifier)) {
      const target = resolveSpecifier(file, node.moduleSpecifier.text)
      const onlyTypes = node.isTypeOnly || (node.exportClause !== undefined && ts.isNamedExports(node.exportClause)
        && node.exportClause.elements.length > 0 && node.exportClause.elements.every((element) => element.isTypeOnly))
      if (target !== null) (onlyTypes ? type : value).add(target)
      else if (!onlyTypes) bare.add(node.moduleSpecifier.text)
    }
    if (ts.isCallExpression(node) && (node.expression.kind === ts.SyntaxKind.ImportKeyword
      || (ts.isIdentifier(node.expression) && requireNames.has(node.expression.text)))) {
      const spec = node.arguments[0]
      if (spec && (ts.isStringLiteral(spec) || ts.isNoSubstitutionTemplateLiteral(spec))) {
        const target = resolveSpecifier(file, spec.text)
        if (target !== null) dynamic.add(target)
        else dynamicBare.add(spec.text)
      } else dynamicBare.add('<computed-module>')
    }
    if (ts.isImportEqualsDeclaration(node) && !node.isTypeOnly && ts.isExternalModuleReference(node.moduleReference)) {
      const spec = node.moduleReference.expression
      if (ts.isStringLiteral(spec)) {
        const target = resolveSpecifier(file, spec.text)
        if (target !== null) value.add(target)
        else bare.add(spec.text)
      }
    }
    ts.forEachChild(node, visit)
  }
  visit(source)
  return { value, type, bare, dynamic, dynamicBare }
}

function buildGraph(): Map<string, Edges> {
  const graph = new Map<string, Edges>()
  for (const file of walk(SRC)) graph.set(file, edgesOf(file))
  return graph
}

const GRAPH = buildGraph()
const rel = (file: string): string => relative(SRC, file).split('\\').join('/')

/**
 * 强连通分量(Tarjan),只在**值**边上跑。返回 size>1 的环(节点路径,已排序)。
 *
 * ★ 抽成接收图的函数而不是只绑 `GRAPH`,是为了能用一张**合成图**证明它真的
 *   会检出环(见最后一个 `it`)—— 否则 Tarjan 写错时下面每条「无环」断言都会
 *   假绿,那比没有测试更糟。
 */
function valueCyclesOf(graph: Map<string, Edges>): string[][] {
  const index = new Map<string, number>()
  const low = new Map<string, number>()
  const onStack = new Set<string>()
  const stack: string[] = []
  const cycles: string[][] = []
  let counter = 0

  const connect = (node: string): void => {
    index.set(node, counter)
    low.set(node, counter)
    counter += 1
    stack.push(node)
    onStack.add(node)
    for (const next of graph.get(node)?.value ?? []) {
      if (!graph.has(next)) continue
      if (!index.has(next)) {
        connect(next)
        low.set(node, Math.min(low.get(node)!, low.get(next)!))
      } else if (onStack.has(next)) {
        low.set(node, Math.min(low.get(node)!, index.get(next)!))
      }
    }
    if (low.get(node) === index.get(node)) {
      const component: string[] = []
      let current: string
      do {
        current = stack.pop()!
        onStack.delete(current)
        component.push(rel(current))
      } while (current !== node)
      if (component.length > 1) cycles.push(component.sort())
    }
  }
  for (const node of graph.keys()) if (!index.has(node)) connect(node)
  return cycles
}

function runtimeViolations(graph: Map<string, Edges>, roots: string[], disallowedFile: (file: string) => boolean,
  disallowedModule: (specifier: string) => boolean): string[] {
  const violations: string[] = []
  const seen = new Set<string>()
  const queue = roots.map((root) => [root])
  for (let index = 0; index < queue.length; index++) {
    const chain = queue[index]!
    const file = chain[chain.length - 1]!
    if (seen.has(file)) continue
    seen.add(file)
    if (disallowedFile(file)) { violations.push(chain.map(rel).join(' → ')); continue }
    const edges = graph.get(file)
    if (!edges) continue
    for (const spec of new Set([...edges.bare, ...(edges.dynamicBare ?? [])])) {
      if (disallowedModule(spec)) violations.push(`${chain.map(rel).join(' → ')} → ${spec}`)
    }
    for (const target of new Set([...edges.value, ...(edges.dynamic ?? [])])) queue.push([...chain, target])
  }
  return violations
}

describe('架构边界 · 值 import', () => {
  it('默认值导入不能被同一语句的 type 成员掩盖，裸模块值重导出也要检查', () => {
    const edges = edgesOf(join(SRC, 'renderer/src/__synthetic.ts'),
      "import fs, { type Stats } from 'node:fs'; export { readFile } from 'node:fs/promises'; export { type PathLike } from 'node:fs';")
    expect([...edges.bare].sort()).toEqual(['node:fs', 'node:fs/promises'])
  })

  it('路径别名与 .js 说明符解析到真正的源码，不绕过跨层限制', () => {
    const from = join(SRC, 'renderer/src/__synthetic.ts')
    expect(resolveSpecifier(from, '@main/ipc/errors')).toBe(join(SRC, 'main/ipc/errors.ts'))
    expect(resolveSpecifier(from, '@shared/agent/error.js')).toBe(join(SRC, 'shared/agent/error.ts'))
    expect(resolveSpecifier(from, '@renderer/stores/session')).toBe(join(SRC, 'renderer/src/stores/session.ts'))
  })

  it('文件传输服务不反向依赖 store 或 application actions', () => {
    const edges = GRAPH.get(join(SRC, 'renderer/src/services/workspace-files.ts'))!
    const outward = [...edges.value].map(rel)
    expect(outward.filter((file) => file.startsWith('renderer/src/stores/') || file.startsWith('renderer/src/actions/'))).toEqual([])
  })

  it('数据统计 IPC 接到异步有界遍历，而不是同步全目录扫描', () => {
    const file = join(SRC, 'main/ipc/index.ts')
    const source = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true)
    const calls: string[] = []
    const visit = (node: ts.Node): void => {
      if (ts.isPropertyAssignment(node) && ts.isStringLiteral(node.name) && node.name.text === 'storage:getStats'
        && ts.isArrowFunction(node.initializer) && ts.isCallExpression(node.initializer.body)
        && ts.isIdentifier(node.initializer.body.expression)) calls.push(node.initializer.body.expression.text)
      ts.forEachChild(node, visit)
    }
    visit(source)
    expect(calls).toEqual(['getStatsAsync'])
  })

  it('① src/shared 不反向依赖 main / renderer(类型也是)', () => {
    const violations: string[] = []
    for (const [file, edges] of GRAPH) {
      const from = rel(file)
      if (!from.startsWith('shared/')) continue
      for (const target of new Set([...edges.value, ...edges.type])) {
        const to = rel(target)
        if (to.startsWith('main/') || to.startsWith('renderer/')) violations.push(`${from} → ${to}`)
      }
    }
    expect(violations).toEqual([])
  })

  it('② renderer 不依赖 main / Electron / Node 内建模块', () => {
    const violations: string[] = []
    for (const [file, edges] of GRAPH) {
      const from = rel(file)
      if (!from.startsWith('renderer/')) continue
      for (const target of edges.value) {
        if (rel(target).startsWith('main/')) violations.push(`${from} → ${rel(target)}`)
      }
      for (const spec of edges.bare) {
        if (spec === 'electron' || spec.startsWith('node:') || ['fs', 'path', 'os', 'crypto', 'child_process'].includes(spec)) {
          violations.push(`${from} → ${spec}`)
        }
      }
    }
    expect(violations).toEqual([])
  })

  it('③ runtime 零 Electron / ipc / window **直接**值依赖 —— 无头链路的铁律', () => {
    // 保留直接层次约束；下方再检查包括动态加载的完整源码依赖闭包。
    const runtimeFile = resolve(SRC, 'main/runtime.ts')
    const edges = GRAPH.get(runtimeFile)
    expect(edges).toBeDefined()
    const violations: string[] = []
    for (const spec of edges!.bare) if (spec === 'electron') violations.push(`runtime.ts → ${spec}`)
    for (const target of edges!.value) {
      const to = rel(target)
      if (to.startsWith('main/ipc/') || to.startsWith('main/window/')) violations.push(`runtime.ts → ${to}`)
    }
    expect(violations).toEqual([])
  })

  it('④ kernel 不直接依赖 db / state/store(值 import)', () => {
    const violations: string[] = []
    for (const [file, edges] of GRAPH) {
      const from = rel(file)
      if (!from.startsWith('main/kernel/')) continue
      for (const target of edges.value) {
        const to = rel(target)
        if (to.startsWith('main/db/') || to === 'main/state/store.ts') violations.push(`${from} → ${to}`)
      }
    }
    expect(violations).toEqual([])
  })
})

describe('设备服务 · 运行时依赖闭包', () => {
  it('识别动态 import、require、createRequire 别名和 import-equals，忽略类型导入', () => {
    const edges = edgesOf(join(SRC, 'main/__synthetic.ts'), `
      import type { WebContents } from 'electron';
      import { createRequire as factory } from 'node:module';
      const load = factory(import.meta.url);
      await import('@main/window/registry');
      require('electron');
      load('node:fs');
      import updater = require('electron-updater');
      await import(variableName);
    `)
    expect([...edges.dynamic!]).toEqual([join(SRC, 'main/window/registry.ts')])
    expect([...edges.dynamicBare!].sort()).toEqual(['<computed-module>', 'electron', 'node:fs'])
    expect([...edges.bare].sort()).toEqual(['electron-updater', 'node:module'])
  })

  it('传递边不会漏掉间接动态导入的 Electron，也不会因循环无限遍历', () => {
    const a = join(SRC, 'main/a.ts'), b = join(SRC, 'main/b.ts')
    const graph = new Map<string, Edges>([
      [a, { value: new Set([b]), type: new Set(), bare: new Set() }],
      [b, { value: new Set([a]), type: new Set(), bare: new Set(), dynamicBare: new Set(['electron']) }]
    ])
    expect(runtimeViolations(graph, [a], () => false, (spec) => spec === 'electron'))
      .toEqual(['main/a.ts → main/b.ts → electron'])
  })

  it('Agent 装配、附件与后台运行时不能传递加载 Electron 或窗口模块', () => {
    const roots = ['main/runtime.ts', 'main/session-runtime.ts', 'main/agent-shells.ts', 'main/services/attachments.ts']
      .map((file) => join(SRC, file))
    for (const root of roots) expect(GRAPH.has(root)).toBe(true)
    expect(runtimeViolations(GRAPH, roots,
      (file) => rel(file).startsWith('renderer/') || rel(file).startsWith('main/window/'),
      (spec) => spec === 'electron' || spec === 'electron-updater' || spec.startsWith('@electron-') || spec === '<computed-module>'))
      .toEqual([])
  })

  it('共享控制端只依赖平台无关源码与经过选择的纯协议库', () => {
    const roots = [...GRAPH.keys()].filter((file) => rel(file).startsWith('controller-client/') || rel(file).startsWith('shared/device/'))
    expect(roots.length).toBeGreaterThan(0)
    expect(runtimeViolations(GRAPH, roots,
      (file) => !rel(file).startsWith('controller-client/') && !rel(file).startsWith('shared/'),
      (spec) => !['zod', '@msgpack/msgpack'].includes(spec))).toEqual([])
  })
})

describe('架构边界 · 本次解耦掉的静态值环', () => {
  const cycles = valueCyclesOf(GRAPH)
  const hasCycleThrough = (...fragments: string[]): boolean =>
    cycles.some((component) => fragments.every((fragment) => component.some((file) => file.includes(fragment))))

  it('检环器本身有效:a ⇄ b 的合成图必须被检出', () => {
    const a = join(SRC, 'main', '__synthetic_a.ts')
    const b = join(SRC, 'main', '__synthetic_b.ts')
    const synthetic = new Map<string, Edges>([
      [a, { value: new Set([b]), type: new Set(), bare: new Set() }],
      [b, { value: new Set([a]), type: new Set(), bare: new Set() }]
    ])
    expect(valueCyclesOf(synthetic)).toEqual([['main/__synthetic_a.ts', 'main/__synthetic_b.ts']])
  })

  it('① runtime ⇄ imports/service 不再成环(受管说明路径走纯 helper)', () => {
    expect(hasCycleThrough('main/runtime.ts', 'main/imports/service.ts')).toBe(false)
  })

  it('② runtime ▸ scheduled/bridge ▸ scheduler 不再回到 runtime', () => {
    expect(hasCycleThrough('main/runtime.ts', 'main/scheduled/bridge.ts')).toBe(false)
  })

  it('③ runtime ⇄ hooks ⇄ goal 不再成环(钩子宿主口注入)', () => {
    expect(hasCycleThrough('main/runtime.ts', 'main/hooks.ts')).toBe(false)
    expect(hasCycleThrough('main/hooks.ts', 'main/goal/runtime.ts')).toBe(false)
  })

  it('④ oauth/registry ⇄ upstream/transport 不再成环(稳定 id 抽成纯 helper)', () => {
    expect(hasCycleThrough('main/kernel/oauth/registry.ts', 'main/kernel/upstream/transport.ts')).toBe(false)
  })

  it('renderer 文档、Tab 与窗口 store 不再经文件服务互相成环', () => {
    expect(hasCycleThrough('renderer/src/stores/tabs.ts', 'renderer/src/stores/documents.ts')).toBe(false)
    expect(hasCycleThrough('renderer/src/stores/window.ts', 'renderer/src/stores/tabs.ts')).toBe(false)
    expect(hasCycleThrough('renderer/src/services/workspace-files.ts', 'renderer/src/stores/documents.ts')).toBe(false)
  })

  it('⑤ 本次改动的边界模块之间不新增环', () => {
    const boundary = [
      'main/runtime.ts',
      'main/hooks.ts',
      'main/goal/runtime.ts',
      'main/scheduled/bridge.ts',
      'main/scheduled/scheduler.ts',
      'main/imports/service.ts',
      'main/kernel/approval.ts',
      'main/kernel/oauth/registry.ts',
      'main/kernel/upstream/transport.ts',
      'main/kernel/upstream/ids.ts',
      'main/kernel/tool/skill-port.ts',
      'main/imports/managed-paths.ts',
      'main/scheduled/refresh.ts'
    ]
    const offenders = cycles.filter((component) => component.filter((file) => boundary.includes(file)).length > 1)
    expect(offenders).toEqual([])
  })
})

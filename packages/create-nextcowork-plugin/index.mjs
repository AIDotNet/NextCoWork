#!/usr/bin/env node
/**
 * `create-nextcowork-plugin` —— 脚手架。
 *
 * ## 它要解决的是「30 分钟跑通 hello world」这一条
 *
 * 那条验收标准里最容易卡住的不是写代码,是**清单**:`engines` 怎么写、
 * `main` 指哪、`title` 为什么必须是 `%key%`、`l10n` 为什么两种语言都要。
 * 每一条写错的症状都是「装上了但没反应」,而错误信息离原因很远。
 *
 * 所以这个脚手架生成的是一份**已经能上架的**骨架:填三个字段,
 * `npm run package` 出来的 ZIP 直接过服务端校验。
 *
 * ## 为什么不问一堆问题
 *
 * 只问三样:publisher、name、显示名。其余全给默认值 —— 一个还没跑通
 * hello world 的人,回答不了「你需要哪些能力」。能力等他真的要用时再加,
 * 那时候他知道自己在要什么。
 */
import { cp, mkdir, readFile, readdir, writeFile } from 'node:fs/promises'
import { createInterface } from 'node:readline/promises'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const TEMPLATE = join(here, 'template')
/** `--view` 时叠加上去的那份:一个用宿主 React 与控件写的自定义编辑器。 */
const VIEW_TEMPLATE = join(here, 'template-view')

/** 与客户端 `PLUGIN_NAME_RE` 同一形状。这里先挡一次,免得到上传才被拒。 */
const NAME_RE = /^[a-z0-9][a-z0-9-]{0,63}$/

/**
 * 默认的 engines range。
 *
 * ★ 这里声明的是**插件 API 版本**,不是应用版本 —— 两者曾被当成同一个,结果是
 * 宿主拿 `app.getVersion()`(2.x)去比 `^0.2.0`,按本模板生成的插件装上一律
 * 显示「装载失败」。判定落点见 `shared/plugin/api-version.ts`。
 *
 * ★ `^0.x` 按 npm 的规矩锁到 **minor** —— 插件 API 在 1.0 之前明确可以 break,
 * 写 `>=0.3.0` 的插件会在下一个 minor 里静默坏掉,而作者不会收到任何通知。
 *
 * ★ 与 `PLUGIN_API_VERSION` 必须同步;`shared/plugin/__tests__/scaffold.test.ts`
 * 直接读这一行来钉住它,不再自己抄一份默认值。
 */
const DEFAULT_ENGINES = '^0.3.3'

async function main() {
  const args = process.argv.slice(2)
  const flag = (key) => {
    const index = args.indexOf(`--${key}`)
    return index === -1 ? undefined : args[index + 1]
  }
  const target = resolve(args.find((value) => !value.startsWith('--') && !isFlagValue(args, value)) ?? '.')

  /*
    ★ **非交互时不提问。** readline 的 `question()` 在 stdin 已经结束之后
    永远不会 resolve —— 而那时进程会因为「事件循环空了」以退出码 0 悄悄退出,
    什么都没生成,也没有任何错误。这是 CI 里最难查的一类失败。
    所以:有 TTY 才问,没有就用 flag / 默认值。
  */
  // `--view`:连带生成一个 React 视图(自定义编辑器)。见下面生成处的说明。
  const wantsView = args.includes('--view')
  const interactive = process.stdin.isTTY === true && flag('yes') === undefined && !args.includes('--yes')
  const rl = interactive ? createInterface({ input: process.stdin, output: process.stdout }) : null

  const publisher = flag('publisher') ?? (await ask(rl, 'publisher(你的发布者名,小写字母数字与连字符)', 'acme'))
  const name = flag('name') ?? (await ask(rl, 'name(插件名)', 'hello'))
  const displayName = flag('display-name') ?? (await ask(rl, '显示名', 'Hello'))
  const description = flag('description') ?? (await ask(rl, '一句话描述', 'A NextCoWork plugin'))
  rl?.close()

  if (!NAME_RE.test(publisher) || !NAME_RE.test(name)) {
    console.error('✗ publisher 与 name 必须匹配 ^[a-z0-9][a-z0-9-]{0,63}$')
    process.exit(1)
  }

  const dir = join(target, `${publisher}.${name}`)
  await mkdir(dir, { recursive: true })
  await cp(TEMPLATE, dir, { recursive: true })

  /*
    ★ 占位符替换要**连 l10n key 一起改**:`showMessage` 传的 key 在宿主那边
    会被拼成 `plugin.<publisher>.<name>.<key>`,而模板里的调用写的是完整形式。
    只改 package.json 的话,生成出来的插件一按就显示一串 key,
    而作者完全看不出哪里错了。
  */
  const replacements = {
    __PUBLISHER__: publisher,
    __NAME__: name,
    __DISPLAY_NAME__: displayName,
    __DESCRIPTION__: description,
    __ENGINES__: DEFAULT_ENGINES
  }
  for (const file of await walk(dir)) {
    const raw = await readFile(file, 'utf8')
    let next = raw
    for (const [token, value] of Object.entries(replacements)) next = next.replaceAll(token, value)
    if (next !== raw) await writeFile(file, next)
  }

  /*
    `--view`:再生成一个**用 React 写的自定义编辑器**。

    ★ 为什么一定是自定义编辑器,而不是一个独立面板:插件视图想被打开,当前只有
    两条路 —— 绑定到某类文件(customEditors),或者是一个网址(webApps)。
    `contributes.views` 里 location 为 sidebar/panel 的那种解析得了、装得上,
    但**没有任何地方能打开它**(插件详情页会给一条诊断)。脚手架生成一个点不开
    的东西,比不生成更糟:作者会以为是自己写错了。

    ★ 绑定到 `*.<name>`(如 `*.hello`)只是一个能立刻跑起来的默认值。
    改 selector 就能接管别的后缀 —— 但**别用 `*`**:那会让这个插件抢走所有文件
    的打开方式,包括代码文件。
  */
  if (wantsView) {
    await cp(VIEW_TEMPLATE, dir, { recursive: true })
    const pkgPath = join(dir, 'package.json')
    const pkg = JSON.parse(await readFile(pkgPath, 'utf8'))
    const viewType = `${publisher}.${name}.editor`
    /*
      `views` 是给 `nextcowork-plugin build` 看的:源文件 → 产物。
      它**不是** `contributes.views`(那是给宿主看的贡献点)。两者名字像,
      职责完全不同 —— 漏了前者的症状是「HTML 引了一个不存在的 .js」。
    */
    pkg.views = { 'view/editor.tsx': 'dist/view/editor.js' }
    pkg.contributes.customEditors = [
      {
        viewType,
        displayName: '%editor.displayName%',
        selector: [{ filenamePattern: `*.${name}` }],
        priority: 'default'
      }
    ]
    pkg.contributes.views = [
      { id: viewType, title: '%editor.displayName%', icon: 'file-pen', path: 'dist/view/editor.html' }
    ]
    pkg.activationEvents = [...new Set([...(pkg.activationEvents ?? []), `onCustomEditor:${viewType}`])]
    await writeFile(pkgPath, `${JSON.stringify(pkg, null, 2)}\n`)

    // l10n 两边都要补 —— 少一边,宿主的清单校验会直接拒装(key 必须两份都有)
    for (const [file, text] of [['zh-CN', '编辑器'], ['en-US', 'Editor']]) {
      const path = join(dir, 'l10n', `${file}.json`)
      const dict = JSON.parse(await readFile(path, 'utf8'))
      dict['editor.displayName'] = text
      await writeFile(path, `${JSON.stringify(dict, null, 2)}\n`)
    }
  }

  console.log(`
✓ ${publisher}.${name} 已生成${wantsView ? '(含 React 视图)' : ''}

  cd ${publisher}.${name}
  npm install
  npm run build
  npm run package

然后在 NextCoWork 的「扩展 › 插件 › 安装插件」里选那个目录(或 ZIP)。
改代码时用 npm run dev,它会监听 src/。${wantsView ? `

视图在 view/editor.tsx,用的是宿主下发的 React 与控件(\`nextcowork/ui\`)——
**不要**把 react 装进 dependencies,它们在打包时是 external。
新建一个 .${name} 文件就能打开它。` : ''}
`)
}

async function ask(rl, question, fallback) {
  if (rl === null) return fallback
  const answer = (await rl.question(`${question} [${fallback}]: `)).trim()
  return answer === '' ? fallback : answer
}

/** `--name hello` 里的 `hello` 不是目标目录。 */
function isFlagValue(args, value) {
  const index = args.indexOf(value)
  return index > 0 && args[index - 1].startsWith('--')
}

async function walk(dir) {
  const out = []
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) out.push(...(await walk(path)))
    else if (/\.(json|ts|md)$/.test(entry.name)) out.push(path)
  }
  return out
}

main().catch((error) => {
  console.error(`✗ ${error.message}`)
  process.exit(1)
})

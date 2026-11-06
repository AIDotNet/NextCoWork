/**
 * `Bash` —— 在工作区里跑一条 shell 命令。
 *
 * 名字、参数(`command` / `timeout` / `description` / `run_in_background`)和描述结构
 * 都对齐 Claude Code。但有**两处必须照实说**的差异,写进描述里,一个都不能省 ——
 * 每一处说错了,模型都会按 CC 的假设行事,然后拿到一个它无法归因的结果:
 *
 * 1. ★ **不是持久会话。** CC 的原话是 "a persistent shell session";我们脚下是
 *    `SpawnFn`,每次调用现开一个 shell(见 `host.ts` 里为什么不复用交互式 PTY)。
 *    不说的话,模型会先 `cd packages/app` 再在下一次调用里写相对路径 ——
 *    文件安安静静地落在仓库根上,而两次调用都返回成功。
 * 2. ★ **stdin 是关着的。** `node-spawn.ts` 用 `stdio: ['ignore', ...]`。
 *    任何等输入的命令(`git commit` 不带 `-m`、`npm login`)会挂到超时。
 *    描述里直接要求一律用非交互式参数。
 *
 * ★ **`run_in_background` 原先是刻意不声明的**,理由是「声明一个不生效的开关,
 * 比缺一个功能坏得多:模型会以为自己起了个后台服务然后继续往下做」。那条理由
 * 到今天仍然成立 —— 变的是它背后真的有东西了:后台 shell 注册表
 * (`main/agent-shells.ts`)+ `BashOutput` / `KillShell` 两个配套工具。
 * 原来的约束因此换了个落点,而不是被删掉:**没有 `ctx.shells` 的环境里
 * (纯内核测试、无头调用),这个开关会当场返回一句说清楚的失败**,而不是
 * 静默降级成前台执行 —— 降级同样会让模型以为自己起了后台服务。
 *
 * 另外**刻意没有**照抄 CC 描述里那一大段 git commit / PR 工作流。那是 CC 的产品行为,
 * 不是这个工具的用法;搬过来会让描述长一倍,而其中每一句都在教模型做我们没验证过的事。
 */
import { z } from 'zod'
import { toolFail, toolOk, type ToolResult } from '../../../../shared/agent/tool'
import { clampWithEllipsis, stripAnsi } from '../../text'
import { isAbortError } from '../../abort'
import type { DetachedProcess } from '../../host'
import { defineTool } from '../define'
import type { ToolContext, ToolRegistration } from '../registry'
import { NO_WORKSPACE } from './paths'

/** 和 CC 一致:默认 2 分钟。 */
const DEFAULT_TIMEOUT_MS = 120_000
/** 和 CC 一致:最长 10 分钟。再长的活应该拆开,或者用 `run_in_background`。 */
const MAX_TIMEOUT_MS = 600_000
/**
 * 和 CC 一致:输出超过这个长度就截断。
 * ★ 截断发生在**进上下文之前**,而 `node-spawn.ts` 那 512KB 的上限是防内存的 ——
 * 两道是不同的目的,别以为有了一道就能去掉另一道。
 */
const MAX_OUTPUT_CHARS = 30_000

const BashInput = z.object({
  command: z.string().min(1).describe('The command to execute'),
  timeout: z
    .number()
    .int()
    .min(1)
    .max(MAX_TIMEOUT_MS)
    .optional()
    .describe(
      `Optional timeout in milliseconds, up to ${String(MAX_TIMEOUT_MS)}. Defaults to ${String(DEFAULT_TIMEOUT_MS)}`
    ),
  /*
    ★★ `description` 在描述里写成**必填**(这一点与 CC 不同,是有意的),
    但在校验上**缺席时补空串,不拒绝调用**。

    需求:转录里那一行显示的是「这一步在干什么」,而不是命令原文 —— 命令动辄
    几百字符(`cd … && ssh … "powershell -Command …"`),一行里只显示得下一截
    全是路径的前缀,用户看到的等于没看到。所以要让模型**每次都写**它。

    ★★ 但**不能因为它缺席就让整条命令失败**。省掉它是模型的疏忽,不是用户的;
    把 `.min(1)` 挂上去的结果是:正在跑的旧会话里,下一条命令直接变成一条
    「Invalid arguments」的工具错误,用户看到的是「我的命令怎么跑不了了」,
    而他什么都没做错。所以缺席时 `.default('')` 补一个空串,界面退回去画
    `commandGist(command)`(`shared/domain/tool-presenter.ts`)—— 少一句描述,
    不少一条命令。历史转录里那批没有描述的调用走的也是这条。

    ★ 上限 200 字是护栏,不是目标:描述长到要换行时,它就不再是
    「一眼扫过去的那句话」了。
  */
  description: z
    .string()
    .max(200)
    .default('')
    .describe(
      'REQUIRED — always write it. What this command accomplishes, in 5-10 words, written for a person: '
      + 'say the goal, not the flags. This is the only thing the user sees before expanding the call'
    ),
  run_in_background: z
    .boolean()
    .optional()
    .describe(
      'Set to true to start the command and return immediately. Use BashOutput to read its output later, '
      + 'and KillShell to stop it. timeout does not apply to a background command'
    )
})

/**
 * 一段输出的呈现:空的时候要说「空」,不能给一段静默的空白让模型以为没读到。
 *
 * ★ 剥 ANSI 在**截断之前**:很多 CLI 在非 TTY 下照样着色(vitest 用的 picocolors
 * 在 win32 上无条件开色),留着的话那 30K 预算会被转义序列吃掉一大截,
 * 而界面上 ESC 不可见,只看得到满屏 `[32m`。
 */
function section(title: string, body: string): string {
  const t = stripAnsi(body).trim()
  return t === '' ? '' : `<${title}>\n${clampWithEllipsis(t, MAX_OUTPUT_CHARS)}\n</${title}>`
}

/**
 * 用户点了这张卡片上的停止按钮之后,给模型的那句话。
 *
 * ★ 措辞要同时说清三件事:是**用户**停的、**不是**命令坏了、**不要**自动重试。
 * 只说「命令被终止」的话,模型的默认反应是换个写法再跑一遍 —— 而用户刚刚
 * 亲手掐掉的就是这条命令,它会以为自己在帮忙。
 */
const STOPPED_BY_USER =
  'The user stopped this command from the UI. It did not fail — do not run it again or work around it. '
  + 'Ask what to do next, or continue with something else.'

/**
 * 用户把一条前台命令转去了后台:收编进程,告诉模型它现在是哪个 shell id。
 *
 * ★ 措辞要说清三件事:是**用户**转的(不是命令自己结束了)、它**还在跑**、
 * 下一步怎么读它 / 停它。只回一段「到目前为止的输出」的话,模型会把它当成
 * 完整结果去下结论 —— 而那条构建可能还要跑五分钟。
 *
 * ★ 收编失败时**杀掉它**再报错:此刻进程已经脱离了前台,没人杀的话它会成为
 * 一个谁也看不见、也停不了的孤儿。
 */
function adoptDetached(
  input: z.infer<typeof BashInput>,
  ctx: ToolContext,
  stdout: string,
  stderr: string,
  process: DetachedProcess
): ToolResult {
  let id: string
  try {
    if (ctx.shells?.adopt === undefined) throw new Error('Background commands are unavailable in this environment.')
    id = ctx.shells.adopt({
      command: input.command,
      cwd: ctx.workspaceRoot,
      ...(input.description === '' ? {} : { description: input.description }),
      runId: ctx.runId,
      callId: ctx.callId,
      process
    }).id
  } catch (error) {
    process.kill()
    return toolFail(
      'The user tried to move this command to the background, but it could not be adopted, so it was stopped: '
      + (error instanceof Error ? error.message : String(error))
    )
  }
  const parts = [section('stdout', stdout), section('stderr', stderr)].filter((s) => s !== '')
  return toolOk(
    `The user moved this command to the background while it was still running. It keeps running as shell id ${id}; `
    + 'its timeout no longer applies.\n'
    + (parts.length === 0 ? '(no output so far)\n' : `Output so far:\n${parts.join('\n')}\n`)
    + `Read its new output with BashOutput({ bash_id: "${id}" }) — each read returns only what arrived since the `
    + 'previous one. Stop it with KillShell when you no longer need it.'
  )
}

export const bashTool: ToolRegistration = defineTool({
  internalId: 'Bash',
  description:
    'Executes a shell command in the workspace directory, with a timeout and an output cap.\n\n' +
    'Before you run anything:\n' +
    '1. VERIFY THE DIRECTORY. If the command creates files in a new directory, first use LS to confirm ' +
    'the parent exists and is where you think it is.\n' +
    '2. VERIFY THE COMMAND. It runs directly on the user\'s machine. If it deletes data, changes global ' +
    'configuration, or sends anything outward, tell the user what it does before running it.\n\n' +
    'Usage notes:\n' +
    `- command is required. timeout is optional, in milliseconds, up to ${String(MAX_TIMEOUT_MS)} (10 minutes); ` +
    `it defaults to ${String(DEFAULT_TIMEOUT_MS)} (2 minutes)\n` +
    '- Write a 5-10 word description; that is REQUIRED and is what the user sees in the UI\n' +
    '- Use the Shell identified in the Environment section. The tool name Bash does not imply Bash or POSIX syntax.\n' +
    `- Output longer than ${String(MAX_OUTPUT_CHARS)} characters is truncated\n` +
    '- IMPORTANT: EVERY CALL GETS A FRESH SHELL AND KEEPS NO STATE. A cd, an export, or a variable you set ' +
    'in one call does not exist in the next. The working directory is always the workspace root, so use ' +
    'absolute or workspace-relative paths instead of relying on cd. To run somewhere else, change directory ' +
    'and execute the command inside a SINGLE call using the current shell syntax\n' +
    '- IMPORTANT: STDIN IS CLOSED. Any command that waits for input will hang until it times out: pass -m to ' +
    'git commit, pass --yes / --no-input to package managers, and never run something that needs an interactive login\n' +
    '- Set run_in_background for a command that does not finish on its own — a dev server, a watcher, a long ' +
    'build you want to keep working alongside. It returns a shell id immediately; read its output with ' +
    'BashOutput and stop it with KillShell. NEVER background a command you need the result of right now, ' +
    'and NEVER append & to fake it: a backgrounded shell is the only one whose output can still be read\n' +
    '- VERY IMPORTANT: NEVER use shell `find` or `grep` to search — use Grep and Glob. NEVER use `cat`, `head`, ' +
    '`tail`, or `ls` to read files and list directories — use Read and LS. Those tools apply the ignore list, ' +
    'skip binaries, add line numbers, and enforce a timeout; the shell equivalents do none of that\n' +
    '- Chain commands using syntax supported by the current shell. Windows PowerShell 5 does not support `&&`; ' +
    'use an explicit success check when later commands depend on earlier ones succeeding\n' +
    '- Quote paths that contain spaces: `cd "path with spaces"`\n' +
    '- A non-zero exit code comes back to you as an error, with stdout and stderr included\n' +
    '- The user can stop a single running command from the UI. That comes back as a tool error saying so; ' +
    'it is not a failure of the command and must not be retried\n' +
    '- The user can also move a running command to the background from the UI. You then get its output so far ' +
    'and a shell id; it is still running, so read the rest with BashOutput instead of running it again',
  schema: BashInput,
  readOnly: false,
  // ★ 破坏性:一条 shell 命令能做的事没有上界。`auto` 档下会走到「需要询问」。
  destructive: true,
  needsNetwork: false,
  async run(input, ctx) {
    if (ctx.workspaceRoot === '') return toolFail(NO_WORKSPACE)

    /*
      这条前台命令能不能被用户转去后台:要有注册表、且注册表能收编(本地环境)。
      ★ 能的话在第一条进度里就告诉界面,卡片据此画那颗按钮 —— 见 `ToolProgress.detachable`。
    */
    const shells = ctx.shells
    const detachable = input.run_in_background !== true && shells?.adopt !== undefined

    /*
      ★ 模型省了 description 时补的是空串(见 schema 上那段),所以状态行这里
      仍要有兜底 —— 空串发出去的话,状态行会显示成一条什么都没写的「执行中」。
    */
    ctx.emit({
      callId: ctx.callId,
      message: input.description === '' ? clampWithEllipsis(input.command, 80) : input.description,
      ...(detachable ? { detachable: true } : {})
    })

    if (input.run_in_background === true) {
      /*
        ★ 没有注册表时**明说**,不降级成前台跑。降级的话模型会拿不到 shell id
        却以为服务已经起在后台,接着去调 `BashOutput` —— 而那时它面对的是
        两条互相矛盾的信息,没有任何办法归因。
      */
      if (ctx.shells === undefined) {
        return toolFail(
          'Background commands are unavailable in this environment. Run the command in the foreground instead, '
          + 'with a timeout that fits it.'
        )
      }
      try {
        const shell = await ctx.shells.start({
          command: input.command,
          cwd: ctx.workspaceRoot,
          // 空串不往下传:后台 shell 列表拿它当标题,一个空标题比没有标题更难认
          ...(input.description === '' ? {} : { description: input.description }),
          runId: ctx.runId,
          callId: ctx.callId
        })
        return toolOk(
          `Started in the background as shell id ${shell.id}.\n`
          + `Read its new output with BashOutput({ bash_id: "${shell.id}" }) — each read returns only what `
          + 'arrived since the previous one. Stop it with KillShell when you are done; it keeps running '
          + 'across turns until then.'
        )
      } catch (error) {
        return toolFail(error instanceof Error ? error.message : String(error))
      }
    }

    const timeoutMs = input.timeout ?? DEFAULT_TIMEOUT_MS

    /*
      ★ 「停这一条」与「停整轮」是**两个 signal**,不能合成一个。

      `ctx.signal` 是整个 run 的:它一响,`defineTool` 的契约要求把中断原样抛上去。
      而卡片上那颗停止按钮只想掐掉这一条命令,run 要继续 —— 所以它走
      `stopper`,并在下面被翻译成一次**工具失败**。共用一个 signal 的话,
      用户点一条命令的停止,整段回复会跟着停在半截。
    */
    const stopper = new AbortController()
    const onRunAbort = (): void => {
      stopper.abort(ctx.signal.reason)
    }
    // 已经中断的 signal 不会再发事件 —— 这一行不是多余的空判,少了它
    // 「run 已中断」这件事传不到 spawn,于是它会先把命令跑起来再被杀。
    if (ctx.signal.aborted) onRunAbort()
    else ctx.signal.addEventListener('abort', onRunAbort, { once: true })
    /*
      「转去后台」是第三个信号,和上面两个都不同:它不杀任何东西,只是不再等。
      `SpawnFn` 收到它后把还活着的进程原样交回(`SpawnResult.detached`)。
    */
    const detacher = detachable ? new AbortController() : undefined
    const release = shells?.hold(
      { runId: ctx.runId, callId: ctx.callId, command: input.command },
      () => stopper.abort(),
      detacher === undefined ? undefined : () => detacher.abort()
    )

    try {
      /*
        ★ 这里的 catch **只认「用户停了这一条」那一种**,其余原样往上抛。
        `SpawnFn` 在被中断时抛的是 abortError,而 `defineTool` 的契约要求中断
        原样往上抛(见 define.ts 文件头)。把所有 abortError 都转成 toolFail 的话,
        「用户点了整轮停止」会表现成「命令失败了」,于是模型换个写法再试一次 ——
        停止按钮就失效了。
      */
      const r = await ctx.host.spawn(input.command, {
        cwd: ctx.workspaceRoot,
        signal: stopper.signal,
        timeoutMs,
        ...(detacher === undefined ? {} : { detach: detacher.signal })
      })

      if (r.detached !== undefined) return adoptDetached(input, ctx, r.stdout, r.stderr, r.detached)

      const parts = [section('stdout', r.stdout), section('stderr', r.stderr)].filter((s) => s !== '')

      if (r.code === 0) {
        return toolOk(parts.length === 0 ? '(command succeeded with no output)' : parts.join('\n'))
      }

      const head =
        r.code === 124
          ? `Command timed out after ${String(timeoutMs)}ms; the whole process group was killed.`
          : `Command exited with code ${String(r.code)}.`
      return toolFail(
        parts.length === 0 ? `${head} (no output)` : `${head}\n${parts.join('\n')}`
      )
    } catch (error) {
      if (isAbortError(error) && !ctx.signal.aborted) return toolFail(STOPPED_BY_USER)
      throw error
    } finally {
      // ★ 两个都必须还:留着的注销函数会让注册表攥住一个早就跑完的 callId,
      //   而监听器会跟着 run 的 signal 活到 run 结束(一轮几十条命令就是几十个)。
      release?.()
      ctx.signal.removeEventListener('abort', onRunAbort)
    }
  }
})

export const BASH_LIMITS = { DEFAULT_TIMEOUT_MS, MAX_TIMEOUT_MS, MAX_OUTPUT_CHARS } as const

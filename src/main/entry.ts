import { app } from 'electron'

/*
  ssh 的密码/口令询问会把这个 exe 再拉起一次(`NCW_SSH_ASKPASS=1` 由
  `environment/ssh/askpass.ts` 放进 ssh 的环境)。

  Unix 上走的就是这里:helper 是一层 `sh` 包装,包装里再起本应用。

  ★ Windows 上**正常不该走到这里** —— 那边 helper 是「同一个 exe 的 node 形态」
  (`ELECTRON_RUN_AS_NODE=1` + 生成的 askpass.js),因为 Chromium 启动时往 stdout
  写的那个换行会被 OpenSSH 当成密码。只有 `runAsNode` fuse 被关掉时才会退回这个
  分支,它是兜底,不是那条路径的正常形态。
*/
if (process.env.NCW_SSH_ASKPASS === '1') {
  app.disableHardwareAcceleration()
  void import('./environment/ssh/askpass').then(async ({ requestAskpass }) => {
    const answer = await requestAskpass(process.env, process.argv.at(-1) ?? '')
    process.stdout.write(`${answer}\n`, () => app.exit(0))
  }).catch(() => app.exit(1))
} else {
  void import('./index')
}
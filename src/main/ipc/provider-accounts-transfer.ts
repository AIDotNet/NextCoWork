/**
 * 账号元数据在**导出 / 导入**里的那一段(schema 第 24 条)。
 *
 * ## 为什么单独一个文件,而不是塞进 `db/repo.ts` 的 `mergeDataExport`
 *
 * `db/provider-accounts.ts` 依赖 `db/repo.ts`(它要 `removeCredential` 和
 * 云同步脏标记)。反过来让 repo 再依赖它就是一个**循环 import** ——
 * 今天的打包器能跑,但它会在「模块求值顺序」上留一颗定时炸弹,而那种失败
 * 表现为启动时某个导出忽然是 `undefined`,离这里十万八千里。
 *
 * 这一层已经在做同一件事的另一半(凭证的搬运与回滚,见 `storage.ts` 的
 * `credentialImportPlan`),所以账号行的搬运放在它旁边,方向也顺:
 * ipc 依赖 db,db 不依赖 ipc。
 *
 * ## 它拥有哪条不变式
 *
 * **导出里不带限流闸门和额度快照。** 那是「导出那台机器在那一刻观察到的上游
 * 状态」,几小时后就不成立了。带过去的表现是:刚导入的机器上,一个好端端的
 * 账号显示「限流中 · 还有 3 小时」,而用户无从知道那是一份从别处搬来的旧判断。
 */
import type { DataExport, ProviderAccountExport } from '../../shared/domain/data'
import { dataMergeDecision } from '../../shared/domain/data'
import { OAUTH_ISSUER_IDS, type OAuthIssuerId } from '../../shared/domain/oauth-issuer'
import { store } from '../state/store'

/** 导出时把账号行压成元数据。★ 这里列出来的字段就是全部 —— 没列的一律不出门 */
export function exportProviderAccounts(): ProviderAccountExport[] {
  return store.listProviderAccounts().map((row) => ({
    id: row.id,
    providerId: row.providerId,
    issuer: row.issuer,
    ...(row.label === undefined ? {} : { label: row.label }),
    order: row.order,
    enabled: row.enabled,
    current: row.current,
    updatedAt: row.updatedAt
  }))
}

function isIssuer(value: string): value is OAuthIssuerId {
  return OAUTH_ISSUER_IDS.includes(value as OAuthIssuerId)
}

/**
 * 把导入文件里的账号行合并进来。**在 `repo.mergeDataExport` 之后调**,
 * 因为账号要挂在已经写好的 provider 上。
 *
 * ★ 计数**不**并进导入弹窗那几个数字:账号是所属 provider 的附属信息,
 * 单独记一笔会让「导入了 N 项配置」和用户理解的对不上。
 *
 * ★ 认不出 issuer 的行直接丢:界面上登录按钮的名字查的是一张穷尽表
 * (`oauthIssuerLabel`),写进去会得到一行画不出来的账号。
 *
 * @returns 实际写入的行数(只给日志用)
 */
export function mergeProviderAccounts(data: DataExport): number {
  const incoming = data.providerAccounts ?? []
  if (incoming.length === 0) return 0
  const providers = new Set(store.listProviders().map((p) => p.id))
  let written = 0

  for (const account of incoming) {
    if (account.providerId === 'nextcowork') continue
    // provider 本身没被导进来(被跳过、或者文件里就没有)—— 挂不上去的账号是孤儿
    if (!providers.has(account.providerId)) continue
    if (!isIssuer(account.issuer)) continue

    const local = store.getProviderAccount(account.id)
    if (dataMergeDecision(local, account) === 'skip') continue
    store.putProviderAccount({
      id: account.id,
      providerId: account.providerId,
      issuer: account.issuer,
      ...(account.label === undefined ? {} : { label: account.label }),
      order: account.order,
      enabled: account.enabled,
      current: account.current,
      /*
        ★ 不把导入的账号一律标成「要重新登录」:密文很可能跟着一起来了
        (凭证通道同时在搬),那时它是好的。以本地已有的那一位为准,
        真实状态由设置页打开时的 announce 校正(它会解一次密文)。
      */
      needsReauth: local?.needsReauth ?? false,
      /*
        ★★ **限流与额度不从导入文件来**(那两样根本没导出)。已有行的则保留:
        本地观察到的限流是这台机器刚刚撞出来的事实,一次导入不该把它抹掉。
      */
      ...(local?.limit === undefined ? {} : { limit: local.limit }),
      ...(local?.quota === undefined ? {} : { quota: local.quota }),
      createdAt: local?.createdAt ?? account.updatedAt,
      updatedAt: account.updatedAt
    })
    written += 1
  }
  return written
}

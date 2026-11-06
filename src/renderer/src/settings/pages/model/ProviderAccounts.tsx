/**
 * 一家 OAuth 供应商的**账号列表**(设置 › 模型 › 供应商 › 账号)。
 *
 * ## 为了什么需求建的
 *
 * Codex / Kimi / GLM 这些按订阅计费的家,额度是按账号算的。用户要能挂多个号、
 * 排好顺序,限流时自动换到下一个,恢复后自动切回 —— 这一屏是那套机制的全部界面。
 *
 * ## 它拥有哪条不变式
 *
 * **数据只有一个来源:主进程。** 写操作的返回值和 `provider:accountsChanged`
 * 广播是同一份整表,组件直接整份替换、从不自己合并。本地合并的话,两个窗口
 * 同时开着设置页时会算出两个不同的顺序,而谁也不会去 diff 它们。
 *
 * ## 故意不做什么
 *
 * - **不自己判限流有没有解除**。倒计时只是显示;到点之后由主进程的下一次广播
 *   给出真实状态(两个进程不是同一个时钟源,见 `CredentialAuthInfo.expired`)。
 * - **没有限流账号时不跑定时器**(`needsCountdownTick`):设置页常年开着,
 *   一个永不停的 1 秒 tick 会让这一片每秒重渲一次。
 */
import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Plus } from "lucide-react";
import type { ProviderAccount } from "../../../../../shared/domain/provider-account";
import { Button } from "../../../components/arc/button/button";
import { Spinner } from "../../../components/ui/Spinner";
import { useI18n } from "../../../i18n";
import {
  addProviderAccount,
  clearProviderAccountLimit,
  fetchProviderAccountQuota,
  listProviderAccounts,
  removeProviderAccount,
  reorderProviderAccounts,
  setCurrentProviderAccount,
  setProviderAccountEnabled,
  setProviderAccountLabel,
  reauthProviderAccount,
} from "../../../services/provider";
import { AccountRow } from "./AccountRow";
import {
  activeAccountId,
  needsCountdownTick,
  orderedAccounts,
  reorder,
} from "./provider-accounts";

export function ProviderAccounts({
  providerId,
  rotation,
  busy,
  quotaFetchable,
  onError,
  onSigningInChange,
}: {
  providerId: string;
  /** 设置里的「账号自动切换」。★ 决定列表顶部那句「下一次会用谁」怎么算 */
  rotation: boolean;
  /** 面板整体的忙态(保存供应商配置等)—— 账号操作自己的忙态在下面单独有一个 */
  busy: boolean;
  /** 这家是不是订阅制 Coding Plan(决定每行画不画「刷新额度」)。判据收口在 `shared/domain/coding-plan.ts` */
  quotaFetchable: boolean;
  onError: (message: string) => void;
  /** 登录中要让外面那个面板知道:添加账号是一条会等好几分钟的 invoke */
  onSigningInChange: (signingIn: boolean) => void;
}): ReactNode {
  const { t } = useI18n();
  const [accounts, setAccounts] = useState<ProviderAccount[]>([]);
  const [pending, setPending] = useState(false);
  const [now, setNow] = useState(() => Date.now());

  /*
    ★ 只认 `providerId`,不认整个 provider 对象:后者每次广播回来都是新引用,
    挂上去会让这一屏在用户改任何一个无关字段时重新拉一遍账号列表。
    (和 `ProviderPanel` 里那条 `[p.id]` 的注释是同一个理由。)
  */
  useEffect(() => {
    let alive = true;
    void listProviderAccounts(providerId)
      .then((next) => {
        if (alive) setAccounts(next);
      })
      .catch(() => {
        /* 拉不到就先空着 —— 这一屏是附属信息,不该把整个供应商面板弄成错误态 */
      });

    /*
      ★★ **返回值必须进 cleanup**(§1 规则 4)。漏掉的话 HMR 每次热更叠一层监听器,
      表现是一次限流广播触发 N 次重渲,且只在 dev 出现。
    */
    const off = window.nextcowork.on("provider:accountsChanged", (e) => {
      if (e.providerId !== providerId) return;
      setAccounts(e.accounts);
    });
    return () => {
      alive = false;
      off();
    };
  }, [providerId]);

  /*
    倒计时的唯一 tick。★ 只在真有账号被限流时才起 —— 见文件头那条。
    `now` 一路喂给每一行,于是几行的秒数永远是同步跳的。
  */
  useEffect(() => {
    if (!needsCountdownTick(accounts, Date.now())) return
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [accounts]);

  /** 每个写操作都回整份列表 —— 直接替换,不合并(见文件头) */
  const run = useCallback(
    (action: () => Promise<ProviderAccount[]>): void => {
      setPending(true);
      void action()
        .then(setAccounts)
        .catch((error: unknown) => {
          onError(error instanceof Error ? error.message : String(error));
        })
        .finally(() => setPending(false));
    },
    [onError],
  );

  /*
    ★ 用 state 而不是 ref:这个值要驱动按钮上的 spinner 和文案。
    ref 改了不重渲 —— 表现是用户点完「添加账号」之后按钮纹丝不动,
    而浏览器已经在后台弹出来了,他会以为没点上,再点一次
    (而再点一次会**掐掉**上一条登录,见 `provider-auth.ts` 的单飞)。
  */
  const [signingIn, setSigningIn] = useState(false);
  const addAccount = (): void => {
    setSigningIn(true);
    onSigningInChange(true);
    run(() =>
      addProviderAccount(providerId).finally(() => {
        setSigningIn(false);
        onSigningInChange(false);
      }),
    );
  };

  const ordered = orderedAccounts(accounts);
  const active = activeAccountId(accounts, now, rotation);
  const disabled = busy || pending;

  const move = (id: string, delta: -1 | 1): void => {
    const ids = ordered.map((a) => a.id);
    const from = ids.indexOf(id);
    if (from < 0) return;
    run(() => reorderProviderAccounts(providerId, reorder(ids, from, from + delta)));
  };

  return (
    <div className="space-y-1.5">
      {ordered.length === 0 ? (
        <p className="text-[11.5px] leading-[1.6] text-fg-faint">{t("providerAccount.empty")}</p>
      ) : (
        <ul className="space-y-1.5">
          {ordered.map((account) => (
            <AccountRow
              key={account.id}
              account={account}
              now={now}
              isActive={account.id === active}
              busy={disabled}
              quotaFetchable={quotaFetchable}
              onSetCurrent={() => run(() => setCurrentProviderAccount(providerId, account.id))}
              onToggleEnabled={(enabled) =>
                run(() => setProviderAccountEnabled(providerId, account.id, enabled))
              }
              onRemove={() => run(() => removeProviderAccount(providerId, account.id))}
              onReauth={() => run(() => reauthProviderAccount(providerId, account.id))}
              onClearLimit={() => run(() => clearProviderAccountLimit(providerId, account.id))}
              onRename={(label) => run(() => setProviderAccountLabel(providerId, account.id, label))}
              onMove={(delta) => move(account.id, delta)}
              /* ★ 回的是整份列表(契约约定),直接 setAccounts,不自己合并 */
              onRefreshQuota={() =>
                run(() => fetchProviderAccountQuota(providerId, account.id))
              }
            />
          ))}
        </ul>
      )}

      <div className="flex items-center gap-2">
        <Button size="sm"
          type="button"
          variant="secondary"
          disabled={disabled}
          onClick={addAccount}
        >
          {signingIn ? <Spinner size="xs" /> : <Plus size={13} />}
          {signingIn ? t("providerAccount.adding") : t("providerAccount.add")}
        </Button>
        {ordered.length > 1 && (
          <span className="text-[11px] text-fg-faint">{t("providerAccount.dragHint")}</span>
        )}
      </div>
    </div>
  );
}

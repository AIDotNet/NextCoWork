/**
 * 账号列表里的一行:名字 + 状态徽章 + 四个操作 + (Codex 的)两条额度条。
 *
 * ## 为了什么需求建的
 *
 * 一家 OAuth 供应商下面挂着多个登录身份,用户要看得到「哪个在用、哪个被限流了、
 * 还有多久恢复、额度还剩多少」,并且能手动排序 / 停用 / 解除限流 / 设为当前。
 *
 * ## 它拥有哪条不变式
 *
 * **一切判断都来自纯函数**(`provider-accounts.ts` 与 `shared/domain/provider-account.ts`),
 * 这里只负责把它们画出来。在这个文件里写 `account.limit.until > Date.now()` 之类的
 * 判断,就会出现「主进程说可用、界面说限流中」——而两边都没报错。
 *
 * ## 故意不做什么
 *
 * - **不自己起定时器**。倒计时的 `now` 由父组件那一个 tick 喂进来(见 `ProviderAccounts`):
 *   每行一个 `setInterval`,三个账号就是三个,而且它们不同步、互相错开半秒。
 * - **不画后端接不住的控件**(§5):没有限流就没有「立即解除」按钮。
 */
import { useState, type ReactNode } from "react";
import { Check, LogIn, RefreshCw, Star, Trash2, GripVertical } from "lucide-react";
import type { ProviderAccount } from "../../../../../shared/domain/provider-account";
import { accountDisplay } from "../../../../../shared/domain/provider-account";
import { Button } from "../../../components/arc/button/button";
import { ProgressBar } from "../../../components/ui/ProgressBar";
import { TextInput } from "../../../components/ui/TextInput";
import { Switch } from "../../../components/arc/switch/switch";
import { useI18n } from "../../../i18n";
import { cn } from "../../../lib/cn";
import {
  accountBadge,
  countdownTo,
  isQuotaStale,
  quotaBar,
  type AccountBadge,
  type QuotaBar,
} from "./provider-accounts";

/** 徽章的颜色档。★ 四态各一个,不共用 —— 共用会让「已停用」和「限流中」看起来是同一件事 */
const BADGE_CLASS: Readonly<Record<AccountBadge, string>> = {
  ready: "text-accent",
  limited: "text-warning",
  "needs-reauth": "text-danger",
  disabled: "text-fg-faint",
};

const BADGE_KEY: Readonly<Record<AccountBadge, string>> = {
  ready: "providerAccount.badge.ready",
  limited: "providerAccount.badge.limited",
  "needs-reauth": "providerAccount.badge.needsReauth",
  disabled: "providerAccount.badge.disabled",
};

export function AccountRow({
  account,
  now,
  isActive,
  busy,
  quotaFetchable,
  onSetCurrent,
  onToggleEnabled,
  onRemove,
  onReauth,
  onClearLimit,
  onRename,
  onMove,
  onRefreshQuota,
}: {
  account: ProviderAccount;
  /** 由父组件的单一 tick 喂进来 —— 每行自己起定时器会让它们互相错开半秒 */
  now: number;
  /** 「下一次请求会用它」。★ 和 `account.current` 是两件事,见 selectAccount 的注释 */
  isActive: boolean;
  busy: boolean;
  /**
   * 这家的订阅额度能不能主动拉(GLM Coding Plan 那两家)。
   *
   * ★ 需求:额度数据有两类来源 —— Codex 从响应头搭便车(GLM 没有这个来源),
   * 订阅制那两家只能显式发一次查询。没有这个标志的话,要么给所有家画一个
   * 点了必 400 的按钮,要么让订阅账号永远没有入口。
   */
  quotaFetchable: boolean;
  onSetCurrent: () => void;
  onToggleEnabled: (enabled: boolean) => void;
  onRemove: () => void;
  onReauth: () => void;
  onClearLimit: () => void;
  onRename: (label: string) => void;
  /** 键盘路径:鼠标能拖的,键盘也要能移(§8「要么都通,要么都不画」) */
  onMove: (delta: -1 | 1) => void;
  onRefreshQuota: () => void;
}): ReactNode {
  const { t, locale } = useI18n();
  const [renaming, setRenaming] = useState(false);
  const [draft, setDraft] = useState("");

  const badge = accountBadge(account, now);
  const display = accountDisplay(account);
  const name = display.kind === "unknown" ? t("providerAccount.unnamed") : display.text;
  const timeFormat = new Intl.DateTimeFormat(locale, { hour: "2-digit", minute: "2-digit" });

  const countdown = account.limit === undefined ? null : countdownTo(account.limit.until, now);
  const countdownText =
    countdown === null
      ? null
      : countdown.hours > 0
        ? t("providerAccount.countdownHours", { hours: countdown.hours, minutes: countdown.minutes })
        : countdown.minutes > 0
          ? t("providerAccount.countdownMinutes", {
              minutes: countdown.minutes,
              seconds: countdown.seconds,
            })
          : t("providerAccount.countdownSeconds", { seconds: countdown.seconds });

  const commitRename = (): void => {
    setRenaming(false);
    onRename(draft);
  };

  return (
    <li
      className={cn(
        "rounded-[12px] border border-border bg-surface-field px-2.5 py-2",
        // ★ 停用的那行整体压暗,但**不隐藏** —— 用户要找得到它才能再打开
        !account.enabled && "opacity-60",
      )}
    >
      <div className="flex items-center gap-2">
        {/* 拖拽把手。键盘路径走下面那两颗上下移按钮,两条路都通(§8) */}
        <span className="shrink-0 cursor-grab text-fg-faint" aria-hidden>
          <GripVertical size={14} />
        </span>

        <div className="min-w-0 flex-1">
          {renaming ? (
            <TextInput
              value={draft}
              onChange={setDraft}
              onCommit={commitRename}
              ariaLabel={t("providerAccount.rename")}
              placeholder={t("providerAccount.renamePlaceholder")}
              disabled={busy}
            />
          ) : (
            <div className="flex min-w-0 items-center gap-1.5">
              <span className="min-w-0 truncate text-[13px] text-fg">{name}</span>
              {account.current && (
                <span
                  title={t("providerAccount.currentHint")}
                  className="shrink-0 rounded-pill bg-accent/10 px-1.5 py-px text-[10.5px] text-accent"
                >
                  {t("providerAccount.current")}
                </span>
              )}
              {isActive && !account.current && (
                <span className="shrink-0 text-[10.5px] text-fg-faint">
                  {t("providerAccount.active")}
                </span>
              )}
            </div>
          )}
          <p className={cn("mt-0.5 flex items-center gap-1 text-[11.5px]", BADGE_CLASS[badge])}>
            <span>{t(BADGE_KEY[badge])}</span>
            {badge === "limited" && countdownText !== null && (
              <>
                <span aria-hidden>·</span>
                <span>{countdownText}</span>
                {account.limit !== undefined && (
                  <span className="text-fg-faint">
                    {t("providerAccount.limitedUntil", {
                      time: timeFormat.format(new Date(account.limit.until)),
                    })}
                  </span>
                )}
              </>
            )}
          </p>
        </div>

        <div className="flex shrink-0 items-center gap-1">
          {/* ★ 只在限流时出现:画一个永远灰着的按钮是一次会失败的承诺(§5) */}
          {badge === "limited" && (
            <Button size="sm" type="button" variant="secondary" disabled={busy} onClick={onClearLimit}>
              {t("providerAccount.clearLimit")}
            </Button>
          )}
          {/* ★ 只在订阅制那两家出现:额度是显式查询来的,不是搭便车(见 props 注释) */}
          {quotaFetchable && (
            <button
              type="button"
              title={t("providerAccount.quota.refresh")}
              aria-label={t("providerAccount.quota.refresh")}
              disabled={busy}
              onClick={onRefreshQuota}
              className="flex size-7 items-center justify-center rounded-full text-fg-muted transition-colors hover:bg-tint-strong hover:text-fg disabled:opacity-40 motion-reduce:transition-none"
            >
              <RefreshCw size={13} aria-hidden />
            </button>
          )}
          {badge === "needs-reauth" && (
            <Button size="sm" type="button" variant="primary" disabled={busy} onClick={onReauth}>
              <LogIn size={12} />
              {t("providerAccount.reauth")}
            </Button>
          )}
          {!account.current && (
            <button
              type="button"
              title={t("providerAccount.setCurrent")}
              aria-label={t("providerAccount.setCurrent")}
              disabled={busy}
              onClick={onSetCurrent}
              className="flex size-7 items-center justify-center rounded-full text-fg-muted transition-colors hover:bg-tint-strong hover:text-fg disabled:opacity-40 motion-reduce:transition-none"
            >
              <Star size={13} aria-hidden />
            </button>
          )}
          <Switch
            checked={account.enabled}
            disabled={busy}
            onCheckedChange={onToggleEnabled}
            aria-label={account.enabled ? t("providerAccount.disable") : t("providerAccount.enable")}
          />
          <button
            type="button"
            title={t("providerAccount.rename")}
            aria-label={t("providerAccount.rename")}
            disabled={busy}
            onClick={() => {
              setDraft(account.label ?? "");
              setRenaming((v) => !v);
            }}
            className="flex size-7 items-center justify-center rounded-full text-fg-muted transition-colors hover:bg-tint-strong hover:text-fg disabled:opacity-40 motion-reduce:transition-none"
          >
            <Check size={13} aria-hidden className={cn(!renaming && "hidden")} />
            <span className={cn("text-[11px]", renaming && "hidden")} aria-hidden>
              Aa
            </span>
          </button>
          <button
            type="button"
            title={t("providerAccount.remove")}
            aria-label={t("providerAccount.remove")}
            disabled={busy}
            onClick={onRemove}
            className="flex size-7 items-center justify-center rounded-full text-fg-muted transition-colors hover:bg-danger/10 hover:text-danger disabled:opacity-40 motion-reduce:transition-none"
          >
            <Trash2 size={13} aria-hidden />
          </button>
          {/* 键盘可达的排序路径 —— 拖拽之外的那一半(§8) */}
          <div className="flex flex-col">
            <button
              type="button"
              aria-label={t("providerAccount.moveUp")}
              disabled={busy}
              onClick={() => onMove(-1)}
              className="px-1 text-[9px] leading-[1.1] text-fg-faint hover:text-fg disabled:opacity-40"
            >
              ▲
            </button>
            <button
              type="button"
              aria-label={t("providerAccount.moveDown")}
              disabled={busy}
              onClick={() => onMove(1)}
              className="px-1 text-[9px] leading-[1.1] text-fg-faint hover:text-fg disabled:opacity-40"
            >
              ▼
            </button>
          </div>
        </div>
      </div>

      <AccountQuota account={account} now={now} quotaFetchable={quotaFetchable} />
    </li>
  );
}

/**
 * 账号行下的额度条(5 小时 / 每周)。
 *
 * ★ 两类来源在「没有数据」时说**不同的话**:Codex 的数据只在发消息时搭便车回来
 * (提示「发一条消息后更新」),订阅制那两家是显式查询来的(提示去点「刷新额度」)。
 * 合成一句话的表现是:GLM 用户照着「发一条消息」等一个永远不会来的更新。
 */
function AccountQuota({
  account,
  now,
  quotaFetchable,
}: {
  account: ProviderAccount;
  now: number;
  quotaFetchable: boolean;
}): ReactNode {
  const { t, locale } = useI18n();

  const quota = account.quota;
  if (quota === undefined) {
    if (account.issuer === "chatgpt") {
      return (
        <p className="mt-1.5 pl-6 text-[11px] text-fg-faint">{t("providerAccount.quota.empty")}</p>
      );
    }
    if (quotaFetchable) {
      return (
        <p className="mt-1.5 pl-6 text-[11px] text-fg-faint">
          {t("providerAccount.quota.fetchableEmpty")}
        </p>
      );
    }
    return null;
  }

  const bars = [quotaBar(quota.primary), quotaBar(quota.secondary)].filter(
    (bar): bar is QuotaBar => bar !== null,
  );
  if (bars.length === 0) {
    return (
      <p className="mt-1.5 pl-6 text-[11px] text-fg-faint">{t("providerAccount.quota.empty")}</p>
    );
  }

  const timeFormat = new Intl.DateTimeFormat(locale, {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
  const staleHours = Math.floor((now - quota.capturedAt) / 3600_000);

  return (
    <div className="mt-1.5 space-y-1 pl-6">
      {bars.map((bar) => (
        <div key={bar.windowMinutes} className="flex items-center gap-2">
          <span className="w-12 shrink-0 text-[10.5px] text-fg-faint">
            {bar.window === "other"
              ? t("providerAccount.quota.other", { minutes: bar.windowMinutes })
              : t(`providerAccount.quota.${bar.window}`)}
          </span>
          <ProgressBar
            value={bar.percent / 100}
            label={t("providerAccount.quota.used", { percent: bar.percent })}
            className={cn("flex-1", bar.critical && "[&>span]:bg-warning")}
          />
          <span className={cn("w-10 shrink-0 text-right text-[10.5px]", bar.critical ? "text-warning" : "text-fg-muted")}>
            {bar.percent}%
          </span>
          <span className="w-24 shrink-0 text-right text-[10.5px] text-fg-faint">
            {t("providerAccount.quota.resetsAt", { time: timeFormat.format(new Date(bar.resetsAt)) })}
          </span>
        </div>
      ))}
      {/* ★ 数据只在发消息时搭便车更新,久不用的账号要说清楚「这是旧数据」 */}
      {isQuotaStale(quota.capturedAt, now) && (
        <p className="text-[10.5px] text-fg-faint">
          {t("providerAccount.quota.stale", { hours: staleHours })}
        </p>
      )}
    </div>
  );
}

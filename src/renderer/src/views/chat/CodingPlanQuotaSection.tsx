/**
 * 圆环菜单里的「套餐额度」区块 —— 当前会话供应商是 GLM Coding Plan 订阅时,
 * 在占用归因下面显示这个账号还剩多少额度。
 *
 * ## 为了什么需求建的
 *
 * 订阅额度按 5 小时 / 每周窗口扣,窗口跑满时请求会失败,而失败前的唯一信号
 * 就是这两个数。它们同时也要在设置页(账号行)显示 —— **两处读的是同一份数据**
 * (账号行上的 `quota` 快照,IPC `provider:fetchQuota` 写入),这里只是第二个视口,
 * 不存在第二份真源。
 *
 * ## 它拥有哪条不变式
 *
 * **数据只在菜单打开时拉一次,刷新按钮是唯一的再取入口。** 这个组件挂载在
 * `Menu` 的内容里,菜单关掉即卸载 —— 自动轮询在这里没有任何意义,反而会把
 * 「看一眼额度」变成一个每次开菜单都发的网络面。
 *
 * ## 故意不做什么
 *
 * - **不在这里挑「实际发请求的账号」**。账号池的轮换发生在主进程热路径上,
 *   这里显示的是 `selectAccount` 选出的那一个(与主进程同一份纯函数),
 *   多账号轮换时界面标注的账号可能和真正服务请求的那个差一拍 ——
 *   这是快照的固有代价,界面上用账号名把它说出来。
 * - **不判限流、不算倒计时**。那些是账号行(`AccountRow`)的职责,这个区块
 *   只回答「还剩多少、什么时候重置」。
 */
import { useEffect, useState, type ReactNode } from "react";
import { RefreshCw } from "lucide-react";
import { selectAccount } from "../../../../shared/domain/provider-account";
import type { ProviderAccount } from "../../../../shared/domain/provider-account";
import { Spinner } from "../../components/ui/Spinner";
import { ProgressBar } from "../../components/ui/ProgressBar";
import { useI18n } from "../../i18n";
import { cn } from "../../lib/cn";
import { fetchProviderAccountQuota, listProviderAccounts } from "../../services/provider";
import { quotaBar, type QuotaBar as QuotaBarView } from "../../settings/pages/model/provider-accounts";

/** 菜单宽度只有 260,这个区块只显示 5 小时 / 每周两个已知窗口 */
function visibleBars(account: ProviderAccount): QuotaBarView[] {
  const bars = [quotaBar(account.quota?.primary), quotaBar(account.quota?.secondary)];
  return bars.filter((bar): bar is QuotaBarView => bar !== null && bar.window !== "other");
}

export function CodingPlanQuotaSection({ providerId }: { providerId: string }): ReactNode {
  const { t, locale } = useI18n();
  const [accounts, setAccounts] = useState<ProviderAccount[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  /*
    需求:菜单打开(组件挂载)时才拉一次账号列表。挂载即卸载的场景下
    cleanup 不清也漏不到哪去,但 useEffect 的规矩是返回值必须进 cleanup(§1.4)。
  */
  useEffect(() => {
    let alive = true;
    void listProviderAccounts(providerId)
      .then((next) => {
        if (alive) setAccounts(next);
      })
      .catch(() => {
        /* 拉不到就让区块显示失败一行 —— 这是一张诊断卡,不该把菜单变成错误弹窗 */
        if (alive) setFailed(true);
      });
    return () => {
      alive = false;
    };
  }, [providerId]);

  const refresh = (): void => {
    const account = accounts === null ? null : selectAccount(accounts, Date.now());
    if (account === null) return;
    setRefreshing(true);
    void fetchProviderAccountQuota(providerId, account.id)
      .then(setAccounts)
      .catch(() => setFailed(true))
      .finally(() => setRefreshing(false));
  };

  const account = accounts === null || accounts.length === 0 ? null : selectAccount(accounts, Date.now());
  const bars = account === null ? [] : visibleBars(account);
  const timeFormat = new Intl.DateTimeFormat(locale, {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });

  return (
    <>
      {/*
        ★ 不用 `MenuLabel` 当标题:它自带 `px-2.5 pt-2 pb-1`,套进 flex 之后
        右边的刷新按钮会对不齐 —— 这里照抄它的字号和左距,只把内边距挪到外层。
      */}
      <div className="flex items-center justify-between pl-2.5 pr-1.5 pt-2">
        <span className="pb-1 text-[11px] text-fg-faint">
          {t("providerAccount.quota.menuTitle")}
        </span>
        {/* 刷新常驻:额度是查询来的(见文件头),没有入口的数据就是死数据 */}
        <button
          type="button"
          aria-label={t("providerAccount.quota.refresh")}
          title={t("providerAccount.quota.refresh")}
          disabled={refreshing}
          onClick={refresh}
          className="mb-0.5 flex size-6 items-center justify-center rounded-full text-fg-faint transition-colors hover:bg-tint-hover hover:text-fg disabled:opacity-40 motion-reduce:transition-none"
        >
          {refreshing ? <Spinner size="xs" /> : <RefreshCw size={12} aria-hidden />}
        </button>
      </div>
      <div className="px-2.5 pb-1 text-[11.5px]">
        {failed ? (
          <p className="py-0.5 text-fg-faint">{t("providerAccount.quota.failed")}</p>
        ) : accounts === null ? (
          <p className="flex items-center gap-1.5 py-0.5 text-fg-faint">
            <Spinner size="xs" />
            {t("providerAccount.quota.refreshing")}
          </p>
        ) : account === null ? (
          <p className="py-0.5 text-fg-faint">{t("providerAccount.quota.noAccount")}</p>
        ) : bars.length === 0 ? (
          <p className="py-0.5 text-fg-faint">{t("providerAccount.quota.menuEmpty")}</p>
        ) : (
          bars.map((bar) => {
            const remaining = 100 - bar.percent;
            const remainingText = t("providerAccount.quota.remaining", { percent: remaining });
            return (
              <div key={bar.window} className="py-1.5">
                <div className="flex items-center gap-2">
                  <span className="min-w-0 shrink-0 text-fg-muted">
                    {t(`providerAccount.quota.${bar.window}`)}
                  </span>
                  <span
                    className={cn(
                      "ml-auto shrink-0 tabular-nums",
                      bar.critical ? "text-warning" : "text-fg",
                    )}
                  >
                    {remainingText}
                  </span>
                </div>
                {/*
                  ★ 条子画的是**剩余**量,和右边那个数是同一个语义
                  (AccountRow 那两根画的是已用,两边各说各的话是刻意的:设置页在管
                  「这个号还能不能用」,这里在管「这一轮还够不够」)。告警阈值沿用
                  `quotaBar.critical`(已用 ≥ 90% = 剩余 ≤ 10%),同一事实两种说法
                  不该有两个阈值。
                */}
                <ProgressBar
                  value={remaining / 100}
                  /* valuetext 组合窗口名和剩余量:读屏念「5 小时 剩 88%」,比孤零零一个百分比可懂 */
                  label={`${t(`providerAccount.quota.${bar.window}`)} ${remainingText}`}
                  className={cn("mt-1", bar.critical && "[&>span]:bg-warning")}
                />
                <div className="mt-0.5 text-right text-[10px] tabular-nums text-fg-faint">
                  {t("providerAccount.quota.resetsAt", {
                    time: timeFormat.format(new Date(bar.resetsAt)),
                  })}
                </div>
              </div>
            );
          })
        )}
      </div>
    </>
  );
}

/**
 * 搜索服务 —— 参考图那一页,八家。
 *
 * ## 这一页的顺序不是装饰
 *
 * 行的先后就是 `SearchProviderConfig.priority`,而 priority 的语义是
 * **`web_search` 按它升序依次调用,一家失败就切下一家**。所以拖动这些行
 * 是在改运行时行为,不是在整理界面 —— 上面那句说明必须写出来,
 * 不然用户会以为自己只是在排版。
 *
 * ## 三种「用不了」是三件不同的事,不能混成一个灰
 *
 * | 情况 | 界面 |
 * |---|---|
 * | `unavailable`(这家在世界上就没了,如 Bing) | 开关**禁用**,原样显示那句原因 |
 * | 没填 Key | 开关可点,但点开会被主进程接受 —— 显示「未配置」提示去填 |
 * | 填了 Key 但没启用 | 正常关着 |
 *
 * ## 拖拽复用 `shell/useDragReorder`,轴换成 'y'
 *
 * 不引 DnD 库,也不再写一份 —— 两份近乎一样的实现迟早分叉,其中一份会拿到
 * 另一份没有的 bug 修复。那个 hook 这次为此加了 axis 参数,默认 'x' 保持
 * 外层 Tab 条原样。
 *
 * ## 列表下面还有一小节
 *
 * `BuiltinSearchSection` 是免 Key 的内置兜底(一家都没配或全挂时自动接手)。
 * 它**不是列表里的一行**,理由写在那个文件的文件头 —— 简单说:它没有 Key、
 * 没有开关、也不参与这里的优先级排序。
 */
import {
  Check,
  ExternalLink,
  GripVertical,
  Search } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import type { SearchProviderStatus } from "../../../../../shared/domain/search";
import { searchMeta } from "../../../../../shared/domain/search";
import { Button } from "../../../components/arc/button/button";
import { TextInput } from "../../../components/ui/TextInput";
import { Switch } from "../../../components/arc/switch/switch";
import { useI18n } from "../../../i18n";
import { cn } from "../../../lib/cn";
import { openExternal } from "../../../services/app";
import { testSearchProvider } from "../../../services/websearch";
import { useDragReorder } from "../../../shell/useDragReorder";
import { useWebSearchStore } from "../../../stores/websearch";
import { SettingGroup } from "../../Row";
import type { SettingsPageProps } from "../../props";
import { BuiltinSearchSection } from "./BuiltinSearchSection";
import { Spinner } from '../../../components/ui/Spinner'

export function SearchPane({ settings, patch }: SettingsPageProps): ReactNode {
  const { t } = useI18n();
  const {
    providers,
    loaded,
    error,
    load,
    setEnabled,
    reorder,
    setCredential,
    clearCredential,
  } = useWebSearchStore();

  useEffect(() => {
    void load();
  }, [load]);

  const ordered = [...providers].sort(
    (a, b) => a.config.priority - b.config.priority,
  );

  const drag = useDragReorder((from, to) => {
    const ids = ordered.map((p) => p.config.id);
    const [moved] = ids.splice(from, 1);
    if (moved === undefined) return;
    ids.splice(to, 0, moved);
    void reorder(ids);
  }, "y");

  return (
    <>
      <SettingGroup>
        <div className="px-4 py-3">
          <p className="text-[13px] text-fg">{t("connection.search.title")}</p>
          <p className="mt-0.5 text-[12px] text-fg-faint">
            {t("connection.search.hint")}
          </p>
        </div>

        {error !== null ? (
          <div className="px-4 pb-4">
            <p className="rounded-[8px] bg-danger/10 px-2.5 py-2 text-[12px] text-danger">
              {t("connection.search.loadFailed", { error })}
            </p>
          </div>
        ) : !loaded ? (
          <div className="flex items-center justify-center gap-2 py-8 text-[12.5px] text-fg-faint">
            <Spinner size="sm" />
            {t("connection.search.reading")}
          </div>
        ) : (
          <ul className="relative border-t border-hairline">
            {ordered.map((p, i) => (
              <ProviderRow
                key={p.config.id}
                status={p}
                rank={i + 1}
                style={drag.styleFor(i)}
                onGrab={(e) => drag.onPointerDown(e, i)}
                onToggle={(on) => void setEnabled(p.config.id, on)}
                onSaveKey={(k) => setCredential(p.config.id, k)}
                onClearKey={() => void clearCredential(p.config.id)}
              />
            ))}
          </ul>
        )}
      </SettingGroup>
      <BuiltinSearchSection settings={settings} patch={patch} />
    </>
  );
}

function ProviderRow({
  status,
  rank,
  style,
  onGrab,
  onToggle,
  onSaveKey,
  onClearKey,
}: {
  status: SearchProviderStatus;
  rank: number;
  style: React.CSSProperties;
  onGrab: (e: React.PointerEvent<HTMLElement>) => void;
  onToggle: (enabled: boolean) => void;
  onSaveKey: (apiKey: string) => Promise<void>;
  onClearKey: () => void;
}): ReactNode {
  const { t } = useI18n();
  const id = status.config.id;
  const meta = searchMeta(id);
  const blocked = meta?.unavailable;
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [tested, setTested] = useState<string | null>(null);

  const save = (): void => {
    if (draft.trim() === "") return;
    setBusy(true);
    void onSaveKey(draft.trim())
      .then(() => {
        setDraft(""); // ★ 存完就从内存里抹掉,别留在 React 状态里
        setEditing(false);
      })
      .catch((e: unknown) =>
        setTested(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => setBusy(false));
  };

  const test = (): void => {
    setBusy(true);
    setTested(null);
    void testSearchProvider(id)
      .then((r) => {
        if (!r.ok) {
          setTested(r.error.message);
          return;
        }
        const { ok, latencyMs, message } = r.data;
        setTested(
          ok
            ? `${t("connection.search.connected")}${latencyMs === undefined ? "" : ` · ${String(latencyMs)}ms`}`
            : (message ?? t("connection.search.unavailable")),
        );
      })
      .finally(() => setBusy(false));
  };

  return (
    <li
      // ★ 抓手是行里那个小把手,而**几何要按整行算** —— 这个标记就是
      // `useDragReorder` 用来从抓手找回「哪个元素是一项」的凭据
      data-drag-item
      style={style}
      className={cn(
        "border-b border-hairline bg-canvas px-4 py-3 last:border-b-0",
        blocked !== undefined && "opacity-60",
      )}
    >
      <div className="flex items-start gap-2.5">
        {/* 用不了的那两家不给拖 —— 排在第几位对它们没有任何意义 */}
        <span
          className={cn(
            "app-no-drag mt-[3px] shrink-0 text-fg-faint",
            blocked === undefined ? "cursor-grab" : "cursor-default opacity-30",
          )}
          onPointerDown={blocked === undefined ? onGrab : undefined}
          aria-hidden
        >
          <GripVertical size={14} />
        </span>
        <span className="mt-[2px] w-4 shrink-0 text-[11.5px] tabular-nums text-fg-faint">
          {rank}
        </span>

        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="text-[13px] text-fg">{meta?.name ?? id}</span>
            {status.hasKey && (
              <span className="flex shrink-0 items-center gap-1 text-[11.5px] text-accent">
                <Check size={11} />
                {t("connection.search.configured")}
                {status.last4 !== undefined && (
                  <code className="text-fg-faint">····{status.last4}</code>
                )}
              </span>
            )}
          </div>
          <p className="mt-0.5 text-[12px] text-fg-muted">
            {meta?.description}
          </p>

          {/* ★ 原因原样显示,不概括成「暂不可用」—— 那句话里有用户接下来该做什么 */}
          {blocked !== undefined && (
            <p className="mt-1.5 text-[11.5px] leading-[1.6] text-fg-faint">
              {blocked}
            </p>
          )}

          {blocked === undefined && (
            <div className="mt-2">
              {editing || !status.hasKey ? (
                <div className="flex items-center gap-2">
                  <div className="min-w-0 flex-1">
                    <TextInput
                      value={draft}
                      onChange={setDraft}
                      onCommit={save}
                      size="sm"
                      ariaLabel={t("connection.search.apiKey", {
                        name: meta?.name ?? id,
                      })}
                      placeholder={t("connection.search.pasteKey")}
                      disabled={busy}
                    />
                  </div>
                  <Button size="sm"
                    type="button"
                    variant="primary"
                    disabled={busy || draft.trim() === ""}
                    onClick={save}
                  >
                    {t("common.save")}
                  </Button>
                  {meta !== undefined && (
                    <Button size="sm"
                      type="button"
                      variant="secondary"
                      onClick={() => void openExternal(meta.keyUrl)}
                    >
                      <ExternalLink size={12} />
                      {t("connection.search.get")}
                    </Button>
                  )}
                </div>
              ) : (
                <div className="flex items-center gap-1.5">
                  <Button size="sm" type="button" variant="secondary" onClick={() => setEditing(true)}>
                    {t("connection.search.replaceKey")}
                  </Button>
                  <Button size="sm" type="button" variant="secondary" onClick={onClearKey}>
                    {t("connection.search.clear")}
                  </Button>
                  <Button size="sm"
                    type="button"
                    variant="secondary"
                    disabled={busy}
                    onClick={test}
                  >
                    {busy ? <Spinner size="xs" /> : <Search size={12} />}
                    {t("connection.search.test")}
                  </Button>
                  {tested !== null && (
                    <span className="truncate text-[11.5px] text-fg-muted">
                      {tested}
                    </span>
                  )}
                </div>
              )}
            </div>
          )}
        </div>

        <div className="shrink-0 pt-0.5">
          <Switch
            aria-label={t("connection.search.enable", { name: meta?.name ?? id })}
            checked={status.config.enabled}
            disabled={blocked !== undefined}
            onCheckedChange={onToggle}
          />
        </div>
      </div>
    </li>
  );
}

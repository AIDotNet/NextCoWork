import { AlertTriangle, Check, Search } from "lucide-react";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { type ModelAlias } from "../../../../../shared/domain/provider";
import { Button } from "../../../components/arc/button/button";
import { Dialog } from "../../../components/ui/Dialog";
import { EmptyState } from "../../../components/arc/empty-state/empty-state";
import { TextInput } from "../../../components/ui/TextInput";
import { cn } from "../../../lib/cn";
import { useI18n } from "../../../i18n";
import { Spinner } from '../../../components/ui/Spinner'
import {
  fetchProviderModels,
  setProviderAliases,
} from "../../../services/provider";
import {
  filterRows,
  importRows,
  initialSelection,
  submitOrder,
  toggleAll,
  toggleRow,
  filterFetchedModels,
  type ImportModality,
  type ImportRow,
} from "./import-models";

/**
 * 参考图那个「导入模型」弹窗 —— 从服务商拉回列表,勾选后整表替换。
 *
 * ★★ **它是替换语义,不是追加**(参考图原话:「取消勾选会从当前列表删除」)。
 * 所以取消勾选一条 = 删掉那条别名,而底下的 `provider:setAliases` 就是照这个
 * 语义写的:一次调用定下这家的全部别名。做成 upsert / remove 两条频道的话,
 * 差集要在渲染层算,而那种错的表现是「取消勾选了但它还在」—— 不报错,只是没生效。
 *
 * ★ **判断逻辑全在 `import-models.ts`**,这里只有 DOM。理由写在那个文件头:
 * vitest 是 node 环境且只收 `.test.ts`,留在这个文件里的规则一行都不会被测到。
 *
 * ★ 嵌套在 `ProviderCatalog` / 设置浮层之上没问题:`Dialog` 会 portal 到 body
 * (它的文件头解释了为什么必须这样 —— 内容区的 `mask-image` 会成为 fixed 的包含块)。
 *
 * ★ **打开就拉,不给一颗「开始拉取」按钮。** 用户点的那颗
 * 「从服务商拉取模型列表」已经表达过意图了,再点一次是多余的一步;
 * 失败时这里有「重试」。
 */
export function ImportModelsDialog({
  open,
  providerId,
  providerName,
  aliases,
  modality = "text",
  preserveAliases = [],
  onClose,
  onDone,
}: {
  open: boolean;
  providerId: string;
  providerName: string;
  /** 这家现有的别名 —— 决定哪些默认勾上,以及哪些是「本地独有」 */
  aliases: readonly ModelAlias[];
  /** 图片页只允许导入图片模型；文本页保留原有完整列表。 */
  modality?: ImportModality;
  /** 同一供应商下其他模态的绑定，在替换图片模型时继续保留。 */
  preserveAliases?: readonly ModelAlias[];
  onClose: () => void;
  onDone?: () => void;
}): ReactNode {
  const { t } = useI18n();
  const [rows, setRows] = useState<ImportRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [query, setQuery] = useState("");
  const [note, setNote] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  /** 每次打开自增,useEffect 靠它重跑 —— 「重试」也是加一 */
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!open) return;
    let alive = true;
    setRows(null);
    setError(null);
    setNote(null);
    setQuery("");
    void fetchProviderModels(providerId)
      .then((fetched) => {
        if (!alive) return;
        const next = importRows(filterFetchedModels(fetched, modality), aliases);
        setRows(next);
        setSelected(initialSelection(next, Number.POSITIVE_INFINITY));
      })
      .catch((e: unknown) => {
        if (!alive) return;
        setError(e instanceof Error ? e.message : String(e));
      });
    /*
      ★ 卸载/重开时把回调作废。没有这一句的话,用户拉到一半关掉弹窗、
      换一家再打开,上一次的响应会晚到并把**另一家**的模型填进来 ——
      而那份列表看着完全正常,直到他点「更新列表」把别的家的模型名存进这家。
    */
    return () => {
      alive = false;
    };
    // ★ `aliases` 刻意不进依赖:store 每广播一次都是个新数组,进去就会无限重拉。
    // 它只在**打开的那一刻**被读一次,用来决定哪些默认勾上 —— 这正是想要的语义
    // (弹窗开着的时候别名被别处改了,不该把用户正在勾的东西重置掉)。
  }, [open, providerId, attempt, modality]);

  const visible = useMemo(() => filterRows(rows ?? [], query), [rows, query]);
  const allVisibleChecked =
    visible.length > 0 && visible.every((r) => selected.has(r.id));
  const fromUpstreamCount = (rows ?? []).filter((r) => r.fromUpstream).length;
  const localOnlyCount = (rows ?? []).length - fromUpstreamCount;

  const pick = (id: string): void => {
    const r = toggleRow(selected, id, Number.POSITIVE_INFINITY);
    setSelected(r.selected);
    setNote(r.atCap ? t("models.cannotSelect") : null);
  };

  const pickAll = (): void => {
    const r = toggleAll(selected, visible, Number.POSITIVE_INFINITY);
    setSelected(r.selected);
    setNote(null);
  };

  const submit = (): void => {
    if (rows === null) return;
    setSaving(true);
    setError(null);
    const selectedModels = submitOrder(rows, selected);
    const preservedModels = preserveAliases.map((alias) => alias.upstreamModel);
    void setProviderAliases(providerId, [...preservedModels, ...selectedModels])
      .then(() => {
        onDone?.();
        onClose();
      })
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : String(e)),
      )
      .finally(() => setSaving(false));
  };

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t("models.import")}
      description={
        rows === null
          ? t("models.fetching", { provider: providerName })
          : t("models.fetchedHint", { count: fromUpstreamCount })
      }
      width={560}
      footer={
        <>
          <Button size="sm" type="button" variant="secondary" onClick={onClose} disabled={saving}>
            {t("common.cancel")}
          </Button>
          <Button size="sm"
            type="button"
            variant="primary"
            disabled={rows === null || saving || selected.size === 0}
            onClick={submit}
          >
            {saving ? <Spinner size="sm" /> : undefined}
            {t("models.updateList", { count: selected.size })}
          </Button>
        </>
      }
    >
      {error !== null && (
        <div className="mb-3 flex items-start gap-2 rounded-[8px] border border-danger/40 bg-danger/5 px-2.5 py-2">
          <AlertTriangle size={13} className="mt-[2px] shrink-0 text-danger" />
          <div className="min-w-0 flex-1">
            {/* ★ 原样显示主进程那句 —— 它带着真实 URL 和状态码,是用户唯一能拿去查的东西 */}
            <p className="text-[11.5px] leading-[1.6] break-words text-danger">
              {error}
            </p>
          </div>
          {rows === null && (
            <Button size="sm" type="button" variant="secondary" onClick={() => setAttempt((n) => n + 1)}>
              {t("common.retry")}
            </Button>
          )}
        </div>
      )}

      {rows === null ? (
        error === null && (
          <EmptyState
            icon={<Spinner size="md" />}
            title={t("models.fetchingTitle")}
            description={t("models.fetchingHint")}
            className="py-12"
          />
        )
      ) : (
        <>
          <div className="flex items-center gap-3 pb-2.5">
            <div className="min-w-0 flex-1">
              <TextInput
                size="sm"
                value={query}
                onChange={setQuery}
                placeholder={t("models.searchId")}
                ariaLabel={t("models.searchId")}
                icon={<Search size={13} className="text-icon" />}
              />
            </div>
            <span className="shrink-0 text-[11.5px] text-fg-faint">
              {t("models.selectedCount", { count: selected.size })}
            </span>
            <button
              type="button"
              onClick={pickAll}
              className="app-no-drag shrink-0 text-[11.5px] text-fg-muted underline underline-offset-2 transition-colors hover:text-fg"
            >
              {allVisibleChecked ? t("models.clearAll") : t("models.selectAll")}
            </button>
          </div>

          {note !== null && (
            <p className="pb-2 text-[11.5px] text-danger">{note}</p>
          )}

          {visible.length === 0 ? (
            <EmptyState
              icon={<Search size={20} />}
              title={t("models.noMatch")}
              description={t("models.searchIdHint")}
              className="py-10"
            />
          ) : (
            <ul className="max-h-[46vh] overflow-y-auto overflow-x-hidden rounded-[8px] border border-border">
              {visible.map((r) => (
                <ModelRow
                  key={r.id}
                  row={r}
                  checked={selected.has(r.id)}
                  onToggle={() => pick(r.id)}
                />
              ))}
            </ul>
          )}

          {localOnlyCount > 0 && (
            /*
              ★ 参考图没有这一句,但没有它就是一次静默的数据丢失:弹窗是替换语义,
              而上游这次没报的本地别名如果不列出来,用户点「更新列表」会把它删掉,
              且他从头到尾没在弹窗里见过它。判据写在 `import-models.ts` 的 importRows。
            */
            <p className="mt-2.5 text-[11.5px] leading-[1.6] text-fg-faint">
              末尾 {localOnlyCount} 个标着「本地」的,这次上游没报回来 ——
              它们已经在你的列表里了,默认保留。取消勾选会删掉。
            </p>
          )}
        </>
      )}
    </Dialog>
  );
}

function ModelRow({
  row,
  checked,
  onToggle,
}: {
  row: ImportRow;
  checked: boolean;
  onToggle: () => void;
}): ReactNode {
  const { t } = useI18n();
  return (
    <li className="border-b border-hairline last:border-b-0">
      <button
        type="button"
        role="checkbox"
        aria-checked={checked}
        onClick={onToggle}
        className="app-no-drag flex w-full items-center gap-2.5 px-2.5 py-2 text-left transition-colors hover:bg-tint"
      >
        <span
          className={cn(
            "flex size-[15px] shrink-0 items-center justify-center rounded-[4px] border transition-colors",
            checked
              ? "border-accent bg-accent text-accent-fg"
              : "border-border",
          )}
          aria-hidden
        >
          {checked && <Check size={11} strokeWidth={3} />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate font-mono text-[12px] text-fg">
            {row.id}
          </span>
          {row.displayName !== undefined && row.displayName !== row.id && (
            <span className="block truncate text-[11px] text-fg-faint">
              {row.displayName}
            </span>
          )}
        </span>
        {/* 本地独有的角标和「已添加」不同 —— 两者都是已添加,但来源不一样,而来源决定了
            取消勾选的后果有多不可逆(上游那份下次还能拉回来,这份不能) */}
        {!row.fromUpstream ? <Tag>{t("models.local")}</Tag> : row.added && <Tag>{t("models.added")}</Tag>}
      </button>
    </li>
  );
}

function Tag({ children }: { children: ReactNode }): ReactNode {
  return (
    <span className="shrink-0 rounded-[5px] bg-tint px-1.5 py-0.5 text-[10.5px] text-fg-muted">
      {children}
    </span>
  );
}

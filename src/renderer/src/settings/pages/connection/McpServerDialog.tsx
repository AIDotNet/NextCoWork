/**
 * 「添加 / 编辑 MCP 服务器」弹窗 —— 参考图那个表单。
 *
 * 这里只有**接线**:校验、拼配置、调频道。所有能单测的东西都在
 * `settings/mcp-form.ts`(vitest 是 node 环境、只收 `.ts`,`.tsx` 测不到),
 * 那边 46 个用例钉着参数切分、`key=value` 解析、id 生成与校验。
 *
 * ## 密钥这一栏有两条不直观的规矩,都写在界面上了
 *
 * 1. **编辑时取不回旧值。** 多行框里显示的是 `GITHUB_TOKEN=`,等号后面空着 ——
 *    「凭证只写不读」的直接结果(方案 §9),不是加载失败。
 * 2. **一旦这次填了任何一个值,整张表按这次填的覆盖。** `mcp:setSecrets`
 *    是整体写而不是累加(那边的注释写了理由:累加会让用户删掉的值继续生效)。
 *    于是「只补填其中一个键」会把另外几个已存的值清掉 —— 这件事不能靠用户猜,
 *    所以下面会把**将被清掉的键名逐个列出来**。
 */
import { AlertTriangle } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import type {
  McpServerConfig,
  McpServerStatus,
  McpTransport,
} from "../../../../../shared/domain/mcp";
import { Button } from "../../../components/arc/button/button";
import { Dialog } from "../../../components/ui/Dialog";
import SegmentedControl from "../../../components/arc/segmented-control/segmented-control";
import { Select } from "../../../components/ui/Select";
import { TextInput } from "../../../components/ui/TextInput";
import { useI18n } from "../../../i18n";
import { cn } from "../../../lib/cn";
import { getMcpSecretsInfo, setMcpSecrets } from "../../../services/mcp";
import { useMcpStore } from "../../../stores/mcp";
import { useWindowStore } from '../../../stores/window';
import { Spinner } from '../../../components/ui/Spinner'
import {
  authorizationWarning,
  draftOf,
  emptyDraft,
  hasErrors,
  parseSecretLines,
  secretValues,
  suggestId,
  toConfig,
  validateDraft,
  type McpDraft,
  type McpFormErrors,
} from "../../mcp-form";

export function McpServerDialog({
  open,
  editing,
  existingIds,
  onClose,
}: {
  open: boolean;
  /** `null` = 新增 */
  editing: McpServerStatus | null;
  /** 用于查重。编辑时会把自己排除掉 */
  existingIds: readonly string[];
  onClose: () => void;
}): ReactNode {
  const { t } = useI18n();
  const transports: ReadonlyArray<{ value: McpTransport; label: string }> = [
    { value: "stdio", label: t('ssh.mcp.process') },
    { value: "streamable-http", label: "HTTP" },
    { value: "sse", label: "SSE" },
  ];
  const upsert = useMcpStore((s) => s.upsert);
  const [draft, setDraft] = useState<McpDraft>(emptyDraft);
  const [errors, setErrors] = useState<McpFormErrors>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const workspaces = useWindowStore((state) => state.workspaceTargets);
  const [workspaceId, setWorkspaceId] = useState('');
  /** 用户手动改过 id 之后就别再从名字推了 —— 推回去等于把他改的覆盖掉 */
  const [idTouched, setIdTouched] = useState(false);
  const [stored, setStored] = useState<{
    names: string[];
    encryptionAvailable: boolean;
  }>({
    names: [],
    encryptionAvailable: true,
  });

  // 每次打开都从头装一次:上一次的草稿留着会让「新增」带上一台服务器的残影
  useEffect(() => {
    if (!open) return;
    setDraft(editing === null ? emptyDraft() : draftOf(editing.config));
    setWorkspaceId(editing?.config.workspaceId ?? '');
    setErrors({});
    setFailure(null);
    setIdTouched(editing !== null);
    setStored({ names: [], encryptionAvailable: true });
    if (editing === null) return;

    let alive = true;
    void getMcpSecretsInfo(editing.config.id)
      .then((info) => {
        if (alive)
          setStored({
            // 远端设备可能是别的版本,回程形状不能完全信 —— 这里渲染期会 `.length`
            names: Array.isArray(info?.storedNames) ? info.storedNames : [],
            encryptionAvailable: info?.encryptionAvailable !== false,
          });
      })
      .catch((e: unknown) => console.error("[mcp] 读取密钥状态失败", e));
    return () => {
      alive = false;
    };
  }, [open, editing]);

  const set = <K extends keyof McpDraft>(k: K, v: McpDraft[K]): void => {
    setDraft((d) => ({ ...d, [k]: v }));
  };

  const others =
    editing === null
      ? existingIds
      : existingIds.filter((i) => i !== editing.config.id);
  const secretLines = parseSecretLines(draft.secretsText);
  const typed = secretValues(secretLines);
  const typedKeys = Object.keys(typed);
  // ★ 这次没填、但库里存着的键 —— 保存后会被这次的整体写覆盖掉
  const willClear =
    typedKeys.length === 0 ? [] : stored.names.filter((n) => !(n in typed));
  const authWarning = authorizationWarning(secretLines);

  const save = (): void => {
    const e = validateDraft(draft, others);
    setErrors(e);
    if (hasErrors(e)) return;

    setBusy(true);
    setFailure(null);
    const config: McpServerConfig = toConfig(
      draft,
      editing?.config.enabled !== false,
    );
    config.workspaceId = workspaceId || undefined;

    // 顺序有意义:先写配置(它声明了键名,主进程按那份声明过滤密钥),
    // 再写密钥(那一步会带着新值重连)。反过来的话密钥会被当成未声明而丢掉。
    void upsert(config)
      .then(() =>
        typedKeys.length === 0 ? undefined : setMcpSecrets(config.id, typed),
      )
      .then(() => {
        onClose();
      })
      .catch((err: unknown) => {
        setFailure(err instanceof Error ? err.message : String(err));
      })
      .finally(() => setBusy(false));
  };

  const stdio = draft.transport === "stdio";

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={
        editing === null
          ? t("connection.mcp.dialog.add")
          : t("connection.mcp.dialog.edit", { name: editing.config.name })
      }
      description={t("connection.mcp.dialog.description")}
      width={560}
      footer={
        <div className="flex items-center justify-end gap-2">
          <Button size="sm" type="button" variant="secondary" onClick={onClose} disabled={busy}>
            {t("common.cancel")}
          </Button>
          <Button size="sm" type="button" variant="primary" onClick={save} disabled={busy}>
            {busy ? (
              <Spinner size="sm" />
            ) : (
              t("connection.mcp.dialog.saveConnect")
            )}
          </Button>
        </div>
      }
    >
      <div className="flex flex-col gap-3.5">
        <Field label={t('ssh.mcp.target')}>
          <Select
            inModal
            value={workspaceId}
            ariaLabel={t('ssh.mcp.target')}
            onValueChange={setWorkspaceId}
            className="h-8 text-[13px]"
            options={[
              { value: '', label: t('ssh.mcp.local') },
              ...(workspaceId && !workspaces[workspaceId] ? [{ value: workspaceId, label: workspaceId }] : []),
              ...Object.values(workspaces).map((workspace) => ({ value: workspace.id, label: t('ssh.mcp.scope', { name: workspace.name }) }))
            ]}
          />
        </Field>
        <Field
          label={t("connection.mcp.dialog.transport")}
          hint={
            workspaceId ? t('ssh.mcp.scope', { name: workspaces[workspaceId]?.name ?? workspaceId }) : stdio
              ? t("connection.mcp.dialog.localHint")
              : t("connection.mcp.dialog.remoteHint")
          }
        >
          {/* ★ 用 Segmented 不用 Menu —— Menu 在滚动区里会被裁(ModelPage.tsx 记着这条) */}
          <SegmentedControl
            label={t("connection.mcp.dialog.transport")}
            value={draft.transport}
            options={[...transports]}
            onValueChange={(t) => set("transport", t as McpTransport)}
          />
        </Field>

        <div className="flex gap-2.5">
          <Field
            label={t("connection.mcp.dialog.name")}
            className="flex-1"
            error={errors.name}
          >
            <TextInput
              value={draft.name}
              ariaLabel={t("connection.mcp.dialog.serverName")}
              placeholder="GitHub"
              invalid={errors.name !== undefined}
              onChange={(name) => {
                setDraft((d) => ({
                  ...d,
                  name,
                  id: idTouched ? d.id : suggestId(name),
                }));
              }}
            />
          </Field>
          <Field
            label="ID"
            className="flex-1"
            error={errors.id}
            hint={t("connection.mcp.dialog.idHint")}
          >
            <TextInput
              value={draft.id}
              ariaLabel={t("connection.mcp.dialog.serverId")}
              placeholder="github"
              disabled={editing !== null}
              invalid={errors.id !== undefined}
              onChange={(id) => {
                setIdTouched(true);
                set("id", id);
              }}
            />
          </Field>
        </div>

        <Field
          label={t("connection.mcp.dialog.descriptionLabel")}
          hint={t("connection.mcp.dialog.descriptionHint")}
        >
          <TextInput
            value={draft.description}
            ariaLabel={t("connection.mcp.dialog.descriptionLabel")}
            placeholder={t("connection.mcp.dialog.descriptionPlaceholder")}
            onChange={(v) => set("description", v)}
          />
        </Field>

        {stdio ? (
          <>
            <Field
              label={t("connection.mcp.dialog.command")}
              error={errors.command}
            >
              <TextInput
                value={draft.command}
                ariaLabel={t("connection.mcp.dialog.command")}
                placeholder="npx"
                invalid={errors.command !== undefined}
                onChange={(v) => set("command", v)}
              />
            </Field>
            <Field
              label={t("connection.mcp.dialog.args")}
              hint={t("connection.mcp.dialog.argsHint")}
            >
              <TextInput
                value={draft.argsText}
                ariaLabel={t("connection.mcp.dialog.args")}
                placeholder="-y @modelcontextprotocol/server-everything"
                onChange={(v) => set("argsText", v)}
              />
            </Field>
            <Field
              label={t("connection.mcp.dialog.cwd")}
              hint={t("connection.mcp.dialog.cwdHint")}
            >
              <TextInput
                value={draft.cwd}
                ariaLabel={t("connection.mcp.dialog.cwd")}
                placeholder={t("connection.mcp.dialog.empty")}
                onChange={(v) => set("cwd", v)}
              />
            </Field>
          </>
        ) : (
          <Field label={t("connection.mcp.dialog.url")} error={errors.url}>
            <TextInput
              value={draft.url}
              ariaLabel={t("connection.mcp.dialog.url")}
              placeholder="https://example.com/mcp"
              inputMode="url"
              invalid={errors.url !== undefined}
              onChange={(v) => set("url", v)}
            />
          </Field>
        )}

        <Field
          label={
            stdio
              ? t("connection.mcp.dialog.env")
              : t("connection.mcp.dialog.headers")
          }
          hint={`一行一条,写成 名字=值。值存进系统密钥环,不会写进配置文件${
            editing === null ? "" : ";编辑时取不回已存的值,所以等号后面是空的"
          }`}
        >
          <textarea
            value={draft.secretsText}
            aria-label={
              stdio
                ? t("connection.mcp.dialog.env")
                : t("connection.mcp.dialog.headers")
            }
            spellCheck={false}
            rows={3}
            placeholder={
              stdio ? "GITHUB_TOKEN=ghp_xxx" : "Authorization=Bearer xxx"
            }
            onChange={(e) => set("secretsText", e.target.value)}
            className={cn(
              "apple-field app-no-drag selectable w-full resize-none rounded-[8px] border border-hairline",
              "bg-surface-field px-2.5 py-2 font-mono text-[12px] leading-[1.7] text-fg outline-none",
              "placeholder:text-fg-faint focus:border-fg-faint",
            )}
          />
          {stored.names.length > 0 && (
            <p className="mt-1.5 text-[11.5px] text-fg-faint">
              {t("connection.mcp.dialog.stored", {
                names: stored.names.join("、"),
              })}
            </p>
          )}
          {!stored.encryptionAvailable && (
            <Notice>{t("connection.mcp.dialog.keyringUnavailable")}</Notice>
          )}
          {willClear.length > 0 && (
            /* ★ 整体写的直接后果,说清楚而不是让用户事后发现 */
            <Notice>
              {t("connection.mcp.dialog.willClear", {
                names: willClear.join("、"),
              })}
            </Notice>
          )}
          {authWarning !== null && <Notice>{authWarning}</Notice>}
        </Field>

        {failure !== null && (
          <p className="rounded-[8px] bg-danger/10 px-2.5 py-2 text-[12px] text-danger">
            {failure}
          </p>
        )}
      </div>
    </Dialog>
  );
}

function Field({
  label,
  hint,
  error,
  className,
  children,
}: {
  label: string;
  hint?: string;
  error?: string;
  className?: string;
  children: ReactNode;
}): ReactNode {
  return (
    <div className={cn("min-w-0", className)}>
      <label className="mb-1.5 block text-[12.5px] text-fg">{label}</label>
      {children}
      {error !== undefined ? (
        <p className="mt-1.5 text-[11.5px] text-danger">{error}</p>
      ) : (
        hint !== undefined && (
          <p className="mt-1.5 text-[11.5px] text-fg-faint">{hint}</p>
        )
      )}
    </div>
  );
}

function Notice({ children }: { children: ReactNode }): ReactNode {
  return (
    <p className="mt-1.5 flex items-start gap-1.5 text-[11.5px] text-danger">
      <AlertTriangle size={12} className="mt-[2px] shrink-0" />
      <span>{children}</span>
    </p>
  );
}

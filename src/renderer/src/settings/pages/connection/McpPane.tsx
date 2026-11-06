/**
 * MCP —— 参考图那一页的服务器列表。
 *
 * ★ **一行显示的是「配置 + 连接状态」合成的一份**(`McpServerStatus`),
 * 不是渲染层拿两份自己 join。理由写在那个类型的注释里:两份各自到达,
 * 中间那一帧必然有一边是旧的 —— 表现为「刚加的服务器先显示未连接,
 * 一秒后才跳成已连接」,而它其实从来没断过。
 *
 * ★ **所有写操作都不本地 `set`**,等主进程的 `mcp:changed` 广播回来
 * (store 里写了理由)。于是「停用」这个开关按下去到变色之间有一小段延迟,
 * 那段延迟是真的 —— 它正在断开一个子进程。假装立刻断了才是骗人。
 */
import { Cable, Pencil, Plus, RefreshCw, Trash2 } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import type {
  McpConnectionState,
  McpServerStatus,
} from "../../../../../shared/domain/mcp";
import { Button } from "../../../components/arc/button/button";
import { EmptyState } from "../../../components/arc/empty-state/empty-state";
import { IconButton } from "../../../components/ui/IconButton";
import { Switch } from "../../../components/arc/switch/switch";
import { useI18n } from "../../../i18n";
import { cn } from "../../../lib/cn";
import { testMcpConnection } from "../../../services/mcp";
import { useMcpStore } from "../../../stores/mcp";
import { SettingGroup } from "../../Row";
import { McpServerDialog } from "./McpServerDialog";
import { normalizeMcpConfig } from "../../mcp-form";
import { ErrorBoundary } from "../../../components/ui/ErrorBoundary";
import { Spinner } from '../../../components/ui/Spinner'

const STATE_DOT: Readonly<Record<McpConnectionState, string>> = {
  disconnected: "bg-fg-faint",
  connecting: "bg-accent-soft",
  connected: "bg-accent",
  error: "bg-danger",
};

export function McpPane(): ReactNode {
  const { t } = useI18n();
  const { servers, loaded, error, load, upsert, remove } = useMcpStore();
  const [dialog, setDialog] = useState<{
    open: boolean;
    editing: McpServerStatus | null;
  }>({
    open: false,
    editing: null,
  });

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <SettingGroup>
      <div className="flex items-center justify-between px-4 py-3">
        <div className="min-w-0">
          <p className="text-[13px] text-fg">{t("connection.mcp.title")}</p>
          <p className="mt-0.5 text-[12px] text-fg-faint">
            {t("connection.mcp.hint")}
          </p>
        </div>
        <Button size="sm"
          type="button"
          variant="primary"
          onClick={() => setDialog({ open: true, editing: null })}
        >
          <Plus size={13} />
          {t("connection.mcp.add")}
        </Button>
      </div>

      {/* ★ 读失败和「一台都没加」是两回事,分开说 —— 混成一句会让用户去添加
          一台他其实已经加过的服务器 */}
      {error !== null ? (
        <div className="px-4 pb-4">
          <p className="rounded-[8px] bg-danger/10 px-2.5 py-2 text-[12px] text-danger">
            {t("connection.mcp.loadFailed", { error })}
          </p>
        </div>
      ) : !loaded ? (
        <div className="flex items-center justify-center gap-2 py-8 text-[12.5px] text-fg-faint">
          <Spinner size="sm" />
          {t("connection.mcp.reading")}
        </div>
      ) : servers.length === 0 ? (
        <EmptyState
          icon={<Cable size={22} />}
          title={t("connection.mcp.empty")}
          description={t("connection.mcp.emptyHint")}
        />
      ) : (
        <ul className="border-t border-hairline">
          {servers.map((s) => (
            <ServerRow
              key={s.config.id}
              status={s}
              onToggle={(enabled) =>
                void upsert({ ...normalizeMcpConfig(s.config), enabled })
              }
              onEdit={() => setDialog({ open: true, editing: s })}
              onRemove={() => void remove(s.config.id)}
            />
          ))}
        </ul>
      )}

      {/* ★ 弹窗里任何一处渲染期异常都只坏这一块,不能把整个窗口带成白屏。
          resetKey 跟着「这次打开的是谁」走:关掉再开,错误态随之清掉 */}
      <ErrorBoundary
        resetKey={dialog.open ? (dialog.editing?.config.id ?? "__new__") : null}
        label="mcp-dialog"
        onDismiss={() => setDialog({ open: false, editing: null })}
      >
        <McpServerDialog
          open={dialog.open}
          editing={dialog.editing}
          existingIds={servers.map((s) => s.config.id)}
          onClose={() => setDialog({ open: false, editing: null })}
        />
      </ErrorBoundary>
    </SettingGroup>
  );
}

function ServerRow({
  status,
  onToggle,
  onEdit,
  onRemove,
}: {
  status: McpServerStatus;
  onToggle: (enabled: boolean) => void;
  onEdit: () => void;
  onRemove: () => void;
}): ReactNode {
  const { t } = useI18n();
  const { state, tools, toolCount } = status;
  // 库里的 JSON 不保证是当前形状(缺 `args` 时下面的展开会直接抛)—— 见 normalizeMcpConfig
  const config = normalizeMcpConfig(status.config);
  const [testing, setTesting] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  /** 「测试连接」的结果。失败是正常返回值,不是异常 —— 见 services/mcp.ts */
  const [tested, setTested] = useState<string | null>(null);

  const test = (): void => {
    setTesting(true);
    setTested(null);
    void testMcpConnection(config.id)
      .then((r) => {
        setTested(
          r.ok
            ? t("connection.mcp.connected", { count: r.data.toolCount })
            : r.error.message,
        );
      })
      .finally(() => setTesting(false));
  };

  const where =
    config.transport === "stdio"
      ? [config.command, ...config.args].join(" ")
      : config.url;

  return (
    <li className="border-b border-hairline px-4 py-3 last:border-b-0">
      <div className="flex items-start gap-3">
        <span
          className={cn(
            "mt-[7px] size-[7px] shrink-0 rounded-full",
            STATE_DOT[state],
          )}
          aria-hidden
        />
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="truncate text-[13px] text-fg">{config.name}</span>
            <span className="shrink-0 rounded-[5px] bg-tint px-1.5 py-px text-[11px] text-fg-faint">
              {config.transport === "stdio"
                ? t("connection.mcp.transport.stdio")
                : config.transport === "sse"
                  ? "SSE"
                  : "HTTP"}
            </span>
            <span className="shrink-0 text-[11.5px] text-fg-faint">
              {t(
                `connection.mcp.status.${state}` as
                  | "connection.mcp.status.disconnected"
                  | "connection.mcp.status.connecting"
                  | "connection.mcp.status.connected"
                  | "connection.mcp.status.error",
              )}
            </span>
          </div>

          {config.description !== undefined && config.description !== "" && (
            <p className="mt-0.5 truncate text-[12px] text-fg-muted">
              {config.description}
            </p>
          )}
          <p className="mt-0.5 truncate font-mono text-[11.5px] text-fg-faint">
            {where}
          </p>

          {status.error !== undefined && (
            <p className="mt-1.5 text-[11.5px] text-danger">{status.error}</p>
          )}
          {tested !== null && (
            <p className="mt-1.5 text-[11.5px] text-fg-muted">{tested}</p>
          )}

          {/* 工具数是这台服务器有没有真的在干活的唯一可见证据 —— 连上了但零工具
              通常意味着服务器起来了、握手也过了,但它什么也没暴露 */}
          {toolCount > 0 && (
            <button
              type="button"
              className="app-no-drag mt-1.5 text-[11.5px] text-accent hover:underline"
              onClick={() => setExpanded((v) => !v)}
            >
              {t("connection.mcp.tools", { count: toolCount })}
              {expanded ? " ▾" : " ▸"}
            </button>
          )}
          {expanded && (
            <div className="mt-1.5 flex flex-wrap gap-1.5">
              {tools.map((t) => (
                <code
                  key={t}
                  className="selectable rounded-[5px] bg-tint px-1.5 py-px text-[11px] text-fg-muted"
                >
                  {t}
                </code>
              ))}
            </div>
          )}
        </div>

        <div className="flex shrink-0 items-center gap-1">
          <IconButton
            label={t("connection.mcp.test")}
            onClick={test}
            disabled={testing}
          >
            {testing ? (
              <Spinner size="sm" />
            ) : (
              <RefreshCw size={14} />
            )}
          </IconButton>
          <IconButton label={t("connection.mcp.edit")} onClick={onEdit}>
            <Pencil size={14} />
          </IconButton>
          <IconButton
            label={
              confirmingDelete
                ? t("common.confirmDelete")
                : t("connection.mcp.delete")
            }
            onClick={() => {
              if (confirmingDelete) {
                setConfirmingDelete(false);
                onRemove();
              } else {
                setConfirmingDelete(true);
              }
            }}
            className={confirmingDelete ? "text-danger hover:bg-danger/10 hover:text-danger" : undefined}
          >
            <Trash2 size={14} />
          </IconButton>
          <Switch
            aria-label={t("connection.mcp.enable", { name: config.name })}
            checked={config.enabled}
            onCheckedChange={onToggle}
          />
        </div>
      </div>
    </li>
  );
}

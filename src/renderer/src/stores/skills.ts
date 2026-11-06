import { create } from "zustand";
import type { SkillListItem } from "../../../shared/domain/skill";
import {
  listSkills,
  listSkillDiagnostics,
  setSkillGlobalEnabled,
  setSkillWorkspaceActive,
} from "../services/skills";
import { on } from "../services/ipc";
import type { InstallProgress } from "../lib/install-progress";
import { skillMessageKey } from "../views/skills/skill-error";
import type { TranslationKey } from "../i18n";

interface SkillsState {
  items: SkillListItem[];
  diagnostics: Array<{ path: string; message: string }>;
  loading: boolean;
  error: "skills.loadFailed" | "skills.diagnosticsFailed" | null;
  workspaceId: string | null;
  /**
   * 正在装的那些,按 `market:<slug>` / `local:<路径>` 索引。
   *
   * ★ 放 store 不放组件:这条事件是**全局广播**的(别的窗口发起的安装,这个
   * 窗口的市场页也要看到那颗按钮在跑),而组件里的 useState 一关页面就没了 ——
   * 而装一个包这件事在用户离开 Skill 页之后还在继续。
   */
  installProgress: Record<string, InstallProgress>;
  /** 最后一次失败的原因,同一套 key。用户再点一次安装时清掉 */
  installError: Record<string, TranslationKey>;
  load: (workspaceId: string | null) => Promise<void>;
  toggleGlobal: (skillId: string) => Promise<void>;
  toggleWorkspace: (skillId: string) => Promise<void>;
  /** 用户又点了一次安装 —— 把这个 key 上一次的失败抹掉 */
  clearInstallError: (key: string) => void;
}

let latestRequest = 0;

/**
 * 一次安装最多挂多久。
 *
 * ★ 没有这个兜底的话,主进程崩掉 / 那一帧终态事件丢了,进度条就**永远**留在
 * 界面上,而那颗按钮同时也永远点不动 —— 用户唯一的出路是重启应用。
 * 3 分钟是按 20MB 上限在很慢的网上算的。同 `stores/plugins.ts`。
 */
const PROGRESS_STALE_MS = 3 * 60 * 1000;

/**
 * `done` 之后延迟多久再把进度条撤掉。
 *
 * ★ 不能立刻撤:主进程是先 `skills:changed` 后 `done`,而渲染层收到 changed
 * 还要再打一次 `skills:list` 才拿到新列表。中间那几十毫秒里撤掉进度条,
 * 按钮会闪回「安装 Skill」再跳到「已安装」。
 */
const DONE_LINGER_MS = 400;

function without<T>(record: Record<string, T>, key: string): Record<string, T> {
  if (!(key in record)) return record;
  const next = { ...record };
  delete next[key];
  return next;
}

/** 顺手扫掉挂太久的 —— 挂在每一帧进度上,不另起定时器 */
function sweepStale(
  record: Record<string, InstallProgress>,
): Record<string, InstallProgress> {
  const now = Date.now();
  const stale = Object.keys(record).filter(
    (key) => now - (record[key]?.startedAt ?? now) > PROGRESS_STALE_MS,
  );
  if (stale.length === 0) return record;
  const next = { ...record };
  for (const key of stale) delete next[key];
  return next;
}

let subscribed = false;

/**
 * ★ 订阅装在第一次 `load()` 里,不在模块顶层、也不在组件的 useEffect 里。
 * 顶层 `on(...)` 会在 import 那一刻去碰 `window.nextcowork`,任何 import 到
 * 这个 store 的单测都会在 import 阶段炸;放进组件的 useEffect 则是页面一关
 * 就收不到了,而装一个 Skill 在用户离开这一页之后还在继续。同 `stores/plugins.ts`。
 */
function subscribeOnce(): void {
  if (subscribed) return;
  subscribed = true;
  on("skills:installProgress", (event) => {
    const { key, phase } = event;
    if (phase === "failed") {
      useSkillsStore.setState((state) => ({
        installProgress: without(state.installProgress, key),
        installError: {
          ...state.installError,
          [key]: skillMessageKey(event.messageKey ?? ""),
        },
      }));
      return;
    }
    if (phase === "done") {
      setTimeout(() => {
        useSkillsStore.setState((state) => ({
          installProgress: without(state.installProgress, key),
        }));
      }, DONE_LINGER_MS);
      return;
    }
    useSkillsStore.setState((state) => ({
      installProgress: {
        ...sweepStale(state.installProgress),
        [key]: {
          phase,
          ...(event.received === undefined ? {} : { received: event.received }),
          ...(event.total === undefined ? {} : { total: event.total }),
          startedAt: state.installProgress[key]?.startedAt ?? Date.now(),
        },
      },
    }));
  });
}

export const useSkillsStore = create<SkillsState>((set, get) => ({
  items: [],
  diagnostics: [],
  loading: false,
  error: null,
  workspaceId: null,
  installProgress: {},
  installError: {},
  clearInstallError(key) {
    set((state) => ({ installError: without(state.installError, key) }));
  },
  async load(workspaceId) {
    subscribeOnce();
    const request = ++latestRequest;
    const changedWorkspace = get().workspaceId !== workspaceId;
    set({
      workspaceId,
      loading: true,
      error: null,
      ...(changedWorkspace ? { items: [], diagnostics: [] } : {}),
    });
    const [items, diagnostics] = await Promise.allSettled([
      listSkills(workspaceId ?? undefined),
      listSkillDiagnostics(workspaceId ?? undefined),
    ]);
    if (request !== latestRequest) return;
    set({
      loading: false,
      ...(items.status === "fulfilled" ? { items: items.value } : {}),
      ...(diagnostics.status === "fulfilled"
        ? { diagnostics: diagnostics.value }
        : {}),
      error:
        items.status === "rejected"
          ? "skills.loadFailed"
          : diagnostics.status === "rejected"
            ? "skills.diagnosticsFailed"
            : null,
    });
  },
  async toggleGlobal(skillId) {
    const item = get().items.find((value) => value.id === skillId);
    if (!item) return;
    await setSkillGlobalEnabled(skillId, !item.globalEnabled);
    await get().load(get().workspaceId);
  },
  async toggleWorkspace(skillId) {
    const { workspaceId, items } = get();
    if (!workspaceId) return;
    const item = items.find((value) => value.id === skillId);
    if (!item) return;
    await setSkillWorkspaceActive(
      skillId,
      workspaceId,
      !item.activeInWorkspace,
    );
    await get().load(workspaceId);
  },
}));

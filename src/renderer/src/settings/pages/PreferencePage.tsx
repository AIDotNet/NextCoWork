/**
 * 偏好 —— 三个子 Tab:主题 / 快捷键 / 个性化。
 *
 * **主题**是参考图里紧挨着的三组控件:
 *
 * - **外观模式** —— 只管深浅,不管颜色;
 * - **图片主题** —— 选了图就**盖过**下面那一栏(见 `theme.ts` 的 `tokensOf`);
 * - **颜色主题** —— 七套色板,「随机」和「自定义」那两套按种子现算。
 *
 * ★ 主题这一段**一个判断都不做**:选哪套、认不认识这个 id、什么颜色,
 * 全在 `shared/domain/theme.ts` 里(那边在 vitest 里跑得起来,这边跑不起来 ——
 * 测试环境是 node,没有 document)。这里只摆控件、调 `patch`。
 *
 * ★ **个性化那三栏和这一页其余部分不是一类东西。** 主题改的是像素,
 * 而它改的是**模型看到什么** —— 三个字段会被拼进每一次对话的系统提示词
 * (`main/kernel/context-assembler.ts` 的 `buildPersonalizationSection`)。
 * 它落在「偏好」而不是「通用 › Agent」,是因为参考图这么放,而参考图是对的:
 * 用户找的是「怎么让它按我的方式说话」,那和挑配色是同一种心情。
 */
import { Check, Pipette, Trash2, Upload, RotateCcw, Keyboard } from "lucide-react";
import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from "react";
import type {
  ResolvedTheme,
  ThemePreference,
} from "../../../../shared/domain/settings";
import { PERSONALIZATION_MAX } from "../../../../shared/domain/settings";
import type { ImageRender, ImageTheme } from "../../../../shared/domain/theme";
import {
  COLOR_THEMES,
  CUSTOM_COLOR_THEME_ID,
  DEFAULT_CUSTOM_SEED,
  IMAGE_THEMES,
  RANDOM_COLOR_THEME_ID,
  normalizeHex,
  paletteOf,
  resolveColorTheme,
  swatchOf,
  type ColorTheme,
} from "../../../../shared/domain/theme";
import { Button } from "../../components/arc/button/button";
import SegmentedControl from "../../components/arc/segmented-control/segmented-control";
import { TextArea } from "../../components/ui/TextArea";
import { TextInput } from "../../components/ui/TextInput";
import { useDraft } from "../../components/ui/useDraft";
import { useI18n } from "../../i18n";
import { acceleratorFromKeyboardEvent, prettyAccelerator } from "../../lib/accelerator";
import { cn } from "../../lib/cn";
import { useImageThemes } from "../../stores/imageTheme";
import { useAppearance } from "../../theme/useAppearance";
import { ThemeStudioPane } from "../theme-studio/ThemeStudioPane";
import { SettingField, SettingGroup, SettingRow } from "../Row";
import type { SettingsPageProps } from "../props";

export function PreferencePage({
  settings,
  sub,
  patch,
}: SettingsPageProps): ReactNode {
  if (sub === "shortcut") return <ShortcutPane settings={settings} patch={patch} />;
  if (sub === "personalization") {
    return <PersonalizationPane settings={settings} patch={patch} />;
  }
  return <ThemeStudioPane settings={settings} patch={patch} />;
}

export function ThemePane({
  settings,
  patch,
}: Omit<SettingsPageProps, "sub">): ReactNode {
  const { t } = useI18n();
  // 色板要按**当前生效**的深浅画:同一套主题两个外观是两张表
  const appearance = useAppearance();
  const imageId = settings.imageTheme.id;

  /** 上传的那几张由主进程提供稳定的 ncw:// URL，浏览器直接负责加载。 */
  const uploaded = useImageThemes((s) => s.uploaded);
  const [importing, setImporting] = useState(false);
  const [importError, setImportError] = useState<string | null>(null);

  async function upload(): Promise<void> {
    setImporting(true);
    setImportError(null);
    try {
      const added = await useImageThemes.getState().importOne();
      // 导入完直接选上 —— 点「上传图片」的意图就是要看到它铺开,
      // 再让用户回头点一次卡片是多余的一步。取消选文件时 added 是 null
      if (added !== null) patch({ imageTheme: { id: added.id } });
    } catch (err) {
      // 解不开的图、超大的文件、写不进 userData —— 这些都只该让这一行变红,
      // 不该让整个设置页白掉
      setImportError(err instanceof Error ? err.message : String(err));
    } finally {
      setImporting(false);
    }
  }

  async function removeUploaded(id: string): Promise<void> {
    await useImageThemes.getState().remove(id);
    // ★ 删掉的正好是选中那张时,**必须同时把选择清掉**。否则
    // `imageTheme.id` 指着一条不存在的记录:`resolveImageTheme` 查不到 →
    // 整套 token 悄悄退回颜色主题,而界面上没有任何一张卡显示选中,
    // 下面那栏还是灰的 —— 一个说不清自己为什么会这样的状态
    if (id === imageId) patch({ imageTheme: { id: null } });
  }

  return (
    <>
      <SettingGroup>
        <SettingRow
          title={t("preference.appearance")}
          description={t("preference.appearanceHint")}
          wide
        >
          <SegmentedControl
            label={t("preference.appearance")}
            value={settings.theme}
            options={[
              { value: "system", label: t("preference.followSystem") },
              { value: "light", label: t("preference.light") },
              { value: "dark", label: t("preference.dark") },
            ]}
            onValueChange={(theme) => patch({ theme: theme as ThemePreference })}
          />
        </SettingRow>

        <SettingField
          title={t("preference.imageTheme")}
          description={t("preference.imageThemeHint")}
        >
          <div className="grid grid-cols-3 gap-2.5">
            {IMAGE_THEMES.map((t) => (
              <ImageCard
                key={t.id}
                theme={t}
                selected={t.id === imageId}
                // 再点一次取消 —— 回到下面那栏选着的颜色主题,不是回到默认
                onClick={() =>
                  patch({ imageTheme: { id: t.id === imageId ? null : t.id } })
                }
              />
            ))}

            {/* 上传的排在内置六张后面,同一个网格 —— 它们是同一类东西,
                只是一个来自渐变配方、一个来自 userData 里的文件 */}
            {uploaded.map((theme) => (
              <ImageCard
                key={theme.id}
                theme={theme}
                backdrop={
                  theme.source.kind === "uploaded"
                    ? theme.source.url
                    : undefined
                }
                selected={theme.id === imageId}
                onClick={() =>
                  patch({
                    imageTheme: { id: theme.id === imageId ? null : theme.id },
                  })
                }
                onDelete={() => void removeUploaded(theme.id)}
                deleteLabel={t("preference.deleteImage", { name: theme.name })}
              />
            ))}
          </div>

          <div className="mt-3 flex items-center justify-between gap-4">
            <div
              className={cn(
                "transition-opacity",
                // 没选图时这两颗药丸没有作用对象。仍然可见(它记着上次的选择),
                // 但点不动 —— 点了没反应比灰着更让人困惑
                imageId === null && "pointer-events-none opacity-40",
              )}
            >
              <SegmentedControl
                label={t("preference.renderMode")}
                value={settings.imageTheme.render}
                options={[
                  { value: "blur", label: t("preference.blur") },
                  { value: "overlay", label: t("preference.overlay") },
                ]}
                onValueChange={(render) => patch({ imageTheme: { render: render as ImageRender } })}
              />
            </div>

            <div className="flex min-w-0 items-center justify-end gap-2 text-[11.5px]">
              {importError !== null && (
                <span
                  className="selectable truncate text-danger"
                  title={importError}
                >
                  {importError}
                </span>
              )}
              <Button size="sm"
                type="button"
                variant="secondary"
                disabled={importing}
                onClick={() => void upload()}
              >
                <Upload size={13} />
                {/* 选文件框 + 读盘 + 解码取色,大图能到一两秒 —— 这几百毫秒里
                    按钮必须说话,否则用户会再点一次 */}
                {importing
                  ? t("preference.processing")
                  : t("preference.uploadImage")}
              </Button>
            </div>
          </div>
        </SettingField>

        <SettingField
          title={t("preference.colorTheme")}
          description={t("preference.colorThemeHint")}
          last
        >
          <div
            className={cn(
              "grid grid-cols-2 gap-2 transition-opacity",
              imageId !== null && "opacity-40",
            )}
          >
            {COLOR_THEMES.map((t) => (
              <ColorCard
                key={t.id}
                // 「随机」和「自定义」在表里都只是占位声明,真正的色板要现算 ——
                // 所以整表统一走一遍 `resolveColorTheme`,不在这一页认那两个 id。
                // 强制换成这张卡的 id,其余字段(种子、自定义色)沿用设置里存着的那份 ——
                // 画的是「这张卡会给我什么」,不是「我现在选着什么」
                theme={resolveColorTheme({ ...settings.colorTheme, id: t.id })}
                appearance={appearance}
                selected={t.id === settings.colorTheme.id && imageId === null}
                onClick={() =>
                  patch({
                    colorTheme:
                      t.id === RANDOM_COLOR_THEME_ID
                        ? // ★ 重掷用时间戳,不是 `seed + 1`。`settings` 是 prop,一次
                          // patch 要经主进程绕回来才更新,连点两下时第二下读到的还是
                          // 旧值 —— `+1` 会算出同一个种子,表现是「点了两次只换一次色」。
                          // 时间戳不依赖上一次的结果,而 `randomSeedColor` 走的是黄金角,
                          // 相邻两个数照样隔着 137.5°。
                          { id: t.id, seed: Date.now() }
                        : { id: t.id },
                  })
                }
              />
            ))}

            {/* 取色器占的是最后一行右手边那个空格 —— 「自定义」是七套里的第七套,
                两列排下来它旁边本来就空着,取色器正好落在它边上 */}
            <SeedPicker
              value={settings.colorTheme.custom}
              onCommit={(custom) =>
                patch({ colorTheme: { id: CUSTOM_COLOR_THEME_ID, custom } })
              }
              labels={{
                pickAccent: t("preference.pickAccent"),
                customAccent: t("preference.customAccent"),
                accentHex: t("preference.accentHex"),
              }}
            />
          </div>
        </SettingField>
      </SettingGroup>
    </>
  );
}

/** 快捷键。当前提供打开设置这一条，后续动作可沿同一结构继续扩展。 */
function ShortcutPane({ settings, patch }: Omit<SettingsPageProps, "sub">): ReactNode {
  const { t } = useI18n();
  const [recording, setRecording] = useState(false);
  const shortcut = settings.shortcuts.openSettings;

  const onRecord = (event: ReactKeyboardEvent<HTMLButtonElement>): void => {
    event.preventDefault();
    event.stopPropagation();
    const next = acceleratorFromKeyboardEvent(event.nativeEvent);
    if (next !== undefined) {
      patch({ shortcuts: { openSettings: next } });
      setRecording(false);
    }
  };

  return (
    <SettingGroup title={t("preference.shortcutGroup")}>
      <SettingRow
        title={t("preference.openSettings")}
        description={
          <>
            {t("preference.openSettingsHint")} {t("preference.shortcutModifierHint")}
          </>
        }
        last
      >
        <div className="flex items-center gap-2">
          <button
            type="button"
            aria-label={t("preference.recordShortcut")}
            aria-keyshortcuts={shortcut}
            aria-pressed={recording}
            onClick={() => setRecording(true)}
            onKeyDown={recording ? onRecord : undefined}
            onBlur={() => setRecording(false)}
            className={cn(
              "app-no-drag inline-flex h-9 min-w-[92px] items-center justify-center gap-2 rounded-[8px] px-3 font-sans text-[12px] transition",
              recording
                ? "bg-accent text-accent-fg ring-2 ring-accent/40"
                : "bg-tint text-fg hover:bg-tint-strong"
            )}
          >
            {recording ? <Keyboard size={13} /> : null}
            {recording ? t("preference.pressShortcut") : prettyAccelerator(shortcut)}
          </button>
          <button
            type="button"
            aria-label={t("preference.resetShortcut")}
            title={t("preference.resetShortcut")}
            onClick={() => patch({ shortcuts: { openSettings: "CmdOrCtrl+," } })}
            className="app-no-drag grid size-8 place-items-center rounded-[8px] text-fg-faint transition hover:bg-tint hover:text-fg"
          >
            <RotateCcw size={14} />
          </button>
        </div>
      </SettingRow>
    </SettingGroup>
  );
}

/**
 * 个性化。三个字段全部通栏(`SettingField`),因为它们是**要写字的**框 ——
 * 右对齐的 318px 控件列是给药丸和开关的,一个 318px 宽的多行框写不下一段自我介绍。
 *
 * ★ 三个都走草稿 + 失焦提交(`useDraft`),不是逐键 `patch`:理由在那个 hook 里。
 */
function PersonalizationPane({
  settings,
  patch,
}: Omit<SettingsPageProps, "sub">): ReactNode {
  const { t } = useI18n();
  const p = settings.personalization;
  const name = useDraft(p.name);

  return (
    <SettingGroup>
      <SettingField title={t("preference.name")} description={t("preference.nameHint")}>
        <TextInput
          value={name.draft}
          ariaLabel={t("preference.name")}
          placeholder={t("preference.namePlaceholder")}
          onChange={name.setDraft}
          // 单行框的 Enter 就是「我写完了」,和失焦同义 —— `TextInput` 两条都走 onCommit
          onCommit={() =>
            name.commit((v) => patch({ personalization: { name: v } }))
          }
          onFocus={name.onFocus}
        />
      </SettingField>

      <SettingField
        title={t("preference.background")}
        description={t("preference.backgroundHint")}
      >
        <TextArea
          value={p.background}
          rows={3}
          maxLength={PERSONALIZATION_MAX.background}
          ariaLabel={t("preference.background")}
          placeholder={t("preference.backgroundPlaceholder")}
          onCommit={(background) => patch({ personalization: { background } })}
        />
      </SettingField>

      <SettingField
        title={t("preference.instructions")}
        description={t("preference.instructionsHint")}
        last
      >
        <TextArea
          value={p.instructions}
          rows={6}
          maxLength={PERSONALIZATION_MAX.instructions}
          ariaLabel={t("preference.instructions")}
          placeholder={t("preference.instructionsPlaceholder")}
          onCommit={(instructions) =>
            patch({ personalization: { instructions } })
          }
        />
      </SettingField>
    </SettingGroup>
  );
}

/**
 * 一张图片卡。内置的六张是渐变配方,直接当 `background-image` 用 ——
 * 卡片上画的就是界面上会铺的那一层,不是另找一张缩略图。
 * 上传的那种画的是位图本身(`backdrop`),兑现之前退回种子色。
 */
function ImageCard({
  theme,
  backdrop,
  selected,
  onClick,
  onDelete,
  deleteLabel,
}: {
  theme: ImageTheme;
  /** 上传那种的 `blob:` URL。还没兑现时是 undefined —— 不是「这张图坏了」 */
  backdrop?: string;
  selected: boolean;
  onClick: () => void;
  /** 只有上传的那种给 —— 内置的六张删不掉 */
  onDelete?: () => void;
  deleteLabel?: string;
}): ReactNode {
  const { t } = useI18n();
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  return (
    // ★ 删除键不能嵌进卡片那颗 <button> 里:按钮套按钮浏览器会把内层拆出去,
    // 点删除就变成了点卡片。所以两颗是兄弟,靠这层 relative 叠在一起
    <div className="group relative">
      <button
        type="button"
        aria-pressed={selected}
        aria-label={theme.name}
        onClick={onClick}
        className={cn(
          "app-no-drag relative block aspect-[5/3] w-full overflow-hidden rounded-[10px] transition",
          selected
            ? "ring-2 ring-accent"
            : "ring-1 ring-hairline hover:ring-border",
        )}
        style={{
          // 底下这层纯色是**兜底,不是装饰**:上传的那种在主进程把字节递过来之前
          // 没有底图,而颜色本来就只由 seed 决定 —— 所以这张卡从第一帧起就是对的
          backgroundColor: theme.seed,
          backgroundImage:
            backdrop !== undefined
              ? `url("${backdrop}")`
              : theme.source.kind === "builtin"
                ? theme.source.css
                : undefined,
          backgroundSize: "cover",
          backgroundPosition: "center",
        }}
      >
        {/* 底部压一道暗,好让名字和色点落在任何一张图上都读得见 */}
        <span className="absolute inset-x-0 bottom-0 h-2/3 bg-gradient-to-t from-black/60 to-transparent" />
        <span
          className={cn(
            "absolute bottom-1.5 left-2 truncate text-left text-[11.5px] font-medium text-white",
            // 选中时右边那串色点占掉约 58px。**只在选中时让位** —— 内置那六张
            // 名字都是四个字,左对齐的文字在更宽的盒子里长得一模一样,
            // 所以这条分支对已经定稿的六张卡是零变化;它是为上传的名字(可到 48 字)加的
            selected ? "right-[58px]" : "right-2",
          )}
        >
          {theme.name}
        </span>

        {selected && (
          <>
            <span className="absolute top-1.5 right-1.5 grid size-[18px] place-items-center rounded-full bg-accent text-accent-fg">
              <Check size={12} strokeWidth={3} />
            </span>
            {/* 选中才画色点:六张全画会糊成一片,而它们真正的用处是
                「我选的这张会给我哪几个颜色」 */}
            <span className="absolute right-2 bottom-2 flex gap-1">
              {paletteOf(theme).map((c) => (
                <span
                  key={c}
                  className="size-2.5 rounded-full ring-1 ring-white/60"
                  style={{ background: c }}
                />
              ))}
            </span>
          </>
        )}
      </button>

      {onDelete !== undefined && (
        <button
          type="button"
          aria-label={confirmingDelete ? t("common.confirmDelete") : deleteLabel}
          title={confirmingDelete ? t("common.confirmDelete") : deleteLabel}
          onClick={() => {
            if (confirmingDelete) {
              setConfirmingDelete(false);
              onDelete();
            } else {
              setConfirmingDelete(true);
            }
          }}
          className={cn(
            "app-no-drag absolute top-1.5 left-1.5 grid min-h-[18px] place-items-center rounded-full",
            confirmingDelete ? "px-1.5" : "size-[18px]",
            "bg-black/45 text-white/90 opacity-0 transition",
            // 悬停才露出来 —— 常驻的话六张内置卡和上传卡长得不一样,
            // 而它们本该是同一类东西。focus-visible 是键盘的那条路
            "group-hover:opacity-100 hover:bg-danger hover:text-white focus-visible:opacity-100",
          )}
        >
          {confirmingDelete ? (
            <span className="text-[10px] leading-4">{t("common.confirmDelete")}</span>
          ) : (
            <Trash2 size={11} />
          )}
        </button>
      )}
    </div>
  );
}

/**
 * 「自定义」那一套的取色口:一个系统取色盘 + 一个 hex 文本框,两者共用一份草稿。
 *
 * ★ **草稿是本地的,但它不是「settings 的镜像」。** 这一页的规矩是绝不镜像
 * `settings`(镜像会让控件「点下去闪一下又弹回原样」)。这里破例只因为
 * **文本框有中间态**:打到 `#3` 的那一刻它既不是上一个颜色也不是下一个颜色。
 * 所以草稿只在**外面真的换了值**时才被冲掉 —— `lastSent` 用来认出
 * 「这次回声是我自己发出去的」;别的窗口改的、或从磁盘读回来的,才会覆盖草稿。
 *
 * ★ **必须防抖。** macOS 的取色盘在拖动时连续触发 `onChange`,一帧一次
 * IPC 往返 + 广播 + 重写 22 个 token,拖两秒就是上百趟。120ms 既让拖动手感
 * 是连续的(界面跟着草稿走,不等回声),又把落盘压到几次。
 *
 * ★ **半截的 hex 不 patch,只标红。** 否则打到 `#3` 那一刻整个界面会闪一次;
 * 而 `hexToHsl` 压根不校验,`#3` 一路走下去会吐出 `#NaNNaNNaN`(见 `normalizeHex`)。
 */
function SeedPicker({
  value,
  onCommit,
  labels,
}: {
  value: string;
  onCommit: (hex: string) => void;
  labels: { pickAccent: string; customAccent: string; accentHex: string };
}): ReactNode {
  /**
   * ★ **进来的这个值也可能是脏的。** 它是从磁盘读回来的,而磁盘上可能存着
   * 上一个版本写的、手改过的、或者写了一半的东西 —— `resolveColorTheme`
   * 对这种值的处理是落回 `DEFAULT_CUSTOM_SEED`,这里必须跟它一致:
   * 圆点画的得是**界面实际在用的那个色**,而不是一段谁也认不出来的字符串。
   * (输入框标红只该发生在用户自己打字的时候。)
   */
  const effective = normalizeHex(value) ?? DEFAULT_CUSTOM_SEED;
  const [draft, setDraft] = useState(effective);
  const [echo, setEcho] = useState(effective);
  const lastSent = useRef(effective);
  const timer = useRef<number | null>(null);
  const pending = useRef<string | null>(null);
  // 卸载时要用的是**最新**那个 onCommit,而 effect 的闭包停在挂载那一刻
  const commit = useRef(onCommit);
  commit.current = onCommit;

  // 渲染期同步(React 官方的「根据 prop 调整 state」写法):不走 effect,
  // 免得多渲染一帧 —— 那一帧文本框里显示的还是旧值
  if (effective !== echo) {
    setEcho(effective);
    if (effective !== lastSent.current) setDraft(effective);
  }

  function push(hex: string): void {
    lastSent.current = hex;
    pending.current = hex;
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => {
      timer.current = null;
      pending.current = null;
      commit.current(hex);
    }, 120);
  }

  // 挑完颜色顺手关掉设置页是很常见的动作 —— 不补这一刀,最后那次挑色
  // 会连同定时器一起没掉,表现是「我明明调了,关掉再打开又变回去了」
  useEffect(
    () => () => {
      if (timer.current === null) return;
      window.clearTimeout(timer.current);
      if (pending.current !== null) commit.current(pending.current);
    },
    [],
  );

  const hex = normalizeHex(draft);

  return (
    <div
      className={cn(
        "flex items-center gap-2 rounded-[10px] border px-2.5 py-2",
        hex === null ? "border-danger" : "border-hairline",
      )}
    >
      {/* 原生取色器:macOS 下点开就是系统取色盘(自带吸管),零依赖。
          外面套一层 label 是为了把它做成一颗圆点 —— 各家对
          input[type=color] 自身的样式支持都不一样,盖不干净 */}
      <label
        className="app-no-drag relative size-6 shrink-0 cursor-pointer overflow-hidden rounded-full ring-1 ring-hairline"
        style={{ background: hex ?? effective }}
        title={labels.pickAccent}
      >
        <input
          type="color"
          aria-label={labels.customAccent}
          value={hex ?? effective}
          onChange={(e) => {
            setDraft(e.target.value);
            push(e.target.value);
          }}
          className="absolute inset-0 size-full cursor-pointer opacity-0"
        />
        <Pipette
          size={11}
          className="pointer-events-none absolute inset-0 m-auto text-white mix-blend-difference"
        />
      </label>

      <input
        type="text"
        aria-label={labels.accentHex}
        spellCheck={false}
        value={draft}
        onChange={(e) => {
          setDraft(e.target.value);
          const next = normalizeHex(e.target.value);
          if (next !== null) push(next);
        }}
        // 失焦时把 `#ABC` 这类写法收成规范形式;不合法就退回当前生效的那个,
        // 免得文本框里长期躺着一段红字、而界面用的其实是另一个颜色
        onBlur={() => setDraft(hex ?? effective)}
        className={cn(
          "app-no-drag min-w-0 flex-1 rounded-[6px] bg-tint px-2 py-1 font-mono text-[11.5px]",
          "outline-none focus:ring-1 focus:ring-accent",
          hex === null ? "text-danger" : "text-fg",
        )}
      />
    </div>
  );
}

/** 一套颜色主题:左边一颗强调色圆点,右边名字 + 一句话 */
function ColorCard({
  theme,
  appearance,
  selected,
  onClick,
}: {
  theme: ColorTheme;
  appearance: ResolvedTheme;
  selected: boolean;
  onClick: () => void;
}): ReactNode {
  return (
    <button
      type="button"
      aria-pressed={selected}
      onClick={onClick}
      className={cn(
        "app-no-drag flex items-center gap-2.5 rounded-[10px] border px-2.5 py-2 text-left transition-colors",
        selected ? "border-accent bg-tint" : "border-hairline hover:bg-tint",
      )}
    >
      <span
        className="size-6 shrink-0 rounded-full ring-1 ring-hairline"
        style={{ background: swatchOf(theme, appearance) }}
      />
      <span className="min-w-0 flex-1">
        <span className="block text-[12.5px] text-fg">{theme.name}</span>
        <span className="line-clamp-2 block text-[11px] leading-[1.45] text-fg-muted">
          {theme.description}
        </span>
      </span>
      {selected && <Check size={14} className="shrink-0 text-accent" />}
    </button>
  );
}

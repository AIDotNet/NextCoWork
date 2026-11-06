import { dirname } from "node:path";
/**
 * Skill 扫描 —— 照搬 Claude Code 的目录约定。
 *
 * ```
 * <appData>/skills/<name>/SKILL.md          全局
 * <workspaceRoot>/.next-cowork/skills/<name>/SKILL.md   项目(同名时项目胜出)
 * ```
 *
 * ★ 为什么是**目录**而不是单文件:Skill 常常要带脚本、模板、参考资料
 * (「照着 template.md 写」「跑一下 scripts/check.py」),而子代理定义不带资产,
 * 所以那边是 `agents/<name>.md` 单文件。这个不对称是 CC 的实际形状,照搬。
 *
 * ## 这里的每一条校验都对应一种「静默失败」
 *
 * 加载器的职责是**判定有效性**并把无效的理由说出来。解析器
 * (`frontmatter.ts`)永不 throw、也从不判断有效性 —— 那两件事分在两处,
 * 是因为「这个 YAML 能不能读」和「这条 Skill 能不能用」是两个问题:
 * 一份语法完全正确、却没有 `description` 的 SKILL.md,解析得漂漂亮亮,
 * 但它进了目录之后模型根本不知道什么时候该调它。
 */
import type { Skill, SkillScope } from "../../../shared/domain/skill";
import { SKILL_BODY_MAX, SKILL_NAME_RE } from "../../../shared/domain/skill";
import { LOCAL_SETTINGS_DIRNAME } from "../../../shared/domain/local-settings";
import { fmList, fmString, parseFrontmatter } from "../frontmatter";
import type { KernelFs, WorkspacePaths } from "../host";
import { EnvironmentError } from "../../../shared/domain/environment";
import { PathEscapeError, resolveInWorkspace } from "../tool/path-guard";
import { clampWithEllipsis, stripControlChars } from "../text";
import { BUNDLED_MARKER } from "./bundled";

/**
 * 描述的字符上限。★ 和 CC 对齐,也和「目录里每条只占一行」这个设计一致 ——
 * 描述是**唯一**进系统提示词的部分,它长成什么样,提示词就贵成什么样。
 */
export const SKILL_DESCRIPTION_MAX = 1024;

/** SKILL.md 读进内存的字节上限。正文本身还会再被 `SKILL_BODY_MAX` 截一次。 */
const SKILL_FILE_MAX_BYTES = 256 * 1024;

/**
 * 远端资产判定里**不算资产**的文件:正文本身,加上安装器自己写进包目录的元数据
 * (包清单、内置哨兵)。目录里只有这些的 Skill 是纯文本,SSH 会话里照样能用。
 */
const NON_ASSET_FILES: ReadonlySet<string> = new Set(["SKILL.md", ".nextcowork-package.json", BUNDLED_MARKER]);

/** 一次扫描最多认多少条。防一个被塞了几千个目录的 skills/ 把启动拖住。 */
const MAX_SKILLS = 200;

/** 目录名 —— 和 CC 一致 */
export const SKILLS_DIR = "skills";
/**
 * 项目级资源所在的那层目录。
 *
 * ★ **这里不再写字面量。** 同一个 `.next-cowork` 曾经被 skill / command /
 * agent / mode 四个扫描器各写过一遍,而 `settings.local.json` 那边还有第五份 ——
 * 改一处漏四处的表现是「换了目录名之后只剩某一类资源还认得出来」,
 * 而每一类都是独立失效的,没有任何一处会报错。唯一出处在
 * `shared/domain/local-settings.ts` 的 `LOCAL_SETTINGS_DIRNAME`。
 */
export const PROJECT_SKILLS_PREFIX = LOCAL_SETTINGS_DIRNAME;

export type { SkillScope };

export interface SkillDiagnostic {
  /** 出问题的那个路径,给用户看的 */
  path: string;
  message: string;
}

export interface SkillScanResult {
  skills: Skill[];
  diagnostics: SkillDiagnostic[];
}

/**
 * 一个已启用插件贡献的**一条** skill。
 *
 * ★ 和 `globalRoot` / `projectRoot` 不是同一种东西:那两个是**含若干条的根**
 * (扫描器 readDir 之后逐个子目录看),这里 `dir` 直接就是那一条 skill 的目录
 * (`<插件包>/skills/<name>`)。
 *
 * 为什么不把 `<插件包>/skills` 整个当成第三个根、让扫描器自己 readDir:那样
 * 插件的 `contributes.skills` 就形同虚设 —— 包里多放一个目录就自动生效,
 * 而清单上看不出来、上架审核也审不到。**贡献点必须是清单里写了的那些**,
 * 这条不变式比少写十行代码重要。
 */
export interface PluginSkillRoot {
  /** `publisher.name`。进诊断文本,也进 `Skill.source.pluginId`。 */
  pluginId: string;
  /** 这一条 skill 的**绝对**目录。由插件管理器用包目录拼好,扫描器不做拼接。 */
  dir: string;
}

export interface SkillScanInput {
  fs: KernelFs;
  projectFs?: KernelFs;
  projectPath?: WorkspacePaths;
  /** `<appData>/skills`。空串 = 跳过全局这一层。 */
  globalRoot: string;
  /** `<workspaceRoot>/.nextcowork/skills`。空串 = 没有工作区。 */
  projectRoot: string;
  /**
   * 已启用插件贡献的那几条。**留空 = 一条都没有**,不是「去查一下」。
   *
   * ★ 调用方每次都要重新取(`refreshSkills` 每次发送前跑一遍),因为这份清单
   * 随插件的启用状态变。缓存它的症状是:用户禁用了插件,而模型下一轮仍然看得见
   * 它带来的 skill —— 且插件页上明明写着它是关的。
   */
  pluginRoots?: readonly PluginSkillRoot[];
}

/**
 * 「现在有哪些插件 Skill 目录」的**提供者**。
 *
 * ## 为什么是注册,不是直接 import
 *
 * 唯一知道答案的是 `plugin/manager.ts` 里那个实例,而它的持有者是
 * `ipc/plugins.ts`。扫描器(内核)去 import IPC 层就成了环:
 * `ipc/plugins` → `plugin/manager` → `kernel/skill/load` → `ipc/plugins`。
 * 环在 ESM 里不报错,只是让其中一方在初始化时拿到 `undefined` ——
 * 症状是「某些启动路径下插件 skill 一条都不出现」,且没有任何报错。
 *
 * 同 `protocol.ts` 的 `setPluginRuntimeDir`、`change-recorder.ts` 的
 * `setFileChangeListener`,本仓库里这是既有答案,不是新造的第二套。
 *
 * ★ 默认返回空数组而不是 throw:插件系统可能压根没起来(无头测试、
 * 插件被整体关掉)。那种情况下「没有插件 skill」是正确答案,不是错误。
 */
let pluginSkillRootsProvider: () => readonly PluginSkillRoot[] = () => [];

export function setPluginSkillRootsProvider(provider: (() => readonly PluginSkillRoot[]) | null): void {
  pluginSkillRootsProvider = provider ?? (() => []);
}

/**
 * 调用方拿这个去填 `SkillScanInput.pluginRoots`。
 *
 * ★ **每次扫描都重新调**,不要把结果存起来。这份清单随插件启用状态变,
 * 存下来的症状是:用户禁用了插件,而模型下一轮还看得见它带来的 skill。
 */
export function currentPluginSkillRoots(): readonly PluginSkillRoot[] {
  try {
    return pluginSkillRootsProvider();
  } catch {
    // 插件管理器炸了不该让**所有** skill 都扫不出来 —— 同文件头那条「永不 throw」
    return [];
  }
}

/**
 * 扫两层目录,产出一张 Skill 表。
 *
 * ★ **永不 throw。**目录不存在、权限不足、某个 SKILL.md 是坏的 —— 全部变成
 * `diagnostics` 里的一行。一次扫描因为某台机器上一个坏文件而整体失败的话,
 * 用户看到的是「所有 Skill 都不见了」,而真正坏掉的只有一条。
 */
export async function scanSkills(
  input: SkillScanInput,
): Promise<SkillScanResult> {
  const diagnostics: SkillDiagnostic[] = [];
  const byName = new Map<string, Skill>();

  /*
    ★ 插件层**最先进**,因为后进的覆盖先进的 —— 也就是插件的优先级最低。

    需求:插件带来的是一个合理默认,用户自己写的那条永远说了算。反过来的话,
    用户在 `.next-cowork/skills/` 里放一条同名的覆盖规则会毫无反应,
    而界面上两条都在、没有任何地方解释谁生效。
  */
  await scanPluginRoots(input, byName, diagnostics);

  // ★ 顺序即优先级:全局先进,项目后进覆盖同名。反过来写,项目里那条
  //   「这个仓库要用我们自己的提交规范」的 Skill 就永远压不过全局那条。
  for (const scope of ["global", "project"] as const) {
    const root = scope === "global" ? input.globalRoot : input.projectRoot;
    if (root === "") continue;
    await scanOneRoot(scope === "project" ? input.projectFs ?? input.fs : input.fs, root, scope, byName, diagnostics,
      scope === "project" ? input.projectPath : undefined);
  }

  if (input.projectFs) for (const skill of byName.values()) {
    /*
      ★ 这一条现在也管 `plugin` 作用域,不只是 `global`。

      原先写的是「跳过非 global」,理由是项目 skill 的资产就在服务器上、拿得到。
      插件 skill 和全局 skill 处境相同 —— 文件在**客户端**的插件包里,
      而这一轮的命令跑在 SSH 服务器上。不管它的话,模型会照着 skill 正文去跑
      一个只存在于用户笔记本上的 `scripts/check.py`,表现为一条找不到文件的
      命令失败,而失败信息里那个路径看上去完全合理。
    */
    if (skill.scope === "project") continue;
    try {
      const contents = await input.fs.readDir(dirname(skill.source.path));
      // ★ 内置哨兵(`BUNDLED_MARKER`)和包清单一样是安装器自己写的元数据,不是正文会引用的资产
      if (contents.some((entry) => !NON_ASSET_FILES.has(entry.name))) skill.unavailableReason = "client-assets";
    } catch { skill.unavailableReason = "client-assets"; }
  }
  return { skills: [...byName.values()], diagnostics };
}

/**
 * 逐条读插件贡献的 skill 目录。
 *
 * ★ 走的是**本地 fs**(`input.fs`),不是 `projectFs`:插件包永远装在客户端的
 * `<userData>/plugins/` 下。用远端 fs 去读的话,SSH 工作区里每一条插件 skill
 * 都会变成一条「目录不存在」的诊断。
 *
 * ★ 两个插件贡献同名 skill 时**先来的赢,并且出诊断**。不出诊断的话,
 * 后装的那个插件的 skill 会凭空消失,而两个插件页上都显示得好好的。
 * 「先来」按调用方给的顺序(插件管理器按 pluginId 排过),所以结果是确定的 ——
 * 换成「后来居上」的话,同一台机器上重启一次就可能换一个赢家。
 */
async function scanPluginRoots(
  input: SkillScanInput,
  out: Map<string, Skill>,
  diagnostics: SkillDiagnostic[],
): Promise<void> {
  const roots = input.pluginRoots ?? [];
  /** 名字 → 贡献它的插件。只用来认出「第二个人也贡献了这个名字」。 */
  const owners = new Map<string, string>();
  for (const root of roots) {
    if (out.size >= MAX_SKILLS) {
      diagnostics.push({ path: root.dir, message: `Skill 数量超过 ${String(MAX_SKILLS)} 条,其余未加载` });
      return;
    }
    const dirName = root.dir.split(/[\\/]/).filter((s) => s !== "").pop() ?? "";
    let loaded: Skill | null;
    try {
      /*
        ★ 和另外两层一样走 `resolveInWorkspace`,而不是直接拼 `${dir}/SKILL.md`。

        插件包里的 `SKILL.md` 可以是一条软链。ZIP 安装那条路已经拒了符号链接,
        但**目录安装**(开发时的「装本地目录」)没有这道门 —— 一条指向
        `~/.ssh/id_rsa` 的软链会被原样读进 skill 正文,而正文是要进模型上下文的。
      */
      const file = resolveInWorkspace(root.dir, "SKILL.md");
      loaded = await loadOne(input.fs, file, dirName, "plugin", diagnostics, undefined, root.dir);
    } catch (err) {
      if (err instanceof EnvironmentError) throw err;
      if (err instanceof PathEscapeError) {
        diagnostics.push({ path: `${root.dir}/SKILL.md`, message: `插件 ${root.pluginId} 的这条 Skill 指向了包外,已跳过` });
        continue;
      }
      diagnostics.push({ path: `${root.dir}/SKILL.md`, message: msg(err) });
      continue;
    }
    if (loaded === null) continue;

    const previous = owners.get(loaded.name);
    if (previous !== undefined) {
      diagnostics.push({
        path: `${root.dir}/SKILL.md`,
        message: `插件 ${root.pluginId} 和 ${previous} 都提供了名为 "${loaded.name}" 的 Skill,这一条已跳过。禁用其中一个,或让作者改名。`,
      });
      continue;
    }
    owners.set(loaded.name, root.pluginId);
    loaded.source.kind = "plugin";
    loaded.source.pluginId = root.pluginId;
    out.set(loaded.name, loaded);
  }
}

async function scanOneRoot(
  fs: KernelFs,
  root: string,
  scope: SkillScope,
  out: Map<string, Skill>,
  diagnostics: SkillDiagnostic[],
  path?: WorkspacePaths,
): Promise<void> {
  let entries: Array<{ name: string; isDir: boolean }>;
  try {
    if (!(await fs.exists(root))) return; // 没有 skills 目录是常态,不是错误
    entries = await fs.readDir(root);
  } catch (err) {
    if (err instanceof EnvironmentError) throw err;
    diagnostics.push({ path: root, message: `读不了这个目录:${msg(err)}` });
    return;
  }

  for (const e of entries) {
    if (!e.isDir) continue;
    if (out.size >= MAX_SKILLS) {
      diagnostics.push({
        path: root,
        message: `Skill 数量超过 ${String(MAX_SKILLS)} 条,其余未加载`,
      });
      return;
    }
    /*
      ★ 目录名先过 `SKILL_NAME_RE`。这不只是「规范一下命名」:名字会进
      系统提示词、会被模型原样当成 `skill` 工具的入参,而一个叫
      `../../etc` 或者带换行的目录名两样都能搞坏。
    */
    if (!SKILL_NAME_RE.test(e.name)) {
      diagnostics.push({
        path: `${root}/${e.name}`,
        message: `目录名不合法,已跳过`,
      });
      continue;
    }

    let dir: string;
    try {
      /*
        ★ 这一步挡的是软链逃逸:`~/.nextcowork/skills/evil -> /` 之后,
        「扫描 skills 目录」就变成了「扫描整个磁盘」。`resolveInWorkspace`
        会 realpath 之后做包含判断,所以逃出去的目录在这里就死了。
      */
      dir = path ? await path.resolveWithin(root, e.name) : resolveInWorkspace(root, e.name);
    } catch (err) {
      if (err instanceof EnvironmentError) throw err;
      if (err instanceof PathEscapeError) {
        diagnostics.push({
          path: `${root}/${e.name}`,
          message: "这个目录指向了 skills 之外,已跳过",
        });
        continue;
      }
      diagnostics.push({ path: `${root}/${e.name}`, message: msg(err) });
      continue;
    }

    try {
      const file = path ? await path.resolveWithin(dir, "SKILL.md") : resolveInWorkspace(dir, "SKILL.md");
      const loaded = await loadOne(fs, file, e.name, scope, diagnostics, path, dir);
      if (loaded !== null) out.set(loaded.name, loaded);
    } catch (err) {
      if (err instanceof EnvironmentError) throw err;
      diagnostics.push({ path: `${dir}/SKILL.md`, message: msg(err) });
    }
  }
}

async function loadOne(
  fs: KernelFs,
  file: string,
  dirName: string,
  scope: SkillScope,
  diagnostics: SkillDiagnostic[],
  path?: WorkspacePaths,
  directory?: string,
): Promise<Skill | null> {
  let raw: string;
  try {
    if (!(await fs.exists(file))) {
      diagnostics.push({
        path: file,
        message: "这个目录里没有 SKILL.md,已跳过",
      });
      return null;
    }
    const bytes = await fs.readFileBytes(file, SKILL_FILE_MAX_BYTES);
    raw = new TextDecoder("utf-8", { fatal: false }).decode(bytes);
  } catch (err) {
    if (err instanceof EnvironmentError) throw err;
    diagnostics.push({ path: file, message: `读不了这个文件:${msg(err)}` });
    return null;
  }

  const fm = parseFrontmatter(raw);
  for (const s of fm.skipped) diagnostics.push({ path: file, message: s });

  /*
    ★ frontmatter 里的 name 胜过目录名,但**不一致时要说一声**。
    不说的话,用户把目录改名之后会发现 `/old-name` 还在,而目录里明明写着新名字。
  */
  const declared = fmString(fm, "name");
  const name = declared ?? dirName;
  if (declared !== undefined && declared !== dirName) {
    diagnostics.push({
      path: file,
      message: `frontmatter 里的 name "${declared}" 和目录名 "${dirName}" 不一致,以 name 为准`,
    });
  }
  if (!SKILL_NAME_RE.test(name)) {
    diagnostics.push({
      path: file,
      message: `name "${name}" 不合法,这条 Skill 已作废`,
    });
    return null;
  }

  /*
    ★ 缺 description 就整条作废,而不是拿名字凑一个。
    渐进披露里,描述是模型**唯一**的判断依据 —— 目录里只有名字的话,
    模型要么永远不调它,要么见什么都调它。两种都比「这条没装上」更糟。
  */
  const description = fmString(fm, "description");
  if (description === undefined) {
    diagnostics.push({
      path: file,
      message:
        "frontmatter 里缺 description —— 没有描述,模型无从判断什么时候该用它,这条 Skill 已作废",
    });
    return null;
  }

  const body = clampWithEllipsis(
    stripControlChars(fm.body).trim(),
    SKILL_BODY_MAX,
  );
  if (body === "") {
    diagnostics.push({ path: file, message: "正文是空的,这条 Skill 已作废" });
    return null;
  }

  let packageMeta: {
    sourceKind?: Skill["source"]["kind"];
    version?: string;
    sha256?: string;
  } = {};
  try {
    const metaPath = path && directory ? await path.resolveWithin(directory, ".nextcowork-package.json")
      : `${dirName === "" ? file : file.slice(0, Math.max(0, file.length - "SKILL.md".length))}.nextcowork-package.json`;
    const rawMeta = new TextDecoder().decode(
      await fs.readFileBytes(metaPath, 4096),
    );
    const parsed = JSON.parse(rawMeta) as typeof packageMeta;
    if (parsed.sourceKind === "zip" && typeof parsed.sha256 === "string")
      packageMeta = parsed;
  } catch (error) {
    if (error instanceof EnvironmentError) throw error;
    /* folder installs and older packages have no metadata sidecar */
  }

  return {
    // ★ id 就是 name:同名时项目覆盖全局,所以两条永远不会共存,
    //   再造一个 `<scope>:<name>` 的 id 只会让工作区的启用清单在
    //   「项目里新增了一条同名 Skill」之后突然失配。
    id: name,
    name,
    description: clampWithEllipsis(description, SKILL_DESCRIPTION_MAX),
    category: fmString(fm, "category") ?? "未分类",
    source: {
      kind: packageMeta.sourceKind ?? "folder",
      path: file,
      ...(packageMeta.version ? { version: packageMeta.version } : {}),
      ...(packageMeta.sha256 ? { sha256: packageMeta.sha256 } : {}),
    },
    scope,
    globalEnabled: true,
    frontmatter: {
      ...(fmString(fm, "model") !== undefined
        ? { model: fmString(fm, "model") }
        : {}),
      // `allowed-tools` 是 CC 的写法;`allowedTools` 是有人会顺手写的那个变体
      ...(allowedTools(fm) !== undefined
        ? { allowedTools: allowedTools(fm) }
        : {}),
    },
    body,
  };
}

function allowedTools(
  fm: ReturnType<typeof parseFrontmatter>,
): string[] | undefined {
  return fmList(fm, "allowed-tools") ?? fmList(fm, "allowedTools");
}

function msg(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

export const SKILL_LIMITS = {
  SKILL_DESCRIPTION_MAX,
  SKILL_FILE_MAX_BYTES,
  MAX_SKILLS,
} as const;

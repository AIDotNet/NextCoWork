import { createReadStream } from "node:fs";
import { promises as fs } from "node:fs";
import { basename, dirname, join, resolve, sep } from "node:path";
import { createHash } from "node:crypto";
import unzipper from "unzipper";
import {
  SKILL_BODY_MAX,
  SKILL_NAME_RE,
  type SkillInstallScope,
} from "../../../shared/domain/skill";
import { SKILL_DESCRIPTION_MAX } from "./load";
import { parseFrontmatter, fmString } from "../frontmatter";

const MAX_ZIP = 20 * 1024 * 1024;
const MAX_EXPANDED = 50 * 1024 * 1024;
const MAX_ENTRIES = 1000;
const MAX_DEPTH = 12;

/**
 * ★ 导出给「从其他 AI 应用导入技能包」复用 —— 那条路径不经过 zip,但**必须**
 * 受同一组上限管着。抄一份数值出去的代价很具体:两处一旦分叉,预览会说
 * 「能装」而安装当场拒绝,而用户看不出哪一边说了实话。
 */
export const PACKAGE_LIMITS = {
  MAX_EXPANDED,
  MAX_ENTRIES,
  MAX_DEPTH,
} as const;

export interface InstalledPackage {
  name: string;
  version?: string;
  sha256: string;
  target: string;
}

function safeEntryPath(path: string): string | null {
  const name = path.replace(/\/$/, "").replace(/^\.\//, "");
  return name !== "" &&
    !name.includes("\\") &&
    !name.startsWith("/") &&
    !/^[A-Za-z]:/.test(name) &&
    name
      .split("/")
      .every((part) => part !== "" && part !== "." && part !== "..") &&
    name.split("/").length <= MAX_DEPTH
    ? name
    : null;
}

async function readEntry(entry: unzipper.File, limit: number): Promise<Buffer> {
  const stream = entry.stream();
  const chunks: Buffer[] = [];
  let total = 0;
  try {
    for await (const part of stream) {
      const chunk = Buffer.isBuffer(part)
        ? part
        : Buffer.from(part as Uint8Array);
      total += chunk.length;
      if (total > limit) throw new Error("ZIP 解压后体积过大");
      chunks.push(chunk);
    }
    if (total !== entry.uncompressedSize) throw new Error("ZIP 条目大小不一致");
    return Buffer.concat(chunks, total);
  } finally {
    stream.destroy();
  }
}

export async function installSkillZip(
  zipPath: string,
  root: string,
  _scope: SkillInstallScope,
  expectedSha256?: string,
): Promise<InstalledPackage> {
  const stat = await fs.stat(zipPath);
  if (!stat.isFile() || stat.size > MAX_ZIP) throw new Error("ZIP 文件过大");
  const hash = createHash("sha256");
  await new Promise<void>((resolvePromise, reject) => {
    const stream = createReadStream(zipPath);
    stream.on("data", (chunk: string | Buffer) => hash.update(chunk));
    stream.on("error", reject);
    stream.on("end", () => resolvePromise());
  });
  const sha256 = hash.digest("hex");
  if (expectedSha256 && expectedSha256.toLowerCase() !== sha256)
    throw new Error("ZIP 摘要校验失败");
  const directory = await unzipper.Open.file(zipPath);
  if (directory.files.length === 0 || directory.files.length > MAX_ENTRIES)
    throw new Error("ZIP 文件数量无效");
  let expanded = 0;
  const entryPaths = new Map<unzipper.File, string>();
  const seen = new Set<string>();
  for (const entry of directory.files) {
    const name = safeEntryPath(entry.path);
    if (name === null) throw new Error("ZIP 包含非法路径");
    entryPaths.set(entry, name);
    const normalized = name.toLocaleLowerCase();
    if (seen.has(normalized)) throw new Error("ZIP 包含重复路径");
    seen.add(normalized);
    if (!Number.isFinite(entry.uncompressedSize) || entry.uncompressedSize < 0)
      throw new Error("ZIP 条目大小无效");
    // Unix mode 0120000 marks symbolic links. Never materialize links from an
    // untrusted archive, even when their target appears to stay under root.
    const unixMode = (entry.externalFileAttributes >>> 16) & 0xffff;
    if ((unixMode & 0xf000) === 0xa000)
      throw new Error("ZIP 不允许包含符号链接");
    expanded += entry.uncompressedSize;
    if (expanded > MAX_EXPANDED) throw new Error("ZIP 解压后体积过大");
  }
  const paths = [...entryPaths.values()];
  // ★ null means a flat archive with SKILL.md at its root. Its install directory
  // comes from validated frontmatter, never from a download filename users can rename.
  let packageRoot: string | null = null;
  if (!paths.includes("SKILL.md")) {
    const topLevels = new Set(
      paths.map((name) => name.split("/")[0] ?? ""),
    );
    if (topLevels.size !== 1)
      throw new Error("ZIP 必须只有一个顶层目录");
    packageRoot = topLevels.values().next().value ?? null;
    if (packageRoot === null || !SKILL_NAME_RE.test(packageRoot))
      throw new Error("ZIP 顶层目录或 SKILL.md 无效");
  }
  const skillPath =
    packageRoot === null ? "SKILL.md" : `${packageRoot}/SKILL.md`;
  const skillEntry =
    directory.files.find((entry) => entryPaths.get(entry) === skillPath) ?? null;
  if (skillEntry === null) throw new Error("ZIP 顶层目录或 SKILL.md 无效");
  const metadataPath =
    packageRoot === null
      ? ".nextcowork-package.json"
      : `${packageRoot}/.nextcowork-package.json`;
  if (paths.includes(metadataPath))
    throw new Error("ZIP 使用了保留的元数据文件名");
  const raw = (await readEntry(skillEntry, 256 * 1024)).toString("utf8");
  if (Buffer.byteLength(raw) > 256 * 1024) throw new Error("SKILL.md 文件过大");
  const fm = parseFrontmatter(raw);
  if (fm.skipped.length > 0) throw new Error("SKILL.md frontmatter 无法识别");
  const name = fmString(fm, "name");
  const description = fmString(fm, "description");
  if (
    !name ||
    (packageRoot !== null && name !== packageRoot) ||
    !SKILL_NAME_RE.test(name) ||
    !description ||
    description.length > SKILL_DESCRIPTION_MAX ||
    fm.body.trim() === "" ||
    fm.body.length > SKILL_BODY_MAX
  )
    throw new Error("SKILL.md 元数据无效");
  const target = resolve(root, name);
  // 版本号三个来源,由强到弱:顶层 `version`、`metadata:` 下的 `version`
  // (市场上的包多半写在这儿)、最后才是文件名 —— 文件名是用户能随手改的。
  const version =
    fmString(fm, "version") ??
    fmString(fm, "metadata.version") ??
    basename(zipPath).match(/v?(\d+\.\d+\.\d+)/)?.[1];
  const rootResolved = resolve(root);
  if (!(target === rootResolved || target.startsWith(rootResolved + sep)))
    throw new Error("安装路径无效");
  const staging = `${target}.installing-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const backup = `${target}.backup-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  let backedUp = false;
  await fs.mkdir(dirname(staging), { recursive: true });
  try {
    let actualExpanded = 0;
    for (const entry of directory.files) {
      const entryPath = entryPaths.get(entry);
      if (entryPath === undefined) throw new Error("ZIP 包含非法路径");
      const child =
        packageRoot === null
          ? entryPath
          : entryPath.split("/").slice(1).join("/");
      if (child === "") continue;
      const destination = join(staging, child);
      if (entry.path.endsWith("/")) {
        await fs.mkdir(destination, { recursive: true });
        continue;
      }
      await fs.mkdir(dirname(destination), { recursive: true });
      const bytes = await readEntry(entry, MAX_EXPANDED - actualExpanded);
      actualExpanded += bytes.length;
      await fs.writeFile(destination, bytes, { flag: "wx" });
    }
    await fs.writeFile(
      join(staging, ".nextcowork-package.json"),
      JSON.stringify({
        sourceKind: "zip",
        sha256,
        ...(version ? { version } : {}),
      }),
      { flag: "wx" },
    );
    try {
      await fs.rename(target, backup);
      backedUp = true;
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code !== "ENOENT") throw error;
    }
    await fs.rename(staging, target);
    if (backedUp)
      await fs
        .rm(backup, { recursive: true, force: true })
        .catch(() => undefined);
  } catch (error) {
    await fs
      .rm(staging, { recursive: true, force: true })
      .catch(() => undefined);
    // If activation failed after moving the old package aside, put it back.
    if (backedUp) await fs.rename(backup, target).catch(() => undefined);
    throw error;
  }
  return { name, sha256, target, version };
}

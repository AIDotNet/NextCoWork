import { execFileSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { installSkillZip } from "../install";
import { nodeHost } from "../../host";
import { scanSkills } from "../load";

const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0))
    rmSync(root, { recursive: true, force: true });
});

function archive(
  content: string,
  name = "demo",
): { root: string; zip: string } {
  const root = mkdtempSync(join(tmpdir(), "nextcowork-install-"));
  roots.push(root);
  mkdirSync(join(root, name), { recursive: true });
  writeFileSync(join(root, name, "SKILL.md"), content);
  const zip = join(root, "skill.zip");
  execFileSync("zip", ["-qr", zip, name], { cwd: root });
  return { root, zip };
}

function dotPrefixedArchive(): { root: string; zip: string } {
  const root = mkdtempSync(join(tmpdir(), "nextcowork-dot-install-"));
  roots.push(root);
  mkdirSync(join(root, "x", "a"), { recursive: true });
  writeFileSync(
    join(root, "x", "a", "SKILL.md"),
    "---\nname: a\ndescription: dot prefix\n---\ndot-prefixed body\n",
  );
  const zip = join(root, "skill.zip");
  execFileSync("zip", ["-q", zip, "x/a/SKILL.md"], { cwd: root });
  const bytes = readFileSync(zip).toString("latin1");
  const prefixed = bytes.replaceAll("x/a/SKILL.md", "./a/SKILL.md");
  if (prefixed === bytes) throw new Error("ZIP fixture path was not found");
  writeFileSync(zip, Buffer.from(prefixed, "latin1"));
  return { root, zip };
}

function flatArchive(): { root: string; zip: string } {
  const root = mkdtempSync(join(tmpdir(), "nextcowork-flat-install-"));
  roots.push(root);
  mkdirSync(join(root, "knowledge"), { recursive: true });
  writeFileSync(
    join(root, "SKILL.md"),
    "---\nname: flat-demo\ndescription: flat package\n---\nflat body\n",
  );
  writeFileSync(join(root, "knowledge", "guide.md"), "guide");
  writeFileSync(join(root, "RELEASE.md"), "release");
  const zip = join(root, "renamed-download.zip");
  execFileSync(
    "zip",
    ["-qr", zip, "SKILL.md", "knowledge", "RELEASE.md"],
    { cwd: root },
  );
  return { root, zip };
}

describe("installSkillZip", () => {
  it("strips the archive top-level directory and atomically replaces an install", async () => {
    const first = archive("---\nname: demo\ndescription: old\n---\nold body\n");
    const installRoot = join(first.root, "installed");
    const result = await installSkillZip(first.zip, installRoot, "global");
    expect(result.name).toBe("demo");
    expect(
      readFileSync(join(installRoot, "demo", "SKILL.md"), "utf8"),
    ).toContain("old body");
    expect(existsSync(join(installRoot, "demo", "demo", "SKILL.md"))).toBe(
      false,
    );
    const scanned = await scanSkills({
      fs: nodeHost().fs,
      globalRoot: installRoot,
      projectRoot: "",
    });
    expect(scanned.skills[0]?.source.kind).toBe("zip");
    expect(scanned.skills[0]?.source.sha256).toBe(result.sha256);
  });

  it("installs archives whose entries start with ./", async () => {
    const { root, zip } = dotPrefixedArchive();
    const installRoot = join(root, "installed");

    const result = await installSkillZip(zip, installRoot, "global");

    expect(result.name).toBe("a");
    expect(readFileSync(join(installRoot, "a", "SKILL.md"), "utf8")).toContain(
      "dot-prefixed body",
    );
  });

  it("installs a SKILL.md whose description is a block scalar", async () => {
    // Market packages write multi-line descriptions as `description: |`. The
    // installer rejects any frontmatter it cannot read whole, so a parser that
    // skipped block scalars failed the install with "frontmatter 无法识别".
    const { root, zip } = archive(
      "---\nname: demo\ndescription: |\n  第一行\n  第二行\n---\nbody\n",
    );
    const installRoot = join(root, "installed");

    const result = await installSkillZip(zip, installRoot, "global");

    expect(result.name).toBe("demo");
    const scanned = await scanSkills({
      fs: nodeHost().fs,
      globalRoot: installRoot,
      projectRoot: "",
    });
    expect(scanned.skills[0]?.description).toBe("第一行\n第二行");
    expect(scanned.diagnostics).toHaveLength(0);
  });

  it("installs a market SKILL.md with a metadata block", async () => {
    // Verbatim shape of a published skill: a nested `metadata:` map the app
    // reads nothing from. It used to fail the whole install on its own.
    const { root, zip } = archive(
      [
        "---",
        "name: programming-tutor",
        'description: "全能 AI 编程导师"',
        "license: MIT",
        "metadata:",
        '  author: "Samuel Kahessay"',
        '  version: "1.0.1"',
        '  category: "education"',
        "---",
        "body",
        "",
      ].join("\n"),
      "programming-tutor",
    );
    const installRoot = join(root, "installed");

    const result = await installSkillZip(zip, installRoot, "global");

    expect(result.name).toBe("programming-tutor");
    expect(result.version).toBe("1.0.1");
    const scanned = await scanSkills({
      fs: nodeHost().fs,
      globalRoot: installRoot,
      projectRoot: "",
    });
    expect(scanned.skills[0]?.name).toBe("programming-tutor");
    expect(scanned.diagnostics).toHaveLength(0);
  });

  it("uses frontmatter name when SKILL.md is at the archive root", async () => {
    const { root, zip } = flatArchive();
    const installRoot = join(root, "installed");

    const result = await installSkillZip(zip, installRoot, "global");

    expect(result.name).toBe("flat-demo");
    expect(
      readFileSync(join(installRoot, "flat-demo", "SKILL.md"), "utf8"),
    ).toContain("flat body");
    expect(
      readFileSync(
        join(installRoot, "flat-demo", "knowledge", "guide.md"),
        "utf8",
      ),
    ).toBe("guide");
    expect(
      readFileSync(join(installRoot, "flat-demo", "RELEASE.md"), "utf8"),
    ).toBe("release");
    const scanned = await scanSkills({
      fs: nodeHost().fs,
      globalRoot: installRoot,
      projectRoot: "",
    });
    expect(scanned.skills[0]?.name).toBe("flat-demo");
    expect(scanned.skills[0]?.source.kind).toBe("zip");
  });

  it("still rejects wrapped archives with multiple top-level directories", async () => {
    const root = mkdtempSync(join(tmpdir(), "nextcowork-multi-install-"));
    roots.push(root);
    mkdirSync(join(root, "demo"), { recursive: true });
    mkdirSync(join(root, "extra"), { recursive: true });
    writeFileSync(
      join(root, "demo", "SKILL.md"),
      "---\nname: demo\ndescription: d\n---\nbody\n",
    );
    writeFileSync(join(root, "extra", "file.md"), "extra");
    const zip = join(root, "skill.zip");
    execFileSync("zip", ["-qr", zip, "demo", "extra"], { cwd: root });

    await expect(
      installSkillZip(zip, join(root, "installed"), "global"),
    ).rejects.toThrow(/顶层目录/);
  });

  it("rejects symbolic links in archives", async () => {
    const root = mkdtempSync(join(tmpdir(), "nextcowork-link-"));
    roots.push(root);
    mkdirSync(join(root, "demo"), { recursive: true });
    writeFileSync(
      join(root, "demo", "SKILL.md"),
      "---\ndescription: d\n---\nbody\n",
    );
    symlinkSync("/tmp", join(root, "demo", "escape"));
    const zip = join(root, "link.zip");
    execFileSync("zip", ["-qry", "-y", zip, "demo"], { cwd: root });
    await expect(
      installSkillZip(zip, join(root, "installed"), "global"),
    ).rejects.toThrow(/符号链接/);
  });

  it("rejects unsupported frontmatter syntax", async () => {
    // Two levels of nesting stay unreadable, and name/description are both
    // present here: the install must still fail, on the skipped line alone.
    const { root, zip } = archive(
      "---\nname: demo\ndescription: d\na:\n  b:\n    c: 1\n---\nbody\n",
    );
    await expect(
      installSkillZip(zip, join(root, "installed"), "global"),
    ).rejects.toThrow(/frontmatter/);
  });
});

describe("nextcowork-remotion package", () => {
  const source = new URL(
    "../../../../../examples/skills/nextcowork-remotion/",
    import.meta.url,
  );
  const content = (): string =>
    new TextDecoder("utf-8", { fatal: true }).decode(
      readFileSync(new URL("SKILL.md", source)),
    );

  it("installs as a self-contained global Skill, including in a remote snapshot", async () => {
    expect(readdirSync(source)).toEqual(["SKILL.md"]);
    const { root, zip } = archive(content(), "nextcowork-remotion");
    const installRoot = join(root, "installed");
    const result = await installSkillZip(zip, installRoot, "global");
    const fs = nodeHost().fs;
    const scanned = await scanSkills({
      fs,
      projectFs: fs,
      globalRoot: installRoot,
      projectRoot: "",
    });

    expect(result).toMatchObject({
      name: "nextcowork-remotion",
      version: "1.0.0",
    });
    expect(scanned.diagnostics).toEqual([]);
    expect(scanned.skills).toHaveLength(1);
    expect(scanned.skills[0]).toMatchObject({
      name: "nextcowork-remotion",
      category: "内容创作",
      scope: "global",
      globalEnabled: true,
      frontmatter: {},
      source: { kind: "zip", version: "1.0.0", sha256: result.sha256 },
    });
    expect(scanned.skills[0]?.unavailableReason).toBeUndefined();
    expect(scanned.skills[0]?.frontmatter).toEqual({});
    expect(readFileSync(join(result.target, "SKILL.md"), "utf8")).toBe(
      content(),
    );
  });

  it("is discoverable as a project Skill without a global installation", async () => {
    const { root, zip } = archive(content(), "nextcowork-remotion");
    const projectRoot = join(root, "project", ".next-cowork", "skills");
    await installSkillZip(zip, projectRoot, "project");
    const scanned = await scanSkills({
      fs: nodeHost().fs,
      globalRoot: "",
      projectRoot,
    });

    expect(scanned.diagnostics).toEqual([]);
    expect(scanned.skills).toHaveLength(1);
    expect(scanned.skills[0]).toMatchObject({
      id: "nextcowork-remotion",
      scope: "project",
    });
    expect(scanned.skills[0]?.unavailableReason).toBeUndefined();
  });
});

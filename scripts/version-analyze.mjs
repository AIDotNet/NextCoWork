#!/usr/bin/env node

import { execSync } from 'child_process';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '..');
const pkgPath = path.join(projectRoot, 'package.json');
const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf-8'));

const commitTypes = {
  'feat': '新增',
  'fix': '修复',
  'perf': '性能',
  'refactor': '重构',
  'test': '测试',
  'docs': '文档',
  'chore': '杂项',
  'style': '风格',
  'ci': 'CI'
};

/**
 * 上一次发版的 tag。
 * ★ 必须取**当前分支可达**的那个:`git tag -l --sort=-creatordate` 按创建时间排,
 *   会挑到别的分支上更新的 tag,范围于是变成空集合(或把上一版的提交又数一遍),
 *   生成的日志一路漏到没人发现。`git describe` 只看祖先,顺序也是拓扑的。
 */
function lastReachableTag() {
  try {
    return execSync('git describe --tags --abbrev=0 --match "v[0-9]*"', { encoding: 'utf-8' }).trim();
  } catch {
    // 仓库里还没有可达的 v* tag(首个版本),交给调用方退化到 HEAD~10。
    return '';
  }
}

function parseCommits() {
  try {
    const lastTag = lastReachableTag();

    // 获取提交历史
    const range = lastTag ? `${lastTag}..HEAD` : 'HEAD~10..HEAD';
    const commits = execSync(`git log ${range} --format=%B%n---COMMIT_END---`, {
      encoding: 'utf-8'
    }).split('---COMMIT_END---').filter(Boolean);

    const grouped = {};

    commits.forEach(msg => {
      const lines = msg.trim().split('\n');
      const firstLine = lines[0];

      // 解析 conventional commit 格式
      const match = firstLine.match(/^(feat|fix|perf|refactor|test|docs|chore|style|ci)(\(.+\))?!?:\s*(.+)$/);

      if (match) {
        const type = match[1];
        const scope = match[2] ? match[2].slice(1, -1) : '';
        const subject = match[3];
        const typeLabel = commitTypes[type] || type;

        if (!grouped[typeLabel]) {
          grouped[typeLabel] = [];
        }

        grouped[typeLabel].push({
          scope,
          subject,
          fullMessage: firstLine
        });
      }
    });

    return {
      lastTag,
      commits: grouped,
      commitCount: commits.length
    };
  } catch (error) {
    console.error('错误：', error.message);
    return { commits: {}, commitCount: 0, lastTag: null };
  }
}

function getNextVersion(currentVersion) {
  const parts = currentVersion.split('.');
  const patch = parseInt(parts[2]) + 1;
  return `${parts[0]}.${parts[1]}.${patch}`;
}

function generateChangelogEntry(version, analysis) {
  // ★ 标题必须带 `v` 前缀:release workflow 按 `## v${tag}` 精确匹配 CHANGELOG
  // (.github/workflows/release.yml 两处),缺 v 会在打 tag 时抛
  // `No changelog entry found for vX.Y.Z` —— 而那条链路平时跑不到。
  let entry = `## v${version}\n\n`;

  if (Object.keys(analysis.commits).length === 0) {
    entry += '无重大变化\n\n';
    return entry;
  }

  // 按顺序输出：新增、改动、修复、其他
  const order = ['新增', '改动', '改进', '重构', '性能', '修复', '测试', '文档', '杂项'];

  for (const type of order) {
    if (analysis.commits[type]) {
      entry += `### ${type}\n\n`;
      analysis.commits[type].forEach(commit => {
        const prefix = commit.scope ? `[${commit.scope}] ` : '';
        entry += `- ${prefix}${commit.subject}\n`;
      });
      entry += '\n';
    }
  }

  return entry;
}

function updateChangelog(newVersion, analysis) {
  const changelogPath = path.join(projectRoot, 'CHANGELOG.md');
  let content = fs.readFileSync(changelogPath, 'utf-8');

  const entry = generateChangelogEntry(newVersion, analysis);

  // 在第一个 ## 标题之前插入新版本
  const lines = content.split('\n');
  let insertIndex = -1;

  for (let i = 0; i < lines.length; i++) {
    if (lines[i].startsWith('##') && lines[i].includes('v')) {
      insertIndex = i;
      break;
    }
  }

  if (insertIndex > 0) {
    // 尾部那个空行不能省:去掉它,新条目最后一行会紧贴下一条 `## vX.Y.Z`
    lines.splice(insertIndex, 0, '', entry.trimEnd(), '');
    fs.writeFileSync(changelogPath, lines.join('\n'));
    return true;
  }

  return false;
}

function updatePackageJson(newVersion) {
  const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf-8'));
  pkg.version = newVersion;
  fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n');
}

export function analyzeVersion() {
  const analysis = parseCommits();
  const nextVersion = getNextVersion(pkg.version);

  return {
    currentVersion: pkg.version,
    nextVersion,
    analysis
  };
}

export function generateRelease() {
  const { currentVersion, nextVersion, analysis } = analyzeVersion();

  console.log(`\n当前版本: ${currentVersion}`);
  console.log(`下一个版本: ${nextVersion}`);
  console.log(`自 ${analysis.lastTag || '开始'} 以来的提交: ${analysis.commitCount}\n`);

  console.log('提交类型分布:');
  Object.entries(analysis.commits).forEach(([type, commits]) => {
    console.log(`  ${type}: ${commits.length}`);
  });

  const entry = generateChangelogEntry(nextVersion, analysis);
  console.log('\n生成的变更日志:\n');
  console.log(entry);

  return { currentVersion, nextVersion, analysis };
}

if (process.argv[1] === __filename) {
  const command = process.argv[2];

  if (command === 'analyze') {
    const result = analyzeVersion();
    console.log(JSON.stringify(result, null, 2));
  } else if (command === 'generate') {
    generateRelease();
  } else if (command === 'update') {
    const { nextVersion, analysis } = analyzeVersion();

    console.log(`更新版本号到 ${nextVersion}...`);
    updatePackageJson(nextVersion);

    console.log(`更新 CHANGELOG.md...`);
    if (updateChangelog(nextVersion, analysis)) {
      console.log('✓ 版本日志已更新');
      console.log(`\n请运行以下命令提交更改:`);
      console.log(`  git add package.json CHANGELOG.md`);
      console.log(`  git commit -m "chore: release v${nextVersion}"`);
      console.log(`  git tag v${nextVersion}`);
    } else {
      console.error('✗ 更新失败');
      process.exit(1);
    }
  } else {
    console.log(`
版本分析工具

用法:
  node scripts/version-analyze.mjs analyze     # 分析提交并输出 JSON
  node scripts/version-analyze.mjs generate    # 生成变更日志预览
  node scripts/version-analyze.mjs update      # 更新版本号和变更日志
    `);
  }
}

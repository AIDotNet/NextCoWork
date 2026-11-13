# 2.x → 3.0 移植登记

AGENTS.md §5 要求:移植 2.x 模块时单测一起移植,并在这里登记;行为差异写明原因。
2.x 源码位置指 `main` 分支。

| 2.x 模块 | 3.0 位置 | 3.0 测试 | 状态 | 行为差异与原因 |
| --- | --- | --- | --- | --- |
| `src/main/db/schema.ts`(迁移 1–37) | `src/Ncw.Agent.Storage/Schema/Legacy/*.sql` | `DatabaseTests` | 已移植 | SQL 逐条原样抽出;新库结构已与真实 2.x v37 库逐条比对一致。3.0 的迁移从 38 起放在 `Schema/V3/`。 |
| `src/main/db/index.ts`(打开、PRAGMA、迁移执行) | `AgentDatabase` | `DatabaseTests` | 已移植 | 规则相同(WAL / NORMAL / busy_timeout 2000 / 外键 / 每条迁移一个事务 / user_version 镜像 / 第 7 条补列 / 第 29 条重建全文索引)。**新增**:库版本比本程序新时拒绝打开(2.x 会继续跑),避免回退版本后误写新结构。 |
| `src/main/db/` 消息全文索引写入 | `MessageFts` | `DatabaseTests.Old_2x_database_is_upgraded_and_v29_reindexes_existing_messages` | 部分 | 只移植了索引文本提取、写入、重建、对账;消息仓库本身随会话模块一起移植。 |
| `src/main/db/repo.ts` 工作区部分 | `WorkspaceRepository` | `WorkspaceRepositoryTests` | 已移植 | 作用域收窄在 SQL 里;撞上别的作用域的 id 抛异常而不是静默跳过(3.0 由协议层转成错误码)。不认识的 JSON 字段原样保留。 |
| `src/main/migration/takeover.ts`、`src/main/db/takeover.ts` | `Ncw.Agent.Storage/Takeover/`(`DataRootTakeover`、`DatabaseBackup`、`Ownership`、`LiveOwnerCheck`) | `TakeoverTests`、`DataTakeoverHostTests` | 已移植 | 日志格式(v1、阶段名)和所有权表相同。差异:① `SingletonLock` 同时查数据根和 profile 根(2.x 只查数据根,锁实际在 profile 根,会漏);Windows 上查 Chromium 的 `lockfile`。② 提交不再创建 2.x 独立服务的身份和 `service.lock`(3.0 没有独立设备服务),改为在已迁移的库里写标记,设备 id 来自 3.0 的 `device.json`。③ 回退不删除设备身份(3.0 的身份文件与 2.x 的 `device-identity.json` 不同名)。④ 日志是 committed 但库里没有标记(例如手动恢复了旧库)时允许重新接管。⑤ 全新数据根也写标记。 |
| `src/main/db/flat-layout.ts`、`chromium-layout.ts`、旧 userData 合并 | — | `TakeoverTests.Flat_2x_layout_is_reported_as_unmigrated` | 不移植 | 只检测并提示用户先用 2.x 打开一次;布局整理由 2.x 自己完成。 |
| `src/main/secrets/credential-crypto.ts`(NCK1)与 safeStorage 迁移 | — | — | 未开始 | 接管摘要里「需要重新输入的凭据」暂时恒为 0,遗留 safeStorage 行原样保留。 |
| `src/shared/util/id.ts` | `Ncw.Core.Ids.Ulid` | `UlidTests` | 已移植 | 格式相同,同毫秒单调递增。 |
| `src/shared/domain/workspace.ts` 的 `DEFAULT_WORKSPACE_SETTINGS` | `WorkspaceRepository.DefaultSettings` | `WorkspaceRepositoryTests.New_workspace_has_the_2x_shape` | 已移植 | 无 |

测试来源:`TakeoverTests` 逐条移植自 2.x 的 `src/main/migration/__tests__/takeover.test.ts`;数据库、工作区、ULID 在 2.x 里没有直接的单测
(`src/main/db/__tests__/` 针对的是 provider、usage、device 等尚未移植的模块),3.0 的测试按 2.x 的实现行为重新编写,
并用真实 2.x 数据库(只读)验证了兼容性。接管还在一份真实 2.x 库(1.6 GB、631 个会话、111280 条消息)的副本上手动验证过:
行数全部保留,备份加验证约 16 秒。

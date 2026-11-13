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
| `src/main/secrets/credential-crypto.ts`(NCK1 与主密钥文件) | `Ncw.Agent.Storage/Credentials/CredentialCrypto` | `CredentialTests` | 已移植 | 格式逐字节兼容(已用真实 2.x 库的 17 条凭据验证,全部解开)。错误信息改为英文的 `CryptographicException`(界面文字由控制端生成)。 |
| `src/main/host/index.ts` 的凭据读写、`migrateLegacyCredentials` | `CredentialStore` | `CredentialTests`、`DataTakeoverHostTests.Takeover_converts_legacy_credentials_and_reports_the_ones_to_reenter` | 部分 | 读取时旧行退回 safeStorage 解密(长期保留)、写入时校验主密钥、迁移幂等,均与 2.x 相同。差异:① 旧行迁移只在接管时执行(2.x 每次启动都跑),这样 macOS 钥匙串授权框只出现在已经提示过的接管页上;② safeStorage 不再经 Electron,而是按 Chromium `os_crypt` 格式自己解(见下一行)。按账户作用域换算 ref、`provider:<id>` 走 `providers.json` 的文件模式随供应商模块移植。 |
| Electron `safeStorage`(旧凭据行) | `ChromiumOsCrypt` | `CredentialTests`(openssl 独立生成的 v10 向量) | 已实现,待真机验证 | macOS:钥匙串「NextCoWork Safe Storage」(也试开发时的「nextcowork Safe Storage」),PBKDF2-SHA1 1003 次 + AES-128-CBC;Linux:v10 固定口令,v11(libsecret)不支持、计为需要重新输入;Windows:`Local State` 的 DPAPI 密钥 + AES-256-GCM。本机没有旧行和钥匙串条目,三个平台都还需要用带旧行的真实数据验证(Electron 44 的格式)。 |
| `src/shared/util/id.ts` | `Ncw.Core.Ids.Ulid` | `UlidTests` | 已移植 | 格式相同,同毫秒单调递增。 |
| `src/shared/domain/workspace.ts` 的 `DEFAULT_WORKSPACE_SETTINGS` | `WorkspaceRepository.DefaultSettings` | `WorkspaceRepositoryTests.New_workspace_has_the_2x_shape` | 已移植 | 无 |

测试来源:`TakeoverTests` 逐条移植自 2.x 的 `src/main/migration/__tests__/takeover.test.ts`;数据库、工作区、ULID 在 2.x 里没有直接的单测
(`src/main/db/__tests__/` 针对的是 provider、usage、device 等尚未移植的模块),3.0 的测试按 2.x 的实现行为重新编写,
并用真实 2.x 数据库(只读)验证了兼容性。接管还在一份真实 2.x 库(1.6 GB、631 个会话、111280 条消息)的副本上手动验证过:
行数全部保留,备份加验证约 16 秒。

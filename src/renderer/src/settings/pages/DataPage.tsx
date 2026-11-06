import {
  useCallback,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import {
  AlertTriangle,
  Check,
  Download,
  FolderOpen,
  HardDrive,
  RefreshCw,
  Trash2,
  Upload } from "lucide-react";
import type {
  BackupFrequency,
  StorageStats,
} from "../../../../shared/domain/settings";
import type {
  BackupStatus,
  CleanupAge,
  CleanupPreview,
  ImportPreview,
  RestorePreview,
} from "../../../../shared/domain/data";
import { Button } from "../../components/arc/button/button";
import { Dialog } from "../../components/ui/Dialog";
import { Switch } from "../../components/arc/switch/switch";
import { Select } from "../../components/ui/Select";
import { TextInput } from "../../components/ui/TextInput";
import { useI18n } from "../../i18n";
import { cn } from "../../lib/cn";
import * as dataService from "../../services/data";
import * as configSyncService from "../../services/config-sync";
import { SYNC_PASSWORD_MIN_LENGTH, type SyncPreview, type SyncStatus } from "../../../../shared/domain/config-sync";
import { useRunIndex } from "../../stores/session";
import type { SettingsPageProps } from "../props";
import { formatBytes, formatCount } from "../format";
import { on } from "../../services/ipc";
import { Spinner } from '../../components/ui/Spinner'

type ModalState =
  | { kind: "export" }
  | { kind: "import"; preview: ImportPreview }
  | { kind: "restore"; preview: RestorePreview }
  | { kind: "cleanup"; preview: CleanupPreview; age?: CleanupAge }
  | { kind: "syncSetup" }
  | { kind: "syncPreview"; preview: SyncPreview };

/** 设置 › 数据：所有结果都来自主进程数据服务，不在页面里模拟成功状态。 */
export function DataPage({ settings, patch }: SettingsPageProps): ReactNode {
  const { locale, t } = useI18n();
  const [stats, setStats] = useState<StorageStats | null>(null);
  const [backup, setBackup] = useState<BackupStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [modal, setModal] = useState<ModalState | null>(null);
  const [age, setAge] = useState<CleanupAge>(3);
  const [includeKeys, setIncludeKeys] = useState(false);
  const [syncStatus, setSyncStatus] = useState<SyncStatus | null>(null);
  const [syncConflicts, setSyncConflicts] = useState<Awaited<ReturnType<typeof configSyncService.getConflicts>>>([]);
  const [exportPassword, setExportPassword] = useState("");
  const [exportPasswordAgain, setExportPasswordAgain] = useState("");
  const [importPassword, setImportPassword] = useState("");
  const [syncPassword, setSyncPassword] = useState("");
  const [syncPasswordAgain, setSyncPasswordAgain] = useState("");
  const [rememberSyncKey, setRememberSyncKey] = useState(true);
  const running = useRunIndex().length > 0;

  const refresh = useCallback(
    async (initial = false): Promise<void> => {
      if (initial) setLoading(true);
      try {
        const [nextStats, nextBackup, nextSync, nextConflicts] = await Promise.all([
          dataService.getStats(),
          dataService.getBackupStatus(),
          configSyncService.getStatus(),
          configSyncService.getConflicts(),
        ]);
        setStats(nextStats);
        setBackup(nextBackup);
        setSyncStatus(nextSync);
        setSyncConflicts(nextConflicts);
        if (nextBackup.lastError === null) setError(null);
      } catch (err) {
        setError(errorMessage(err));
      } finally {
        if (initial) setLoading(false);
      }
    },
    [],
  );

  useEffect(() => {
    void refresh(true);
    return on("configSync:changed", setSyncStatus);
  }, [refresh]);

  const closeModal = useCallback((): void => {
    setModal(null);
    setIncludeKeys(false);
    setExportPassword("");
    setExportPasswordAgain("");
    setImportPassword("");
    setSyncPassword("");
    setSyncPasswordAgain("");
  }, []);

  const run = useCallback(
    async <T,>(
      label: string,
      action: () => Promise<T>,
      success?: (value: T) => string | null,
      refreshAfter = true,
    ): Promise<T | null> => {
      setBusy(label);
      setError(null);
      setNotice(null);
      try {
        const value = await action();
        const message = success?.(value);
        if (message !== undefined && message !== null) setNotice(message);
        if (refreshAfter) await refresh();
        return value;
      } catch (err) {
        setError(syncErrorMessage(err, t));
        return null;
      } finally {
        setBusy(null);
      }
    },
    [refresh, t],
  );

  const chooseDirectory = async (): Promise<void> => {
    const selected = await run("choose-directory", () =>
      dataService.chooseBackupDirectory(),
    );
    if (selected !== null && selected !== undefined)
      setNotice(t("data.backupDirectorySet", { path: selected }));
  };

  const openImportPreview = async (): Promise<void> => {
    const preview = await run("import-preview", () =>
      dataService.importPreview(),
    );
    if (preview !== null && preview !== undefined)
      setModal({ kind: "import", preview });
  };

  const openRestorePreview = async (): Promise<void> => {
    if (running) return;
    const result = await run("restore-preview", () =>
      dataService.restoreBackup(false),
    );
    if (result?.preview !== undefined)
      setModal({ kind: "restore", preview: result.preview });
  };

  const openCleanupPreview = async (
    kind: CleanupPreview["kind"],
    selectedAge?: CleanupAge,
  ): Promise<void> => {
    if (running) return;
    const preview = await run("cleanup-preview", () =>
      dataService.cleanupPreview(kind, selectedAge),
    );
    if (preview !== null)
      setModal({
        kind: "cleanup",
        preview,
        ...(selectedAge === undefined ? {} : { age: selectedAge }),
      });
  };

  const setupSync = async (): Promise<void> => {
    if (syncPassword.length < SYNC_PASSWORD_MIN_LENGTH) {
      setError(t("data.cloudSyncPasswordMin", { count: SYNC_PASSWORD_MIN_LENGTH }));
      return;
    }
    if (syncStatus?.control?.vaultConfigured !== true && syncPassword !== syncPasswordAgain) {
      setError(t("data.passwordMismatch"));
      return;
    }
    const result = await run(
      "sync-setup",
      () => configSyncService.setup(syncPassword, rememberSyncKey),
      undefined,
      false,
    );
    if (result !== null) {
      setSyncStatus(result);
      setSyncPassword("");
      setSyncPasswordAgain("");
      closeModal();
      await refresh();
    }
  };

  const openSyncPreview = async (): Promise<void> => {
    const preview = await run("sync-preview", () => configSyncService.getPreview());
    if (preview !== null) setModal({ kind: "syncPreview", preview });
  };

  const confirmSync = async (): Promise<void> => {
    const result = await run("sync-confirm", () => configSyncService.confirmInitial(), undefined, false);
    if (result === null) return;
    closeModal();
    await refresh();
  };

  const executeImport = async (): Promise<void> => {
    const preview = modal?.kind === "import" ? modal.preview : null;
    if (preview === null) return;
    const result = await run(
      "import-apply",
      () =>
        dataService.importApply(
          preview.hasEncryptedCredentials ? importPassword : undefined,
        ),
      (value) =>
        t("data.imported", {
          imported: formatCount(value.imported),
          overwritten: formatCount(value.overwritten),
        }),
    );
    if (result !== null) closeModal();
  };

  const executeRestore = async (): Promise<void> => {
    if (running || modal?.kind !== "restore") return;
    const result = await run(
      "restore",
      () => dataService.restoreBackup(true),
      (value) => (value?.restored ? t("data.restoreComplete") : null),
    );
    if (result?.restored) closeModal();
  };

  const executeCleanup = async (): Promise<void> => {
    if (running || modal?.kind !== "cleanup") return;
    const target = modal;
    // Different cleanup operations return different result shapes (for example,
    // clearLocalData reports whether the deletion completed).  The page only
    // needs to know whether the service call succeeded.
    let label: string;
    let action: () => Promise<unknown>;
    if (target.preview.kind === "attachments") {
      label = "cleanup-attachments";
      action = dataService.cleanupAttachments;
    } else if (target.preview.kind === "age") {
      label = "cleanup-age";
      action = () => dataService.cleanupByAge(target.age ?? age);
    } else if (target.preview.kind === "history") {
      label = "clear-history";
      action = dataService.clearHistory;
    } else {
      label = "clear-local-data";
      action = () => dataService.clearLocalData(true);
    }
    // `clearLocalData` closes the database and requests app.quit(); querying
    // stats again after it succeeds would race the shutdown path and turn a
    // completed deletion into a spurious error state.
    const result = await run(
      label,
      action,
      undefined,
      target.preview.kind !== "local-data",
    );
    if (result !== null) closeModal();
  };

  const doExport = async (): Promise<void> => {
    if (includeKeys) {
      if (exportPassword.length < 8) {
        setError(t("data.passwordMinLength"));
        return;
      }
      if (exportPassword !== exportPasswordAgain) {
        setError(t("data.passwordMismatch"));
        return;
      }
    }
    const result = await run(
      "export",
      () =>
        dataService.exportData({
          includeEncryptedKeys: includeKeys,
          ...(includeKeys ? { password: exportPassword } : {}),
        }),
      (value) =>
        value === null ? null : t("data.exported", { path: value.path }),
    );
    if (result !== null) closeModal();
  };

  const frequency = settings.data?.backupFrequency ?? "manual";
  const backupDirectory = settings.data?.backupDirectory ?? null;
  const busyNow = busy !== null;
  // 需求：使用统计同步只在同步已经跑起来(已解锁并确认)时才有意义；此前画出开关
  // 也没有后端能力接住它。
  const usageSyncAvailable =
    syncStatus?.control?.phase === "ready" || syncStatus?.control?.phase === "syncing";
  const riskDisabled = running || busyNow;
  const statValues = useMemo(
    () =>
      [
        [t("data.stat.database"), formatBytes(stats?.dbBytes)],
        [
          t("data.stat.conversationFiles"),
          formatBytes(stats?.conversationBytes),
        ],
        [
          t("data.stat.conversations"),
          t("data.countConversations", {
            count: formatCount(stats?.conversationCount),
          }),
        ],
        [
          t("data.stat.messages"),
          t("data.countMessages", { count: formatCount(stats?.messageCount) }),
        ],
      ] as const,
    [stats, t],
  );
  const frequencyOptions: ReadonlyArray<{
    value: BackupFrequency;
    label: string;
  }> = [
    { value: "manual", label: t("data.frequency.manual") },
    { value: "daily", label: t("data.frequency.daily") },
    { value: "weekly", label: t("data.frequency.weekly") },
  ];
  const ageOptions: ReadonlyArray<{ value: CleanupAge; label: string }> = [
    { value: "7d", label: t("data.age.7d") },
    { value: "15d", label: t("data.age.15d") },
    { value: 1, label: t("data.age.1") },
    { value: 3, label: t("data.age.3") },
    { value: 6, label: t("data.age.6") },
    { value: 12, label: t("data.age.12") },
  ];

  return (
    <div data-testid="data-page" className="pb-1">
      <DataSection title={t("data.cloudSync")} className="pt-2">
        <DataRow
          title={t("data.configureCloudSync")}
          description={
            syncStatus?.lastError === null || syncStatus?.lastError === undefined
              ? syncStatus?.enabled
                ? t("data.cloudSyncEnabled")
                : t("data.cloudSyncHint")
              : syncErrorMessage(new Error(syncStatus.lastError), t)
          }
          density="compact"
          last={!usageSyncAvailable}
        >
          {syncStatus?.accountId === null || syncStatus === null ? (
            <span className="text-[12px] text-muted-fg">{t("data.cloudSyncSignedOut")}</span>
          ) : syncStatus.control?.phase === "off" ||
            syncStatus.control?.phase === "passwordRequired" ||
            (syncStatus.control?.phase === "error" &&
              syncStatus.control?.errorCode === "migrationRequired") ? (
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={busyNow}
              onClick={() => {
                // 需求：弹窗内联显示的是页面共享的 error，打开前清掉上一轮残留，
                // 否则一打开就挂着与本次输入无关的旧报错。
                setError(null);
                setModal({ kind: "syncSetup" });
              }}
            >
              {syncStatus.control.phase === "off"
                ? t("data.cloudSyncSetPassword")
                : t("data.cloudSyncUnlock")}
            </Button>
          ) : syncStatus.control?.phase === "unsupported" ? (
            <span className="text-[12px] text-fg-faint">{t("data.cloudSyncUnsupported")}</span>
          ) : (
            <span className="text-[12px] text-muted-fg">
              {syncStatus.control?.phase === "syncing"
                ? t("data.cloudSyncSyncing")
                : syncStatus.enabled
                  ? t("data.cloudSyncEnabled")
                  : t("data.cloudSyncHint")}
            </span>
          )}
        </DataRow>
        {usageSyncAvailable && (
          <DataRow
            title={t("usage.sync.toggle")}
            description={t("usage.sync.toggleHint")}
            density="compact"
            last
          >
            {/* 状态以主进程为准：切换后由 configSync:changed 广播回来，页面不留镜像 */}
            <Switch
              checked={syncStatus?.control?.selection.usage === true}
              onCheckedChange={(enabled) => {
                void run("sync-usage", () => configSyncService.setUsageSync(enabled), undefined, false);
              }}
              aria-label={t("usage.sync.toggle")}
              disabled={busyNow}
            />
          </DataRow>
        )}
        {syncConflicts.length > 0 && (
          <DataRow
            title={t("data.cloudSyncConflicts", { count: syncConflicts.length })}
            description={syncConflicts
              .slice(0, 3)
              .map((conflict) =>
                t("data.cloudSyncConflictDetail", {
                  kind: conflict.kind,
                  entity: conflict.entityId,
                }),
              )
              .join(" · ")}
          >
            <div className="flex items-center gap-1.5">
              <Button type="button" size="sm" variant="secondary" onClick={() => { void run("sync-resolve-local", () => configSyncService.resolve(syncConflicts[0]!.id, false)); }} disabled={busyNow}>{t("data.cloudSyncKeepLocal")}</Button>
              <Button type="button" size="sm" variant="secondary" onClick={() => { void run("sync-resolve-remote", () => configSyncService.resolve(syncConflicts[0]!.id, true)); }} disabled={busyNow}>{t("data.cloudSyncUseRemote")}</Button>
            </div>
          </DataRow>
        )}
        {syncStatus?.needsInitialReview && syncStatus.accountId !== null && (
          <DataRow
            title={t("data.cloudSyncReview")}
            description={t("data.cloudSyncReviewHint")}
            last
          >
            <Button type="button" size="sm" variant="secondary" onClick={() => { void openSyncPreview(); }} disabled={busyNow}>
              {t("data.cloudSyncReviewAction")}
            </Button>
          </DataRow>
        )}
      </DataSection>

      <DataSection title={t("data.migration")}>
        <DataRow title={t("data.export")} density="single">
          <div className="flex items-center gap-2">
            <SelectControl
              ariaLabel={t("data.exportScope")}
              value="all"
              options={[{ value: "all", label: t("data.allData") }]}
            />
            <SelectControl
              ariaLabel={t("data.exportFormat")}
              value="json"
              options={[{ value: "json", label: "JSON" }]}
            />
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={busyNow}
              onClick={() => setModal({ kind: "export" })}
            >
              <Download size={13} />
              {t("data.export")}
            </Button>
          </div>
        </DataRow>
        <DataRow
          title={t("data.import")}
          description={t("data.importHint")}
          last
        >
          <Button
            type="button" size="sm"
            variant="secondary"
            disabled={busyNow}
            onClick={() => {
              void openImportPreview();
            }}
          >
            <Upload size={13} />
            {t("data.chooseAndImport")}
          </Button>
        </DataRow>
      </DataSection>

      <DataSection title={t("data.backups")}>
        <DataRow
          title={t("data.backupDirectory")}
          description={backupDirectory ?? t("data.notSet")}
        >
          <Button
            type="button"
            size="sm"
            variant="secondary"
            disabled={busyNow}
            onClick={() => {
              void chooseDirectory();
            }}
          >
            <FolderOpen size={13} />
            {backupDirectory
              ? t("data.changeDirectory")
              : t("data.chooseDirectory")}
          </Button>
        </DataRow>
        <DataRow
          title={t("data.backupFrequency")}
          description={t("data.backupFrequencyHint")}
        >
          <SelectControl
            ariaLabel={t("data.backupFrequency")}
            value={frequency}
            options={frequencyOptions}
            onChange={(value) =>
              patch({ data: { backupFrequency: value as BackupFrequency } })
            }
            className="w-[98px]"
          />
        </DataRow>
        <DataRow
          title={t("data.lastBackup")}
          description={
            backup?.lastError
              ? t("data.backupFailed", { error: backup.lastError })
              : formatDate(
                  backup?.lastBackupAt,
                  locale,
                  t("data.neverBackedUp"),
                )
          }
        >
          <Button
            type="button" size="sm"
            variant="secondary"
            disabled={backupDirectory === null || busyNow}
            onClick={() => {
              void run(
                "backup",
                () => dataService.createBackup(true),
                () => t("data.backupComplete"),
              );
            }}
          >
            <HardDrive size={13} />
            {t("data.backupNow")}
          </Button>
        </DataRow>
        <DataRow
          title={t("data.restoreBackup")}
          description={t("data.restoreBackupHint")}
          last
        >
          <Button
            type="button" size="sm"
            variant="secondary"
            disabled={riskDisabled}
            onClick={() => {
              void openRestorePreview();
            }}
          >
            <RefreshCw size={13} />
            {t("data.chooseBackup")}
          </Button>
        </DataRow>
      </DataSection>

      <DataSection title={t("data.storage")}>
        <div className="grid grid-cols-4 gap-2 pb-2 pt-3">
          {statValues.map(([label, value]) => (
            <Stat key={label} label={label} value={loading ? "…" : value} />
          ))}
        </div>
        <DataRow
          title={t("data.dataDirectory")}
          description={stats?.dataDirectory ?? t("data.dataDirectoryHint")}
          density="compact"
        >
          <Button
            type="button" size="sm"
            variant="secondary"
            disabled={busyNow}
            onClick={() => {
              void run(
                "open-directory",
                () => dataService.openDataDirectory(),
                () => t("data.directoryOpened"),
              );
            }}
          >
            <FolderOpen size={13} />
            {t("data.openDirectory")}
          </Button>
        </DataRow>
        <DataRow
          title={t("data.optimizeStorage")}
          description={t("data.optimizeStorageHint")}
          density="compact"
        >
          <Button
            type="button" size="sm"
            variant="secondary"
            disabled={busyNow}
            onClick={() => {
              void run(
                "vacuum",
                () => dataService.vacuum(),
                () => t("data.storageOptimized"),
              );
            }}
          >
            <HardDrive size={13} />
            {t("data.optimize")}
          </Button>
        </DataRow>
        <DataRow
          title={t("data.cleanupAttachments")}
          description={t("data.cleanupAttachmentsHint")}
          density="compact"
        >
          <Button
            type="button" size="sm"
            variant="secondary"
            disabled={riskDisabled}
            onClick={() => {
              void openCleanupPreview("attachments");
            }}
          >
            <Trash2 size={13} />
            {t("data.cleanup")}
          </Button>
        </DataRow>
        <DataRow title={t("data.cleanupRange")} density="compact">
          <div className="flex items-center gap-2">
            <SelectControl
              ariaLabel={t("data.cleanupRange")}
              value={String(age)}
              options={ageOptions.map((x) => ({
                value: String(x.value),
                label: x.label,
              }))}
              className="w-[114px]"
              onChange={(value) =>
                setAge(value === "7d" || value === "15d" ? value : (Number(value) as CleanupAge))
              }
            />
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={riskDisabled}
              onClick={() => {
                void openCleanupPreview("age", age);
              }}
            >
              <Trash2 size={13} />
              {t("data.cleanup")}
            </Button>
          </div>
        </DataRow>
        <DataRow
          title={t("data.clearHistory")}
          description={t("data.clearHistoryHint")}
          descriptionTone="danger"
          density="compact"
          last
        >
          <Button
            type="button" size="sm"
            variant="danger"
            disabled={riskDisabled}
            onClick={() => {
              void openCleanupPreview("history");
            }}
          >
            <Trash2 size={13} />
            {t("data.clearChats")}
          </Button>
        </DataRow>
      </DataSection>

      <DataSection title={t("data.clearLocal")} className="mb-1">
        <DataRow
          title={t("data.deleteAndQuit")}
          description={t("data.deleteAndQuitHint")}
          density="compact"
          last
        >
          <Button
            type="button" size="sm"
            variant="danger"
            disabled={riskDisabled}
            onClick={() => {
              void openCleanupPreview("local-data");
            }}
          >
            <Trash2 size={13} />
            {t("data.deleteAndQuit")}
          </Button>
        </DataRow>
      </DataSection>

      {busyNow && (
        <p
          className="flex items-center gap-1.5 px-1 pb-2 text-[11.5px] text-fg-muted"
          role="status"
        >
          <Spinner size="xs" />
          {t("data.processing")}
        </p>
      )}
      {running && (
        <p
          className="flex items-center gap-1.5 px-1 pb-2 text-[11.5px] text-fg-muted"
          role="status"
        >
          <AlertTriangle size={12} />
          {t("data.runningAgents")}
        </p>
      )}
      {error !== null && (
        <p
          className="flex items-start gap-1.5 px-1 pb-2 text-[11.5px] text-danger"
          role="alert"
        >
          <AlertTriangle size={13} className="mt-0.5 shrink-0" />
          {error}
        </p>
      )}
      {notice !== null && (
        <p
          className="flex items-start gap-1.5 px-1 pb-2 text-[11.5px] text-fg-muted"
          role="status"
        >
          <Check size={13} className="mt-0.5 shrink-0 text-accent" />
          {notice}
        </p>
      )}

      <ExportDialog
        open={modal?.kind === "export"}
        includeKeys={includeKeys}
        password={exportPassword}
        passwordAgain={exportPasswordAgain}
        busy={busyNow}
        onIncludeKeys={setIncludeKeys}
        onPassword={setExportPassword}
        onPasswordAgain={setExportPasswordAgain}
        onClose={closeModal}
        onExport={() => {
          void doExport();
        }}
      />
      <ImportDialog
        open={modal?.kind === "import"}
        preview={modal?.kind === "import" ? modal.preview : null}
        password={importPassword}
        busy={busyNow}
        onPassword={setImportPassword}
        onClose={closeModal}
        onApply={() => {
          void executeImport();
        }}
      />
      <RestoreDialog
        open={modal?.kind === "restore"}
        preview={modal?.kind === "restore" ? modal.preview : null}
        busy={busyNow}
        onClose={closeModal}
        onApply={() => {
          void executeRestore();
        }}
      />
      <CleanupDialog
        open={modal?.kind === "cleanup"}
        preview={modal?.kind === "cleanup" ? modal.preview : null}
        busy={busyNow}
        onClose={closeModal}
        onApply={() => {
          void executeCleanup();
        }}
      />
      <SyncSetupDialog
        open={modal?.kind === "syncSetup"}
        existing={syncStatus?.control?.vaultConfigured === true}
        password={syncPassword}
        passwordAgain={syncPasswordAgain}
        remember={rememberSyncKey}
        busy={busyNow}
        error={modal?.kind === "syncSetup" ? error : null}
        onPassword={setSyncPassword}
        onPasswordAgain={setSyncPasswordAgain}
        onRemember={setRememberSyncKey}
        onClose={closeModal}
        onSubmit={() => { void setupSync(); }}
      />
      <SyncPreviewDialog
        open={modal?.kind === "syncPreview"}
        preview={modal?.kind === "syncPreview" ? modal.preview : null}
        busy={busyNow}
        onClose={closeModal}
        onConfirm={() => { void confirmSync(); }}
      />
    </div>
  );
}

function errorMessage(error: unknown): string {
  return error instanceof Error && error.message !== ""
    ? error.message
    : String(error);
}

function syncErrorMessage(
  error: unknown,
  t: ReturnType<typeof useI18n>["t"],
): string {
  const message = errorMessage(error);
  const code = message.startsWith("configSync.") ? message.slice("configSync.".length) : null;
  if (code === "password") return t("data.cloudSyncErrorPassword");
  if (code === "migrationRequired") return t("data.cloudSyncErrorMigration");
  if (code === "deviceRevoked") return t("data.cloudSyncErrorDevice");
  if (code === "network") return t("data.cloudSyncErrorNetwork");
  if (code === "unsupported") return t("data.cloudSyncUnsupported");
  if (code === "conflict") return t("data.cloudSyncErrorConflict");
  if (code === "storage") return t("data.cloudSyncErrorStorage");
  if (code === "locked") return t("data.cloudSyncErrorLocked");
  if (code === "signedOut") return t("data.cloudSyncErrorSignedOut");
  if (code === "invalidData") return t("data.cloudSyncErrorInvalidData");
  if (code === "accountChanged") return t("data.cloudSyncErrorAccountChanged");
  if (code === "busy") return t("data.cloudSyncErrorBusy");
  // 其余 configSync.* 是内部错误码,不能把 i18n key 原样丢给用户
  if (code !== null) return t("data.cloudSyncErrorUnknown");
  return message;
}

function formatDate(
  value: number | null | undefined,
  locale: string,
  fallback: string,
): string {
  if (value === null || value === undefined || !Number.isFinite(value))
    return fallback;
  return new Date(value).toLocaleString(locale, {
    dateStyle: "medium",
    timeStyle: "short",
  });
}

function SyncSetupDialog({
  open,
  existing,
  password,
  passwordAgain,
  remember,
  busy,
  error,
  onPassword,
  onPasswordAgain,
  onRemember,
  onClose,
  onSubmit,
}: {
  open: boolean;
  existing: boolean;
  password: string;
  passwordAgain: string;
  remember: boolean;
  busy: boolean;
  /**
   * 需求：校验失败 / 主进程 setup 失败时必须在弹窗里就地可见。
   * 不满足会怎样：页面底部那条 error 被全窗遮罩盖住，表现为点「创建并开始同步」毫无反馈。
   */
  error: string | null;
  onPassword: (value: string) => void;
  onPasswordAgain: (value: string) => void;
  onRemember: (value: boolean) => void;
  onClose: () => void;
  onSubmit: () => void;
}): ReactNode {
  const { t } = useI18n();
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={existing ? t("data.cloudSyncUnlockTitle") : t("data.cloudSyncSetupTitle")}
      description={existing ? t("data.cloudSyncUnlockHint") : t("data.cloudSyncSetupHint")}
      width={440}
      footer={(
        <div className="flex justify-end gap-2">
          <Button type="button" size="sm" variant="secondary" onClick={onClose} disabled={busy}>
            {t("common.cancel")}
          </Button>
          <Button
            type="button" size="sm"
            variant="primary"
            onClick={onSubmit}
            disabled={busy || password.length < SYNC_PASSWORD_MIN_LENGTH}
          >
            {busy ? <Spinner size="sm" /> : undefined}
            {existing ? t("data.cloudSyncUnlock") : t("data.cloudSyncCreate")}
          </Button>
        </div>
      )}
    >
      <div className="space-y-3">
        <TextInput
          type="password"
          value={password}
          onChange={onPassword}
          placeholder={t("data.cloudSyncPassword")}
          ariaLabel={t("data.cloudSyncPassword")}
        />
        {!existing && (
          <TextInput
            type="password"
            value={passwordAgain}
            onChange={onPasswordAgain}
            placeholder={t("data.cloudSyncPasswordAgain")}
            ariaLabel={t("data.cloudSyncPasswordAgain")}
          />
        )}
        <div className="flex items-center justify-between gap-3 rounded-[8px] border border-border px-2.5 py-2">
          <div>
            <p className="text-[12px] text-fg">{t("data.cloudSyncRemember")}</p>
            <p className="mt-0.5 text-[11px] text-fg-faint">{t("data.cloudSyncRememberHint")}</p>
          </div>
          <Switch checked={remember} onCheckedChange={onRemember} aria-label={t("data.cloudSyncRemember")} />
        </div>
        <p className="text-[11px] leading-[1.6] text-fg-faint">{t("data.cloudSyncEncryptionHint")}</p>
        {error !== null && (
          <p className="flex items-start gap-1.5 text-[11.5px] text-danger" role="alert">
            <AlertTriangle size={13} className="mt-0.5 shrink-0" />
            {error}
          </p>
        )}
      </div>
    </Dialog>
  );
}

function SyncPreviewDialog({
  open,
  preview,
  busy,
  onClose,
  onConfirm,
}: {
  open: boolean;
  preview: SyncPreview | null;
  busy: boolean;
  onClose: () => void;
  onConfirm: () => void;
}): ReactNode {
  const { t } = useI18n();
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t("data.cloudSyncPreviewTitle")}
      description={t("data.cloudSyncPreviewHint")}
      width={520}
      footer={(
        <div className="flex justify-end gap-2">
          <Button type="button" size="sm" variant="secondary" onClick={onClose} disabled={busy}>{t("data.cancel")}</Button>
          <Button type="button" size="sm" variant="primary" onClick={onConfirm} disabled={busy}>{t("data.cloudSyncConfirm")}</Button>
        </div>
      )}
    >
      <div className="space-y-2 text-[12px] text-fg-muted">
        <p>{t("data.cloudSyncPreviewCount", { count: preview?.count ?? 0 })}</p>
        <p>{t("data.cloudSyncSecretHint")}</p>
        <div className="max-h-48 overflow-auto rounded border border-border p-2">
          {(preview?.items ?? []).slice(0, 50).map((item) => (
            <div key={`${item.kind}:${item.entityId}`} className="flex justify-between py-1">
              <span>{item.kind}</span><span className="text-fg">{item.entityId}</span>
            </div>
          ))}
        </div>
      </div>
    </Dialog>
  );
}

function DataSection({
  title,
  children,
  className,
}: {
  title: string;
  children: ReactNode;
  className?: string;
}): ReactNode {
  return (
    <section
      className={cn(
        "mb-3 overflow-hidden rounded-[18px] bg-surface px-4",
        className,
      )}
    >
      <h3 className="pt-4 pb-0.5 text-[13px] text-fg">{title}</h3>
      {children}
    </section>
  );
}

function DataRow({
  title,
  description,
  children,
  descriptionTone,
  density = "default",
  last = false,
}: {
  title: string;
  description?: ReactNode;
  children?: ReactNode;
  descriptionTone?: "danger";
  density?: "default" | "compact" | "single";
  last?: boolean;
}): ReactNode {
  return (
    <div
      className={cn(
        "flex items-center gap-5",
        density === "single"
          ? "min-h-[48px] py-2"
          : density === "compact"
            ? "min-h-[58px] py-2.5"
            : "min-h-[62px] py-3",
        !last && "border-b border-border/50",
      )}
    >
      <div className="min-w-0 flex-1">
        <p className="text-[13px] text-fg">{title}</p>
        {description !== undefined && (
          <p
            className={cn(
              "mt-1 text-[11.5px] leading-[1.45] text-fg-muted",
              descriptionTone === "danger" && "text-danger",
            )}
          >
            {description}
          </p>
        )}
      </div>
      {children !== undefined && (
        <div className="flex shrink-0 justify-end">{children}</div>
      )}
    </div>
  );
}

function Stat({ label, value }: { label: string; value: string }): ReactNode {
  return (
    <div className="flex h-[60px] flex-col items-center justify-center rounded-[11px] bg-tint">
      <span className="text-[10.5px] text-fg-muted">{label}</span>
      <strong className="mt-1 text-[14px] font-normal text-fg">{value}</strong>
    </div>
  );
}

function SelectControl({
  value,
  options,
  onChange,
  ariaLabel,
  className,
}: {
  value: string;
  options: ReadonlyArray<{ value: string; label: string }>;
  onChange?: (value: string) => void;
  ariaLabel: string;
  className?: string;
}): ReactNode {
  // 设置浮层是 z-100 的模态，Radix 弹层 portal 到 body，必须 inModal 才不会被压在下面。
  return (
    <div className={cn("w-[108px]", className)}>
      <Select
        inModal
        value={value}
        options={options}
        ariaLabel={ariaLabel}
        onValueChange={(next) => onChange?.(next)}
      />
    </div>
  );
}

function ExportDialog({
  open,
  includeKeys,
  password,
  passwordAgain,
  busy,
  onIncludeKeys,
  onPassword,
  onPasswordAgain,
  onClose,
  onExport,
}: {
  open: boolean;
  includeKeys: boolean;
  password: string;
  passwordAgain: string;
  busy: boolean;
  onIncludeKeys: (value: boolean) => void;
  onPassword: (value: string) => void;
  onPasswordAgain: (value: string) => void;
  onClose: () => void;
  onExport: () => void;
}): ReactNode {
  const { t } = useI18n();
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t("data.exportData")}
      description={t("data.exportDialogHint")}
      width={500}
      footer={
        <>
          <Button type="button" size="sm" variant="secondary" disabled={busy} onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            type="button" size="sm"
            variant="primary"
            disabled={busy}
            onClick={onExport}
          >
            {busy ? <Spinner size="sm" /> : <Download size={13} />}
            {t("data.exportJson")}
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <div className="rounded-[10px] bg-tint px-3 py-2.5 text-[12px] leading-[1.6] text-fg-muted">
          {t("data.exportContents")}
        </div>
        <div className="flex items-center justify-between gap-4 rounded-[10px] border border-border px-3 py-2.5">
          <div>
            <p className="text-[12.5px] text-fg">
              {t("data.includeEncryptedCredentials")}
            </p>
            <p className="mt-0.5 text-[11px] text-fg-faint">
              {t("data.encryptionHint")}
            </p>
          </div>
          <Switch
            checked={includeKeys}
            onCheckedChange={onIncludeKeys}
            aria-label={t("data.includeEncryptedCredentials")}
          />
        </div>
        {includeKeys && (
          <div className="space-y-2">
            <PasswordField
              label={t("data.exportPassword")}
              value={password}
              onChange={onPassword}
              placeholder={t("data.passwordAtLeast8")}
            />
            <PasswordField
              label={t("data.confirmPassword")}
              value={passwordAgain}
              onChange={onPasswordAgain}
              placeholder={t("data.confirmExportPassword")}
            />
            <p className="text-[11px] text-fg-faint">
              {t("data.passwordStorageHint")}
            </p>
          </div>
        )}
      </div>
    </Dialog>
  );
}

function ImportDialog({
  open,
  preview,
  password,
  busy,
  onPassword,
  onClose,
  onApply,
}: {
  open: boolean;
  preview: ImportPreview | null;
  password: string;
  busy: boolean;
  onPassword: (value: string) => void;
  onClose: () => void;
  onApply: () => void;
}): ReactNode {
  const { t } = useI18n();
  if (preview === null) return null;
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t("data.importPreview")}
      description={preview.path}
      width={520}
      footer={
        <>
          <Button type="button" size="sm" variant="secondary" disabled={busy} onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            type="button" size="sm"
            variant="primary"
            disabled={
              busy || (preview.hasEncryptedCredentials && password.length < 8)
            }
            onClick={onApply}
          >
            {busy ? <Spinner size="sm" /> : <Upload size={13} />}
            {t("data.confirmImport")}
          </Button>
        </>
      }
    >
      <PreviewCounts
        rows={[
          [t("data.workspace"), preview.workspaceCount],
          [t("data.conversations"), preview.sessionCount],
          [t("data.messages"), preview.messageCount],
          [t("data.providers"), preview.providerCount],
          [t("data.modelAliases"), preview.aliasCount],
          [t("data.mcpServers"), preview.mcpServerCount],
        ]}
      />
      <p className="mt-3 text-[11.5px] leading-[1.6] text-fg-muted">
        {t("data.importSummary", {
          created: preview.newCount,
          updated: preview.overwriteCount,
          skipped: preview.skippedCount,
        })}
      </p>
      {preview.hasEncryptedCredentials && (
        <div className="mt-3 rounded-[10px] border border-border px-3 py-2.5">
          <p className="text-[12.5px] text-fg">{t("data.encryptedFile")}</p>
          <p className="mt-1 text-[11px] text-fg-faint">
            {t("data.importPasswordHint")}
          </p>
          <TextInput
            type="password"
            value={password}
            onChange={onPassword}
            placeholder={t("data.importPassword")}
            ariaLabel={t("data.importPassword")}
            className="mt-2"
          />
        </div>
      )}
    </Dialog>
  );
}

function RestoreDialog({
  open,
  preview,
  busy,
  onClose,
  onApply,
}: {
  open: boolean;
  preview: RestorePreview | null;
  busy: boolean;
  onClose: () => void;
  onApply: () => void;
}): ReactNode {
  const { locale, t } = useI18n();
  if (preview === null) return null;
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t("data.restoreTitle")}
      description={preview.path}
      width={500}
      footer={
        <>
          <Button type="button" size="sm" variant="secondary" disabled={busy} onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            type="button" size="sm"
            variant="primary"
            disabled={busy}
            onClick={onApply}
          >
            {busy ? <Spinner size="sm" /> : <RefreshCw size={13} />}
            {t("data.confirmRestore")}
          </Button>
        </>
      }
    >
      <div className="rounded-[10px] border border-border px-3 py-2.5 text-[12px] leading-[1.6] text-fg-muted">
        <p>
          {t("data.backupCreatedAt", {
            date: formatDate(preview.manifest.createdAt, locale, ""),
          })}
        </p>
        <p>
          {t("data.restoreContents", {
            sessions: formatCount(preview.sessionCount),
            messages: formatCount(preview.messageCount),
          })}
        </p>
        <p>{t("data.restoreHint")}</p>
      </div>
    </Dialog>
  );
}

function CleanupDialog({
  open,
  preview,
  busy,
  onClose,
  onApply,
}: {
  open: boolean;
  preview: CleanupPreview | null;
  busy: boolean;
  onClose: () => void;
  onApply: () => void;
}): ReactNode {
  const { t } = useI18n();
  if (preview === null) return null;
  const dangerous = preview.kind === "history" || preview.kind === "local-data";
  const title =
    preview.kind === "attachments"
      ? t("data.cleanupAttachments")
      : preview.kind === "age"
        ? t("data.cleanupByAge")
        : preview.kind === "history"
          ? t("data.clearHistory")
          : t("data.deleteAndQuit");
  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={title}
      description={t("data.confirmCleanupHint")}
      width={500}
      footer={
        <>
          <Button type="button" size="sm" variant="secondary" disabled={busy} onClick={onClose}>
            {t("common.cancel")}
          </Button>
          <Button
            type="button" size="sm"
            variant="danger"
            disabled={busy}
            onClick={onApply}
          >
            {busy ? <Spinner size="sm" /> : <Trash2 size={13} />}
            {preview.kind === "local-data"
              ? t("data.deletePermanentlyAndQuit")
              : t("data.confirmCleanup")}
          </Button>
        </>
      }
    >
      {dangerous && (
        <div className="mb-3 flex items-start gap-2 rounded-[10px] bg-danger/10 px-3 py-2.5 text-[12px] leading-[1.6] text-danger">
          <AlertTriangle size={14} className="mt-0.5 shrink-0" />
          {t("data.irreversible")}
          {preview.kind === "local-data"
            ? `${t("data.localDataRetention")}${t("data.localDataDeferredHint")}`
            : t("data.historyRetention")}
        </div>
      )}
      <PreviewCounts
        rows={[
          [t("data.conversations"), preview.sessionCount],
          [t("data.messages"), preview.messageCount],
          [t("data.attachments"), preview.attachmentCount],
        ]}
      />
      <p className="mt-3 text-[12px] text-fg-muted">
        {t("data.reclaimableSpace")}{" "}
        <span className="text-fg">{formatBytes(preview.bytes)}</span>
      </p>
      {preview.undeletable.length > 0 && (
        <p className="mt-2 text-[11.5px] leading-[1.6] text-danger">
          {t("data.undeletable", { count: preview.undeletable.length })}
        </p>
      )}
    </Dialog>
  );
}

function PreviewCounts({
  rows,
}: {
  rows: ReadonlyArray<readonly [string, number]>;
}): ReactNode {
  return (
    <div className="grid grid-cols-3 gap-2">
      {rows.map(([label, value]) => (
        <div
          key={label}
          className="rounded-[8px] bg-tint px-2 py-2 text-center"
        >
          <div className="text-[11px] text-fg-faint">{label}</div>
          <strong className="mt-0.5 block text-[14px] font-normal text-fg">
            {formatCount(value)}
          </strong>
        </div>
      ))}
    </div>
  );
}

function PasswordField({
  label,
  value,
  onChange,
  placeholder,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
}): ReactNode {
  return (
    <label className="block">
      <span className="mb-1 block text-[12px] text-fg-muted">{label}</span>
      <TextInput
        type="password"
        value={value}
        onChange={onChange}
        placeholder={placeholder}
        ariaLabel={label}
      />
    </label>
  );
}

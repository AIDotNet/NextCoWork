/**
 * 需求：开放网关展示真实监听状态，并允许显式开启远程访问与复制 API 密钥。
 *
 * ★ 这一页最容易犯的错是:把「端口 19836」印在界面上,让它看起来像是在监听。
 * 原先 HTTP 壳未实现所以用 TodoRow；现在由主进程监听后推送实际端口，仍不把
 * settings.gateway.preferredPort 当成正在监听的地址，启动失败时必须显示错误。
 */
import { useEffect, useState, type ReactNode } from "react";
import type { GatewayStatus } from "../../../../../shared/domain/provider";
import { Button } from "../../../components/ui/Button";
import { NumberInput } from "../../../components/ui/NumberInput";
import { Toggle } from "../../../components/ui/Toggle";
import { useI18n } from "../../../i18n";
import { SettingField, SettingGroup, SettingRow } from "../../Row";
import { getGatewayAddresses, getGatewayKey, getGatewayStatus, onGatewayStatus, resetGatewayHealth } from "../../../services/gateway";
import { copyText } from "../../../services/app";
import { useToastStore } from "../../../stores/toast";
import type { SettingsPageProps } from "../../props";

export function GatewayPane({ settings, patch }: SettingsPageProps): ReactNode {
  const { t } = useI18n();
  const g = settings.gateway;
  const [status, setStatus] = useState<GatewayStatus | null>(null);
  const [addresses, setAddresses] = useState<string[]>([]);
  const [failure, setFailure] = useState<string | null>(null);
  const [copying, setCopying] = useState(false);

  useEffect(() => {
    let active = true;
    let version = 0;
    const accept = (next: GatewayStatus): void => {
      if (!active) return;
      setStatus(next);
      const current = ++version;
      void getGatewayAddresses().then((values) => {
        if (active && current === version) setAddresses(values);
      }).catch((error: unknown) => {
        if (active && current === version) setFailure(String(error));
      });
    };
    // 需求：先订阅再拉快照；拉取期间有新推送时，不能让旧快照覆盖新状态。
    const off = onGatewayStatus(accept);
    void getGatewayStatus().then((next) => { if (version === 0) accept(next); }).catch((error: unknown) => {
      if (active) setFailure(String(error));
    });
    return () => { active = false; off(); };
  }, []);

  const copyKey = async (): Promise<void> => {
    setCopying(true);
    setFailure(null);
    try {
      await copyText(await getGatewayKey());
      useToastStore.getState().push({ tone: 'success', message: t('gateway.copied') });
    } catch (error) { setFailure(String(error)); }
    finally { setCopying(false); }
  };

  const resetHealth = async (): Promise<void> => {
    setFailure(null);
    try { setStatus(await resetGatewayHealth()); }
    catch (error) { setFailure(String(error)); }
  };
  return (
    <SettingGroup>
      <SettingRow
        title={t("connection.gateway.enable")}
        description={t("gateway.enableHint")}
      >
        <Toggle
          label={t("connection.gateway.enable")}
          checked={g.enabled}
          onChange={(enabled) => patch({ gateway: { enabled } })}
        />
      </SettingRow>

      <SettingRow title={t("gateway.remote")} description={t("gateway.remoteHint")}>
        <Toggle
          label={t("gateway.remote")}
          checked={g.allowRemote === true}
          onChange={(allowRemote) => patch({ gateway: { allowRemote } })}
        />
      </SettingRow>

      <SettingRow
        title={t("connection.gateway.port")}
        description={t("connection.gateway.portHint")}
      >
        <NumberInput
          value={g.preferredPort}
          min={1024}
          max={65535}
          ariaLabel={t("connection.gateway.port")}
          disabled={!g.enabled}
          onCommit={(preferredPort) => patch({ gateway: { preferredPort } })}
        />
      </SettingRow>

      <SettingRow
        title={t("connection.gateway.failover")}
        description={t("connection.gateway.failoverHint")}
      >
        <Toggle
          label={t("connection.gateway.failover")}
          checked={g.failover}
          disabled={!g.enabled}
          onChange={(failover) => patch({ gateway: { failover } })}
        />
      </SettingRow>

      <SettingRow title={t("gateway.status")}>
        <span role="status" className="text-[12px] text-fg-muted">
          {t(!g.enabled ? "gateway.disabled" : status?.port !== null && status?.port !== undefined ? "gateway.listening" : "gateway.starting")}
        </span>
      </SettingRow>
      {(failure !== null || status?.error !== undefined) && (
        <p role="alert" className="text-[12px] text-danger">{t("gateway.error", { error: failure ?? status?.error ?? '' })}</p>
      )}
      <SettingField
        title={t("connection.gateway.address")}
        description={t("gateway.addressHint")}
      >
        <div className="flex flex-col items-start gap-2">
          {addresses.map((address) => <code key={address} className="selectable text-[12px] text-fg">{address}</code>)}
          <Button size="sm" disabled={copying || status?.hasKey !== true} onClick={() => { void copyKey(); }}>{t("gateway.copyKey")}</Button>
          <span className="text-[12px] text-fg-muted">{t("gateway.keyHint")}</span>
        </div>
      </SettingField>
      <SettingField
        title={t("connection.gateway.interfaces")}
        description={t("gateway.interfacesHint")}
      >
        <code className="selectable text-[12px] text-fg-muted">/v1/workspaces · /v1/models · /v1/sessions · /v1/runs · /v1/interactions</code>
      </SettingField>
      <SettingField
        title={t("connection.gateway.health")}
        description={t("connection.gateway.healthHint")}
        last
      >
        <div className="flex flex-col items-start gap-2">
          {(status?.health ?? []).map((item) => <span key={item.providerId} className="text-[12px] text-fg-muted">{item.providerId}: {t(item.healthy ? "gateway.healthy" : "gateway.unhealthy")}</span>)}
          {(status?.health.length ?? 0) === 0 && <span className="text-[12px] text-fg-muted">{t("gateway.healthEmpty")}</span>}
          <Button size="sm" onClick={() => { void resetHealth(); }}>{t("gateway.resetHealth")}</Button>
        </div>
      </SettingField>
    </SettingGroup>
  );
}

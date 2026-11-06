import { useMemo, useState, type ReactNode } from "react";
import type { ModelAlias, UpstreamProvider, UpstreamProtocol } from "../../../../../shared/domain/provider";
import { effectiveModelProtocol } from "../../../../../shared/domain/provider";
import { Select } from "../../../components/ui/Select";
import { cn } from "../../../lib/cn";
import { useI18n } from "../../../i18n";
import { listModels, updateModel } from "../../../services/provider";

export const PROTOCOLS: readonly UpstreamProtocol[] = ["anthropic", "openai-chat", "openai-responses"];

export function protocolLabel(protocol: UpstreamProtocol, t: ReturnType<typeof useI18n>["t"]): string {
  if (protocol === "anthropic") return t("models.protocolAnthropic");
  if (protocol === "openai-chat") return t("models.protocolOpenAIChat");
  return t("models.protocolOpenAIResponses");
}

export function modelProtocolSummary(
  binding: ModelAlias,
  providers: readonly UpstreamProvider[],
  t: ReturnType<typeof useI18n>["t"]
): string {
  const provider = providers.find((item) => item.id === binding.providerId);
  if (provider === undefined) return t("models.protocolProviderMissing");
  const protocol = effectiveModelProtocol(provider, binding);
  return t("models.protocolSummary", {
    protocol: protocolLabel(protocol, t),
    state: binding.protocolOverride === undefined ? t("models.protocolInherited") : t("models.protocolOverridden")
  });
}

export function ModelProtocolEditor({
  bindings,
  providers,
}: {
  bindings: readonly ModelAlias[];
  providers: readonly UpstreamProvider[];
}): ReactNode {
  const { t } = useI18n();
  const [selectedKey, setSelectedKey] = useState(() => bindingKey(bindings[0]));
  const selected = bindings.find((binding) => bindingKey(binding) === selectedKey) ?? bindings[0];
  const provider = providers.find((item) => item.id === selected?.providerId);
  const actualKey = bindingKey(selected);

  if (selected === undefined || provider === undefined) {
    return <p className="text-[10.5px] text-fg-faint">{t("models.protocolNoBinding")}</p>;
  }

  return (
    <ProtocolBindingForm
      key={`${actualKey}:${selected?.protocolOverride ?? "inherit"}`}
      binding={selected}
      provider={provider}
      bindings={bindings}
      providers={providers}
      selectedKey={selectedKey}
      onSelect={setSelectedKey}
      protocolLabel={(protocol) => protocolLabel(protocol, t)}
    />
  );
}

function ProtocolBindingForm({
  binding,
  provider,
  bindings,
  providers,
  selectedKey,
  onSelect,
  protocolLabel,
}: {
  binding: ModelAlias;
  provider: UpstreamProvider;
  bindings: readonly ModelAlias[];
  providers: readonly UpstreamProvider[];
  selectedKey: string;
  onSelect: (key: string) => void;
  protocolLabel: (protocol: UpstreamProtocol) => string;
}): ReactNode {
  const { t } = useI18n();
  const [value, setValue] = useState<UpstreamProtocol | "">(binding.protocolOverride ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState(false);
  const options = useMemo(
    () => PROTOCOLS.map((protocol) => ({ value: protocol, label: protocolLabel(protocol) })),
    [protocolLabel]
  );
  const bindingOptions = bindings.map((item) => {
    const owner = providers.find((candidate) => candidate.id === item.providerId);
    return { value: bindingKey(item), label: `${owner?.name ?? item.providerId} · ${item.alias}` };
  });
  const effective = effectiveModelProtocol(provider, binding);
  const changed = value !== (binding.protocolOverride ?? "");

  const save = async (): Promise<void> => {
    if (!changed || busy) return;
    setBusy(true);
    setError(false);
    try {
      // Reload the selected binding so a concurrent metadata edit is preserved.
      const latest = (await listModels(binding.providerId)).find((item) => item.alias === binding.alias);
      if (latest === undefined) throw new Error("missing-model");
      const next: ModelAlias = { ...latest };
      if (value === "") next.protocolOverride = undefined;
      else next.protocolOverride = value;
      await updateModel(next);
    } catch {
      setError(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-2">
      {bindings.length > 1 && (
        <label className="block">
          <span className="mb-1 block text-[10.5px] text-fg-faint">{t("models.protocolBinding")}</span>
          <Select ariaLabel={t("models.protocolBinding")} value={selectedKey || bindingKey(binding)} onValueChange={onSelect} options={bindingOptions} />
        </label>
      )}
      <label className="block">
        <span className="mb-1 block text-[10.5px] text-fg-faint">{t("models.protocol")}</span>
        <Select
          ariaLabel={t("models.protocol")}
          value={value}
          onValueChange={(next) => setValue(next as UpstreamProtocol | "")}
          options={[{ value: "", label: t("models.protocolFollowProvider") }, ...options]}
          disabled={busy}
        />
      </label>
      <p className="text-[10.5px] text-fg-faint">
        {t("models.protocolEffective", { protocol: protocolLabel(effective), provider: provider.name })}
      </p>
      <div className="flex items-center justify-between gap-2">
        <span className={cn("text-[10.5px]", error ? "text-danger" : "text-fg-faint")}>
          {error ? t("models.protocolSaveFailed") : value === "" ? t("models.protocolInherited") : t("models.protocolOverridden")}
        </span>
        <button
          type="button"
          disabled={!changed || busy}
          onClick={() => void save()}
          className="rounded-[8px] bg-accent px-3 py-1.5 text-[11px] text-accent-fg disabled:opacity-50"
        >
          {busy ? t("models.protocolSaving") : t("models.protocolSave")}
        </button>
      </div>
    </div>
  );
}

function bindingKey(binding: ModelAlias | undefined): string {
  return binding === undefined ? "" : `${binding.providerId}\u0000${binding.alias}`;
}

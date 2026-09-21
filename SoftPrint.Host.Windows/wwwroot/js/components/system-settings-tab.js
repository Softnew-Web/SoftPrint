export function bindSystemSettingsTab({ api }) {
  const byId = (id) => document.getElementById(id);
  const message = byId("systemSettingsMsg");

  async function loadDiagnose() {
    const target = byId("diagnoseReport");
    if (!target) return;
    try {
      const report = await api("/api/diagnose");
      const checks = (report.checks || [])
        .map((check) => `${check.available ? "ok" : "off"}  ${check.name}${check.detail ? ` — ${check.detail}` : ""}`)
        .join("\n");
      target.textContent = [
        `Edição: ${report.edition}`,
        `SO: ${report.os}`,
        `Arquitetura: ${report.architecture}`,
        `Runtime: ${report.runtime}`,
        `Impressão: ${report.printingBackend}`,
        `Impressoras: ${report.printerCount}`,
        "",
        checks,
      ].join("\n");
    } catch (err) {
      target.textContent = err.message || "Falha ao ler o diagnóstico.";
    }
  }

  async function load() {
    try {
      const value = await api("/api/system-settings");
      byId("sysPoll").value = value.pollIntervalMs;
      byId("sysRetention").value = value.retentionDays;
      byId("sysBackup").value = value.backupIntervalMinutes;
      byId("sysSound").checked = !!value.soundEnabled;
      byId("sysLog").checked = !!value.logToFile;
      byId("sysWebhookUrl").value = value.webhookUrl || "";
      byId("sysWebhookSecret").value = value.hasWebhookSecret ? "********" : "";
      byId("sysWebhookTimeout").value = value.webhookTimeoutMs;
      byId("sysWebhookRetry").value = value.webhookRetrySeconds;
      byId("sysWebhookMax").value = value.webhookMaxRetries;
      byId("sysEventRetention").value = value.eventLogRetentionDays;
      byId("sysNetworkTimeout").value = value.networkScanTimeoutMs;
      if (byId("sysTelemetry")) byId("sysTelemetry").checked = !!value.telemetryEnabled;
      if (byId("sysTelemetryUrl")) byId("sysTelemetryUrl").value = value.telemetryUrl || "";
      message.textContent = "Configurações carregadas.";
    } catch (err) {
      message.textContent = err.message;
      message.className = "text-sm text-bad";
    }
    await loadDiagnose();
  }

  byId("btnSaveSystem")?.addEventListener("click", async () => {
    try {
      await api("/api/system-settings", {
        method: "PUT",
        body: JSON.stringify({
          pollIntervalMs: Number(byId("sysPoll").value),
          retentionDays: Number(byId("sysRetention").value),
          backupIntervalMinutes: Number(byId("sysBackup").value),
          soundEnabled: byId("sysSound").checked,
          logToFile: byId("sysLog").checked,
          webhookUrl: byId("sysWebhookUrl").value.trim(),
          webhookSecret: byId("sysWebhookSecret").value,
          webhookTimeoutMs: Number(byId("sysWebhookTimeout").value),
          webhookRetrySeconds: Number(byId("sysWebhookRetry").value),
          webhookMaxRetries: Number(byId("sysWebhookMax").value),
          eventLogRetentionDays: Number(byId("sysEventRetention").value),
          networkScanTimeoutMs: Number(byId("sysNetworkTimeout").value),
          telemetryEnabled: !!byId("sysTelemetry")?.checked,
          telemetryUrl: byId("sysTelemetryUrl")?.value.trim() || "",
        }),
      });
      message.textContent = "Configurações salvas. Reinicie o SoftPrint para aplicar tudo.";
      message.className = "text-sm text-sea-glow";
    } catch (err) {
      message.textContent = err.message;
      message.className = "text-sm text-bad";
    }
  });

  byId("btnDiagnose")?.addEventListener("click", () => loadDiagnose());

  return { load };
}

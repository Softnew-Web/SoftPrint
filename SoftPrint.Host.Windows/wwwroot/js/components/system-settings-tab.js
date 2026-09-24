export function bindSystemSettingsTab({ api }) {
  const byId = (id) => document.getElementById(id);
  const message = byId("systemSettingsMsg");

  async function loadDiagnose() {
    const target = byId("diagnoseReport");
    const checksHost = byId("diagnoseChecks");
    if (!target) return;
    try {
      const report = await api("/api/diagnose");
      if (checksHost) {
        checksHost.innerHTML = (report.checks || [])
          .map((check) => {
            const ok = !!check.available;
            return `<div class="rounded-lg border px-3 py-2 ${
              ok ? "border-sea/40 bg-sea/10" : "border-bad/40 bg-bad/10"
            }">
              <p class="font-medium ${ok ? "text-sea-glow" : "text-bad"}">${ok ? "OK" : "Atenção"} · ${escapeHtml(
                check.name
              )}</p>
              <p class="text-xs text-mist mt-0.5">${escapeHtml(check.detail || (ok ? "disponível" : "indisponível"))}</p>
            </div>`;
          })
          .join("");
      }
      target.textContent = [
        `Edição: ${report.edition}`,
        `SO: ${report.os}`,
        `Arquitetura: ${report.architecture}`,
        `Runtime: ${report.runtime}`,
        `Impressão: ${report.printingBackend}`,
        `Impressoras: ${report.printerCount}`,
      ].join("\n");
    } catch (err) {
      if (checksHost) checksHost.innerHTML = "";
      target.textContent = err.message || "Falha ao ler o diagnóstico.";
    }
  }

  function escapeHtml(value) {
    return String(value ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll('"', "&quot;");
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
      if (byId("sysPrinterRoutes")) byId("sysPrinterRoutes").value = value.printerRoutes || "";
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
          printerRoutes: byId("sysPrinterRoutes")?.value.trim() || "",
        }),
      });
      message.textContent = "Configurações salvas. Notificações e rotas valem na hora; outras opções podem pedir reinício.";
      message.className = "text-sm text-sea-glow";
    } catch (err) {
      message.textContent = err.message;
      message.className = "text-sm text-bad";
    }
  });

  byId("btnDiagnose")?.addEventListener("click", () => loadDiagnose());

  byId("btnSupportBundle")?.addEventListener("click", async () => {
    const msg = byId("supportBundleMsg") || message;
    try {
      msg.textContent = "Gerando pacote…";
      msg.className = "text-xs text-mist";
      const res = await api("/api/support/bundle", { method: "POST" });
      msg.textContent = res.message
        ? `${res.message} → ${res.fileName || res.path || ""}`
        : `Pacote: ${res.fileName || res.path}`;
      msg.className = "text-xs text-sea-glow";
    } catch (err) {
      msg.textContent = err.message || "Falha ao gerar pacote.";
      msg.className = "text-xs text-bad";
    }
  });

  byId("btnExportBackup")?.addEventListener("click", async () => {
    try {
      const data = await api("/api/backup/export");
      const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = `softprint-backup-${new Date().toISOString().slice(0, 10)}.json`;
      a.click();
      message.textContent = "Backup exportado (sem a chave da API completa).";
      message.className = "text-sm text-sea-glow";
    } catch (err) {
      message.textContent = err.message;
      message.className = "text-sm text-bad";
    }
  });

  byId("btnImportBackup")?.addEventListener("click", () => byId("backupFile")?.click());
  byId("backupFile")?.addEventListener("change", async (e) => {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    if (!confirm("Importar este backup? Substitui configurações deste computador.")) return;
    try {
      const text = await file.text();
      JSON.parse(text);
      await api("/api/backup/import", { method: "POST", body: text });
      message.textContent = "Backup importado. Recarregando…";
      message.className = "text-sm text-sea-glow";
      await load();
    } catch (err) {
      message.textContent = err.message || "Falha ao importar.";
      message.className = "text-sm text-bad";
    }
  });

  return { load };
}

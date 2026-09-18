import { feedback, openDlg, state } from "../state.js";
import { escapeHtml } from "../api.js";
import { buildSettingsPayload } from "../settings-payload.js";
import { setApiHint } from "./stats.js";

export function bindConnectTab({ api, apiKey }) {
  const inboxFolder = document.getElementById("inboxFolder");
  const inboxEnabled = document.getElementById("inboxEnabled");
  const deleteInboxAfterPrint = document.getElementById("deleteInboxAfterPrint");
  const inboxStatus = document.getElementById("inboxStatus");
  const inboxFiles = document.getElementById("inboxFiles");

  const markInboxDirty = () => {
    state.inboxDirty = true;
    if (inboxStatus) {
      inboxStatus.textContent = "Alterações da pasta ainda não salvas.";
      inboxStatus.className = "text-sm text-warn min-h-[1.25rem]";
    }
  };

  const endpoint = document.getElementById("endpoint");
  if (endpoint) endpoint.value = location.origin + "/api/jobs";
  setApiHint(location.origin);

  document.getElementById("btnCopyEndpoint").addEventListener("click", () => {
    navigator.clipboard.writeText(document.getElementById("endpoint").value);
    feedback("Endereço copiado.");
  });
  document.getElementById("btnCopyKey").addEventListener("click", () => {
    navigator.clipboard.writeText(apiKey);
    feedback("Chave copiada. Use só no sistema autorizado.");
  });
  document.getElementById("btnOpenLogs").addEventListener("click", async () => {
    try {
      await api("/api/events/open-folder", { method: "POST", body: "{}" });
      feedback("Pasta de logs aberta.");
    } catch (err) {
      feedback(err.message, true);
    }
  });
  document.getElementById("btnEvents").addEventListener("click", async () => {
    try {
      const data = await api("/api/events");
      const lines = [`Eventos de ${data.day}`, "─".repeat(40)];
      const events = (data.events || []).slice().reverse().slice(0, 80);
      if (!events.length) lines.push("Nenhum evento neste dia.");
      else
        events.forEach((e) => {
          lines.push(
            `${new Date(e.at).toLocaleTimeString()}  ${e.reference}  [${e.status}]  via ${
              e.delivery || "—"
            }`
          );
          if (e.error) lines.push(`    erro: ${e.error}`);
        });
      openDlg("Eventos do dia", lines.join("\n"));
    } catch (err) {
      feedback(err.message, true);
    }
  });
  document.getElementById("btnDiscover").addEventListener("click", async () => {
    try {
      feedback("Varrendo rede local…");
      const found = await api("/api/printers/discover", { method: "POST", body: "{}" });
      if (!found.length) return feedback("Nenhuma porta de impressão encontrada.", true);
      openDlg(
        "Impressoras na rede",
        found.map((f) => `${f.address}:${f.port} — ${f.hint}`).join("\n")
      );
      feedback(`${found.length} destino(s) encontrado(s).`);
    } catch (err) {
      feedback(err.message, true);
    }
  });

  if (inboxFolder) inboxFolder.addEventListener("input", () => {
    if (!inboxFolder.value.trim()) {
      if (inboxEnabled) inboxEnabled.checked = false;
      if (deleteInboxAfterPrint) deleteInboxAfterPrint.checked = false;
    }
    markInboxDirty();
  });
  if (inboxEnabled) inboxEnabled.addEventListener("change", markInboxDirty);
  if (deleteInboxAfterPrint) deleteInboxAfterPrint.addEventListener("change", markInboxDirty);

  const btnClearInbox = document.getElementById("btnClearInbox");
  if (btnClearInbox) btnClearInbox.addEventListener("click", () => {
    if (inboxFolder) inboxFolder.value = "";
    if (inboxEnabled) inboxEnabled.checked = false;
    if (deleteInboxAfterPrint) deleteInboxAfterPrint.checked = false;
    markInboxDirty();
    if (inboxStatus) inboxStatus.textContent = "Pasta removida do formulário. Clique em Salvar pasta.";
  });

  const btnBrowse = document.getElementById("btnBrowseInbox");
  if (btnBrowse) btnBrowse.addEventListener("click", async () => {
    try {
      if (inboxStatus) {
        inboxStatus.textContent = "Abrindo seletor de pasta…";
        inboxStatus.className = "text-sm text-sea-glow min-h-[1.25rem]";
      }
      const result = await api("/api/inbox/browse", { method: "POST", body: "{}" });
      if (result.cancelled || !result.folder) {
        if (inboxStatus) inboxStatus.textContent = "Seleção cancelada.";
        return;
      }
      if (inboxFolder) inboxFolder.value = result.folder;
      markInboxDirty();
      if (inboxStatus) inboxStatus.textContent = "Pasta selecionada. Clique em Salvar pasta.";
    } catch (err) {
      if (inboxStatus) {
        inboxStatus.textContent = err.message;
        inboxStatus.className = "text-sm text-bad min-h-[1.25rem]";
      }
    }
  });

  const btnOpenInbox = document.getElementById("btnOpenInbox");
  if (btnOpenInbox) btnOpenInbox.addEventListener("click", async () => {
    try {
      if (inboxFolder?.value.trim() && inboxFolder.value.trim() !== (state.applied?.inboxFolder || "")) {
        feedback("Salve a pasta antes de abrir.", true);
        return;
      }
      await api("/api/inbox/open", { method: "POST", body: "{}" });
      feedback("Pasta de entrada aberta.");
    } catch (err) {
      feedback(err.message, true);
    }
  });

  const btnSaveInbox = document.getElementById("btnSaveInbox");
  if (btnSaveInbox) btnSaveInbox.addEventListener("click", async () => {
    if (!state.applied) {
      if (inboxStatus) {
        inboxStatus.textContent = "Configurações ainda não carregadas. Clique em Atualizar.";
        inboxStatus.className = "text-sm text-bad min-h-[1.25rem]";
      }
      return;
    }
    try {
      state.applied = await api("/api/settings", {
        method: "PUT",
        body: JSON.stringify(
          buildSettingsPayload({
            inboxFolder: inboxFolder?.value.trim() || "",
            inboxEnabled: !!inboxEnabled?.checked,
            deleteInboxAfterPrint: !!deleteInboxAfterPrint?.checked,
          })
        ),
      });
      state.inboxDirty = false;
      applyInboxFromSettings();
      await refreshInbox();
      if (inboxStatus) {
        inboxStatus.textContent = state.applied.inboxFolder
          ? state.applied.inboxEnabled
            ? "Pasta salva — vigilância ativa."
            : "Pasta salva — vigilância desativada."
          : "Pasta removida.";
        inboxStatus.className = "text-sm text-sea-glow min-h-[1.25rem]";
      }
    } catch (err) {
      feedback(err.message, true);
    }
  });

  function applyInboxFromSettings() {
    if (!state.applied || state.inboxDirty) return;
    if (inboxFolder) inboxFolder.value = state.applied.inboxFolder || "";
    if (inboxEnabled) inboxEnabled.checked = !!state.applied.inboxEnabled;
    if (deleteInboxAfterPrint) deleteInboxAfterPrint.checked = !!state.applied.deleteInboxAfterPrint;
  }

  async function refreshInbox() {
    try {
      const snap = await api("/api/inbox");
      if (!state.inboxDirty) {
        const bits = [];
        if (snap.enabled) bits.push("vigilância ativa");
        else bits.push("vigilância off");
        if (snap.deleteAfterPrint) bits.push("apaga após imprimir");
        bits.push(`${snap.fileCount} arquivo(s)`);
        inboxStatus.textContent = bits.join(" · ");
        inboxStatus.className = "text-sm text-sea-glow min-h-[1.25rem]";
      }

      if (!snap.files?.length) {
        inboxFiles.textContent = snap.folder
          ? "Pasta vazia — aguardando PDF/imagens."
          : "Nenhuma pasta configurada. Escolha e salve acima.";
        return;
      }
      inboxFiles.innerHTML = snap.files
        .map(
          (f) => {
            const info = inboxStatusInfo(f.status);
            const detail = f.errorReason || f.error || "";
            return `<div class="py-1.5 border-b border-ink-line/60 last:border-0">
              <div class="flex items-center justify-between gap-2">
                <span class="text-paper truncate">${escapeHtml(f.name)}</span>
                <span class="${info.css} rounded-full px-2 py-0.5 text-[11px] shrink-0">${info.label}</span>
              </div>
              <div class="text-mist text-xs">${escapeHtml(f.kind)} · ${formatBytes(f.size)}${
                detail ? ` · <span class="text-bad" title="${escapeHtml(detail)}">${escapeHtml(detail)}</span>` : ""
              }</div>
            </div>`;
          }
        )
        .join("");
    } catch (err) {
      if (!state.inboxDirty) {
        inboxStatus.textContent = err.message;
        inboxStatus.className = "text-sm text-bad min-h-[1.25rem]";
      }
    }
  }

  function formatBytes(n) {
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  }

  function inboxStatusInfo(status) {
    return {
      queued: { label: "Na fila", css: "bg-warn/15 text-warn" },
      printing: { label: "Imprimindo", css: "bg-sea/20 text-sea-glow" },
      sent: { label: "Enviado", css: "bg-good/15 text-good" },
      simulated: { label: "Simulado", css: "bg-good/15 text-good" },
      failed: { label: "Falhou", css: "bg-bad/15 text-bad" },
      copying: { label: "Copiando", css: "bg-ink-line text-mist" },
      waiting: { label: "Aguardando", css: "bg-ink-line text-mist" },
    }[status] || { label: status || "Aguardando", css: "bg-ink-line text-mist" };
  }

  return { applyInboxFromSettings, refreshInbox };
}

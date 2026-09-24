import { feedback, openDlg, openDlgHtml, state } from "../state.js";
import { escapeHtml, statusLabel } from "../api.js";
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
      const events = (data.events || []).slice().reverse().slice(0, 100);
      const dayLabel = formatDayLabel(data.day);
      if (!events.length) {
        openDlgHtml(
          `Eventos · ${dayLabel}`,
          `<p class="text-mist text-center py-8">Nenhum evento neste dia.</p>`
        );
        return;
      }
      const summary = buildEventsSummary(events);
      openDlgHtml(
        `Eventos · ${dayLabel}`,
        `${renderSummaryBar(summary)}
         <div class="mt-3 space-y-1.5">${events.map((e) => renderLogEntry(e)).join("")}</div>`
      );
    } catch (err) {
      feedback(err.message, true);
    }
  });

  const eventsDay = document.getElementById("eventsDay");
  const eventsFilter = document.getElementById("eventsFilter");
  const eventsPanelList = document.getElementById("eventsPanelList");
  const eventsPanelSummary = document.getElementById("eventsPanelSummary");
  let statusChip = "";
  if (eventsDay && !eventsDay.value) {
    eventsDay.value = new Date().toISOString().slice(0, 10);
  }

  document.querySelectorAll(".events-chip").forEach((btn) => {
    btn.addEventListener("click", () => {
      statusChip = btn.dataset.status ?? "";
      document.querySelectorAll(".events-chip").forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      loadEventsPanel();
    });
  });

  async function loadEventsPanel() {
    if (!eventsPanelList) return;
    eventsPanelList.innerHTML = `<p class="px-2 py-6 text-center text-mist animate-pulse">Carregando eventos…</p>`;
    if (eventsPanelSummary) {
      eventsPanelSummary.classList.add("hidden");
      eventsPanelSummary.innerHTML = "";
    }
    try {
      const day = eventsDay?.value || "";
      const q = (eventsFilter?.value || "").trim().toLowerCase();
      const url = day ? `/api/events?day=${encodeURIComponent(day)}` : "/api/events";
      const data = await api(url);
      let events = (data.events || []).slice().reverse();
      if (q) {
        events = events.filter((e) => {
          const hay = [
            e.reference, e.status, e.error, e.errorReason, e.errorWhere,
            e.delivery, e.deliveryDetail, e.printerName, e.jobType, e.contentKind,
          ]
            .filter(Boolean)
            .join(" ")
            .toLowerCase();
          return hay.includes(q);
        });
      }
      if (statusChip === "ok") {
        events = events.filter((e) => isOkStatus(e.status));
      } else if (statusChip === "uncertain") {
        events = events.filter((e) => !isOkStatus(e.status));
      }
      const allForSummary = events;
      events = events.slice(0, 150);
      if (!events.length) {
        eventsPanelList.innerHTML = `<p class="px-2 py-8 text-center text-mist">Nenhum evento com esse filtro.</p>`;
        return;
      }
      const summary = buildEventsSummary(allForSummary);
      if (eventsPanelSummary) {
        eventsPanelSummary.classList.remove("hidden");
        eventsPanelSummary.innerHTML = renderSummaryChips(summary, data.day);
      }
      eventsPanelList.innerHTML = events.map((e) => renderLogEntry(e)).join("");
    } catch (err) {
      eventsPanelList.innerHTML = `<p class="px-2 py-6 text-center text-bad">${escapeHtml(
        err.message || "Falha ao ler logs."
      )}</p>`;
    }
  }

  document.getElementById("btnLoadEventsPanel")?.addEventListener("click", loadEventsPanel);
  eventsDay?.addEventListener("change", loadEventsPanel);
  let filterTimer = 0;
  eventsFilter?.addEventListener("input", () => {
    clearTimeout(filterTimer);
    filterTimer = setTimeout(loadEventsPanel, 280);
  });
  eventsFilter?.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      clearTimeout(filterTimer);
      loadEventsPanel();
    }
  });
  setTimeout(loadEventsPanel, 600);

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
        inboxStatus.textContent = inboxSaveMessage(state.applied);
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

  return { applyInboxFromSettings, refreshInbox };
}

function inboxSaveMessage(applied) {
  if (!applied?.inboxFolder) return "Pasta removida.";
  if (applied.inboxEnabled) return "Pasta salva — vigilância ativa.";
  return "Pasta salva — vigilância desativada.";
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

function isOkStatus(status) {
  const s = (status || "").toLowerCase();
  return s === "sent" || s === "simulated" || s === "started" || s === "host-stop"
    || s === "user-closed" || s === "update-restart";
}

function isLifecycleEvent(e) {
  return (e.contentKind || "").toLowerCase() === "lifecycle"
    || (e.eventType || "").toLowerCase() === "app.lifecycle"
    || (e.delivery || "").toLowerCase() === "lifecycle";
}

function eventStatusInfo(status) {
  const s = (status || "").toLowerCase();
  if (s === "sent")
    return { label: statusLabel.sent || "Enviado", bar: "bg-sea-glow", badge: "bg-sea/20 text-sea-glow", tone: "ok" };
  if (s === "simulated")
    return { label: statusLabel.simulated || "Simulado", bar: "bg-good", badge: "bg-good/15 text-good", tone: "ok" };
  if (s === "uncertain")
    return { label: statusLabel.uncertain || "Conferir", bar: "bg-bad", badge: "bg-bad/20 text-bad", tone: "bad" };
  if (s === "processing")
    return { label: statusLabel.processing || "Enviando", bar: "bg-sea", badge: "bg-sea/15 text-sea-glow", tone: "mid" };
  if (s === "pending")
    return { label: statusLabel.pending || "Na fila", bar: "bg-warn", badge: "bg-warn/15 text-warn", tone: "mid" };
  if (s === "started")
    return { label: "Iniciado", bar: "bg-good", badge: "bg-good/15 text-good", tone: "ok" };
  if (s === "user-closed")
    return { label: "Fechado", bar: "bg-sea", badge: "bg-sea/15 text-sea-glow", tone: "ok" };
  if (s === "windows-shutdown")
    return { label: "Windows desligou", bar: "bg-warn", badge: "bg-warn/15 text-warn", tone: "mid" };
  if (s === "windows-logoff")
    return { label: "Logoff Windows", bar: "bg-warn", badge: "bg-warn/15 text-warn", tone: "mid" };
  if (s === "crashed")
    return { label: "Crash", bar: "bg-bad", badge: "bg-bad/20 text-bad", tone: "bad" };
  if (s === "unclean-exit")
    return { label: "Parou de súbito", bar: "bg-bad", badge: "bg-bad/20 text-bad", tone: "bad" };
  if (s === "update-restart")
    return { label: "Atualização", bar: "bg-sea", badge: "bg-sea/15 text-sea-glow", tone: "ok" };
  if (s === "host-stop")
    return { label: "Encerrado", bar: "bg-mist", badge: "bg-ink-line text-mist", tone: "mid" };
  return {
    label: statusLabel[s] || status || "—",
    bar: "bg-mist",
    badge: "bg-ink-line text-mist",
    tone: "mid",
  };
}

function formatEventTime(at) {
  try {
    return new Date(at).toLocaleTimeString("pt-BR", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
  } catch {
    return "—";
  }
}

function formatDayLabel(day) {
  if (!day) return "hoje";
  try {
    const [y, m, d] = String(day).split("-").map(Number);
    return new Date(y, m - 1, d).toLocaleDateString("pt-BR", {
      weekday: "short",
      day: "2-digit",
      month: "short",
      year: "numeric",
    });
  } catch {
    return String(day);
  }
}

function buildEventsSummary(events) {
  let ok = 0;
  let bad = 0;
  for (const e of events) {
    if (isLifecycleEvent(e)) {
      const s = (e.status || "").toLowerCase();
      if (s === "crashed" || s === "unclean-exit") bad += 1;
      else ok += 1;
      continue;
    }
    if (isOkStatus(e.status)) ok += 1;
    else bad += 1;
  }
  return { total: events.length, ok, bad };
}

function renderSummaryChips(summary, day) {
  return `
    <span class="rounded-full bg-ink border border-ink-line px-2.5 py-0.5 text-paper">${summary.total} evento${summary.total === 1 ? "" : "s"}</span>
    <span class="rounded-full bg-good/10 border border-good/30 px-2.5 py-0.5 text-good">${summary.ok} OK</span>
    <span class="rounded-full bg-bad/10 border border-bad/30 px-2.5 py-0.5 text-bad">${summary.bad} conferir</span>
    <span class="text-mist ml-auto">${escapeHtml(formatDayLabel(day))}</span>`;
}

function renderSummaryBar(summary) {
  return `<div class="flex flex-wrap gap-2 text-[11px] pb-2 border-b border-ink-line">${renderSummaryChips(summary)}</div>`;
}

function renderLogEntry(e) {
  if (isLifecycleEvent(e))
    return renderLifecycleEntry(e);

  const info = eventStatusInfo(e.status);
  const time = formatEventTime(e.at);
  const ref = e.reference || "—";
  const chips = buildLogChips(e);
  const errHtml = renderLogErrorDetails(e);
  const toneClass = logToneClass(info.tone);

  return `<article class="log-entry ${toneClass}">
    <div class="${info.bar}" aria-hidden="true"></div>
    <div class="px-3 py-2.5 min-w-0">
      <div class="flex items-start justify-between gap-2">
        <div class="min-w-0">
          <p class="text-paper font-medium text-sm truncate" title="${escapeHtml(ref)}">${escapeHtml(ref)}</p>
          <p class="text-[11px] text-mist mt-0.5 tabular-nums">${escapeHtml(time)}</p>
        </div>
        <span class="shrink-0 rounded-full px-2 py-0.5 text-[10px] font-semibold tracking-wide ${info.badge}">${escapeHtml(info.label)}</span>
      </div>
      ${chips.length ? `<div class="mt-2 flex flex-wrap gap-1">${chips.join("")}</div>` : ""}
      ${errHtml}
    </div>
  </article>`;
}

function renderLifecycleEntry(e) {
  const info = eventStatusInfo(e.status);
  const time = formatEventTime(e.at);
  const title = e.error || "Evento do SoftPrint";
  const detail = e.errorReason || "";
  const toneClass = logToneClass(info.tone);
  const detailHtml = detail
    ? `<p class="mt-1.5 text-[11px] text-paper/80 whitespace-pre-wrap break-words">${escapeHtml(detail)}</p>`
    : "";

  return `<article class="log-entry ${toneClass}">
    <div class="${info.bar}" aria-hidden="true"></div>
    <div class="px-3 py-2.5 min-w-0">
      <div class="flex items-start justify-between gap-2">
        <div class="min-w-0">
          <p class="text-paper font-medium text-sm">${escapeHtml(title)}</p>
          <p class="text-[11px] text-mist mt-0.5 tabular-nums">${escapeHtml(time)}</p>
        </div>
        <span class="shrink-0 rounded-full px-2 py-0.5 text-[10px] font-semibold tracking-wide ${info.badge}">${escapeHtml(info.label)}</span>
      </div>
      ${detailHtml}
    </div>
  </article>`;
}

function logToneClass(tone) {
  if (tone === "bad") return "is-bad";
  if (tone === "ok") return "is-ok";
  return "";
}

function buildLogChips(e) {
  const chips = [];
  if (e.printerName) chips.push(metaChip("Impressora", e.printerName));
  if (e.delivery) chips.push(metaChip("Entrega", e.delivery));
  if (e.jobType && e.jobType !== "default") chips.push(metaChip("Tipo", e.jobType));
  if (e.contentKind) chips.push(metaChip("Conteúdo", e.contentKind));
  if (e.deliveryDetail) chips.push(metaChip("Detalhe", e.deliveryDetail));
  return chips;
}

function renderLogErrorDetails(e) {
  if (!(e.error || e.errorReason || e.errorWhere)) return "";
  const rows = [];
  if (e.error) rows.push(errorDetailRow("O quê", e.error));
  if (e.errorWhere) rows.push(errorDetailRow("Onde", e.errorWhere));
  if (e.errorReason) rows.push(errorDetailRow("Por quê", e.errorReason));
  return `<details class="mt-2 group">
        <summary class="cursor-pointer text-bad/90 text-[11px] hover:text-bad list-none flex items-center gap-1">
          <span class="opacity-70 group-open:rotate-90 transition-transform inline-block">▸</span>
          Detalhe do problema
        </summary>
        <div class="mt-1.5 rounded-lg bg-bad/10 border border-bad/25 px-2.5 py-2 text-[11px] text-paper/90 space-y-1">
          ${rows.join("")}
        </div>
      </details>`;
}

function errorDetailRow(label, value) {
  return `<p><span class="text-mist">${escapeHtml(label)}:</span> ${escapeHtml(value)}</p>`;
}

function metaChip(kind, text) {
  const title = escapeHtml(kind + ": " + text);
  return `<span class="log-meta-chip truncate" title="${title}"><span class="text-mist/70">${escapeHtml(kind)}</span> ${escapeHtml(text)}</span>`;
}

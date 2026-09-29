import { feedback, openDlg, openDlgHtml, state } from "../state.js";
import { escapeHtml, statusLabel } from "../api.js";
import { buildSettingsPayload } from "../settings-payload.js";
import { setApiHint } from "./stats.js";

export function bindConnectTab({ api }) {
  const endpoint = document.getElementById("endpoint");
  if (endpoint) endpoint.value = location.origin + "/api/jobs";
  setApiHint(location.origin);

  document.getElementById("btnCopyEndpoint").addEventListener("click", () => {
    navigator.clipboard.writeText(document.getElementById("endpoint").value);
    feedback("Endereço copiado.");
  });
  document.getElementById("btnCopyKey").addEventListener("click", async () => {
    try {
      const data = await api("/api/connect/key");
      if (!data?.apiKey) throw new Error("Chave indisponível.");
      await navigator.clipboard.writeText(data.apiKey);
      feedback("Chave copiada. Use só no sistema autorizado.");
    } catch (err) {
      feedback(err.message || "Não foi possível copiar a chave.", true);
    }
  });
  document.getElementById("btnRotateKey")?.addEventListener("click", async () => {
    if (!confirm("Gerar nova chave de API? Integrações com a chave antiga param de autenticar.")) return;
    try {
      const data = await api("/api/connect/rotate-key", { method: "POST", body: "{}" });
      feedback(data.message || "Chave regenerada.");
    } catch (err) {
      feedback(err.message || "Falha ao regenerar a chave.", true);
    }
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

  // ── Impressoras (Tab 1) e Pastas por impressora (Tab 2) ─────────────────────

  const inboxCards = document.getElementById("inboxCards");
  const inboxDetail = document.getElementById("inboxDetail");
  const inboxDetailTitle = document.getElementById("inboxDetailTitle");
  const inboxEntryLabel = document.getElementById("inboxEntryLabel");
  const inboxEntryPrinter = document.getElementById("inboxEntryPrinter");
  const inboxEntryStatus = document.getElementById("inboxEntryStatus");
  const inboxEntryPaperSize = document.getElementById("inboxEntryPaperSize");
  const inboxEntryCustomPaperRow = document.getElementById("inboxEntryCustomPaperRow");
  const inboxEntryPaperW = document.getElementById("inboxEntryPaperW");
  const inboxEntryPaperH = document.getElementById("inboxEntryPaperH");
  const inboxEntryImageFit = document.getElementById("inboxEntryImageFit");
  const inboxEntryLandscape = document.getElementById("inboxEntryLandscape");
  const inboxEntryScale = document.getElementById("inboxEntryScale");
  const inboxEntryScaleLabel = document.getElementById("inboxEntryScaleLabel");
  const inboxEntryCopies = document.getElementById("inboxEntryCopies");
  const inboxEntryRateLimit = document.getElementById("inboxEntryRateLimit");
  const inboxEntryWebhookUrl = document.getElementById("inboxEntryWebhookUrl");
  const inboxFolderList = document.getElementById("inboxFolderList");

  let _entries = [];
  let _printerStatuses = {}; // name → {isOffline, status}
  let _selectedId = null;
  let _entryDirty = false;
  let _openingDetail = false; // flag para ignorar markDirty durante openDetail

  function getRevision() { return state.applied?.revision ?? 1; }

  function setEntryStatus(msg, isError = false) {
    if (!inboxEntryStatus) return;
    inboxEntryStatus.textContent = msg;
    inboxEntryStatus.className = `text-sm min-h-[1.25rem] ${isError ? "text-bad" : "text-sea-glow"}`;
  }

  function markDirty() {
    if (_openingDetail) return;
    _entryDirty = true;
    setEntryStatus("Alterações não salvas.");
  }

  function toggleCustomPaperRow() {
    const isCustom = inboxEntryPaperSize?.value === "custom";
    if (inboxEntryCustomPaperRow) inboxEntryCustomPaperRow.classList.toggle("hidden", !isCustom);
  }

  inboxEntryPaperSize?.addEventListener("change", () => { toggleCustomPaperRow(); markDirty(); });
  inboxEntryScale?.addEventListener("input", () => {
    if (inboxEntryScaleLabel) inboxEntryScaleLabel.textContent = `${inboxEntryScale.value}%`;
    markDirty();
  });

  async function loadPrinterOptions(selectedPrinter) {
    if (!inboxEntryPrinter) return;
    try {
      const names = await api("/api/printers/names");
      inboxEntryPrinter.innerHTML =
        `<option value="">— padrão do SoftPrint —</option>` +
        (names || []).map((n) => `<option value="${escapeHtml(n)}"${n === selectedPrinter ? " selected" : ""}>${escapeHtml(n)}</option>`).join("");
      if (!names?.includes(selectedPrinter)) inboxEntryPrinter.value = "";
    } catch {
      inboxEntryPrinter.innerHTML = `<option value="">— padrão do SoftPrint —</option>`;
    }
  }

  // Abre detalhe da impressora (Tab 1) — sem campos de pasta.
  async function openDetail(entry) {
    _openingDetail = true;
    _selectedId = entry?.id ?? null;
    _entryDirty = false;
    if (inboxDetailTitle) inboxDetailTitle.textContent = entry ? (entry.label || "Sem nome") : "Nova impressora";
    if (inboxEntryLabel) inboxEntryLabel.value = entry?.label ?? "";
    // Setar todos os campos antes de disparar events, para evitar race com syncCustomRow
    if (inboxEntryPaperSize) inboxEntryPaperSize.value = entry?.paperSize ?? "a4";
    if (inboxEntryPaperW) inboxEntryPaperW.value = entry?.paperWidthMm ?? 210;
    if (inboxEntryPaperH) inboxEntryPaperH.value = entry?.paperHeightMm ?? 297;
    if (inboxEntryImageFit) inboxEntryImageFit.value = entry?.imageFit ?? "contain";
    if (inboxEntryLandscape) inboxEntryLandscape.checked = !!entry?.paperLandscape;
    const scale = entry?.imageScalePercent ?? 100;
    if (inboxEntryScale) inboxEntryScale.value = scale;
    if (inboxEntryScaleLabel) inboxEntryScaleLabel.textContent = `${scale}%`;
    if (inboxEntryCopies) inboxEntryCopies.value = entry?.copies ?? 1;
    if (inboxEntryRateLimit) inboxEntryRateLimit.value = entry?.rateLimitPerMinute ?? 0;
    if (inboxEntryWebhookUrl) inboxEntryWebhookUrl.value = entry?.webhookUrl ?? "";
    toggleCustomPaperRow();
    // Dispara change depois de todos os campos preenchidos para redrawPreview
    inboxEntryPaperSize?.dispatchEvent(new Event("change"));
    _openingDetail = false;
    setEntryStatus("");
    await loadPrinterOptions(entry?.printerName ?? "");
    if (inboxDetail) inboxDetail.classList.remove("hidden");
  }

  function closeDetail() {
    _selectedId = null; _entryDirty = false;
    if (inboxDetail) inboxDetail.classList.add("hidden");
  }

  // Cartões de impressoras (Tab 1).
  function renderCards() {
    if (!inboxCards) return;
    if (!_entries.length) {
      inboxCards.innerHTML = `<p class="text-sm text-mist">Nenhuma impressora cadastrada. Clique em <strong class="text-paper/80">+ Nova</strong> para começar.</p>`;
      return;
    }
    inboxCards.innerHTML = _entries.map((e) => {
      const active = e.id === _selectedId ? "border-sea bg-sea/10" : "border-ink-line bg-ink hover:bg-ink-line/50";
      const paper = e.paperSize ? `<span class="text-[11px] text-mist/70">${escapeHtml(e.paperSize.toUpperCase())}</span>` : "";
      const printerHint = e.printerName
        ? `<span class="text-[11px] text-sea-glow truncate">${escapeHtml(e.printerName)}</span>`
        : `<span class="text-[11px] text-mist">impressora padrão</span>`;

      // Dot de status da impressora
      const ps = _printerStatuses[e.printerName];
      const dotColor = !e.printerName
        ? "bg-mist/40"
        : ps?.isOffline
          ? "bg-bad animate-pulse"
          : ps
            ? "bg-good"
            : "bg-warn/70";
      const dotTitle = !e.printerName
        ? "impressora padrão do sistema"
        : ps?.isOffline
          ? "offline"
          : ps
            ? "online"
            : "não encontrada";

      // Badge de arquivos aguardando
      const fileCount = e.fileCount || 0;
      const fileBadge = fileCount > 0
        ? `<span class="ml-auto shrink-0 rounded-full bg-sea/20 text-sea-glow text-[10px] px-1.5 py-0.5 leading-none">${fileCount}</span>`
        : "";

      return `<button type="button" data-entry-id="${e.id}"
        class="inbox-card flex flex-col gap-1 rounded-xl border ${active} px-4 py-3 text-left transition-colors min-w-[140px] max-w-[220px]">
        <div class="flex items-center gap-1.5 w-full min-w-0">
          <span class="w-2 h-2 rounded-full shrink-0 ${dotColor}" title="${dotTitle}"></span>
          <span class="font-medium text-sm text-paper truncate">${escapeHtml(e.label || "Sem nome")}</span>
          ${fileBadge}
        </div>
        ${printerHint}
        ${paper}
      </button>`;
    }).join("");
    inboxCards.querySelectorAll(".inbox-card").forEach((btn) => {
      btn.addEventListener("click", () => {
        const entry = _entries.find((e) => e.id === btn.dataset.entryId);
        if (entry) openDetail(entry);
      });
    });
  }

  // Lista de pastas por impressora (Tab 2).
  function renderFolderList() {
    if (!inboxFolderList) return;
    // Não reconstrói se o usuário está digitando em algum campo da lista
    if (inboxFolderList.contains(document.activeElement)) return;
    if (!_entries.length) {
      inboxFolderList.innerHTML = `<p class="text-sm text-mist">Nenhuma impressora cadastrada ainda. Adicione em "Suas impressoras".</p>`;
      return;
    }
    inboxFolderList.innerHTML = _entries.map((e) => {
      const folderVal = escapeHtml(e.folder || "");
      const enabledChk = e.enabled ? "checked" : "";
      const deleteChk = e.deleteAfterPrint ? "checked" : "";
      const hasFolder = !!e.folder;
      const badge = e.enabled && hasFolder
        ? `<span class="text-[10px] rounded-full bg-good/15 text-good px-2 py-0.5">Ativa</span>`
        : hasFolder
          ? `<span class="text-[10px] rounded-full bg-warn/15 text-warn px-2 py-0.5">Com pasta</span>`
          : `<span class="text-[10px] rounded-full bg-ink-line text-mist px-2 py-0.5">Sem pasta</span>`;

      // Status da impressora
      const ps = _printerStatuses[e.printerName];
      const printerDotColor = !e.printerName ? "bg-mist/40"
        : ps?.isOffline ? "bg-bad animate-pulse"
        : ps ? "bg-good"
        : "bg-warn/70";
      const printerDotTitle = !e.printerName ? "padrão"
        : ps?.isOffline ? "offline" : ps ? "online" : "não encontrada";

      // Arquivos na pasta
      const files = Array.isArray(e.files) ? e.files : [];
      const filesHtml = files.length
        ? `<div class="border-t border-ink-line/40 pt-2 space-y-1">
            <p class="text-[10px] uppercase tracking-wider text-mist mb-1">${files.length} arquivo(s) na pasta</p>
            ${files.slice(0, 10).map((f) => {
              const si = inboxStatusInfo(f.status);
              return `<div class="flex items-center gap-2 min-w-0">
                <span class="shrink-0 rounded-full text-[10px] px-1.5 py-0.5 leading-none ${si.css}">${si.label}</span>
                <span class="truncate text-xs text-paper/80">${escapeHtml(f.name)}</span>
                <span class="ml-auto shrink-0 text-[10px] text-mist tabular-nums">${formatBytes(f.size)}</span>
              </div>`;
            }).join("")}
            ${files.length > 10 ? `<p class="text-[10px] text-mist">+ ${files.length - 10} arquivo(s)…</p>` : ""}
          </div>`
        : "";

      return `<div class="rounded-xl border border-ink-line bg-ink/40 p-3 space-y-2" data-folder-id="${e.id}">
        <div class="flex items-center gap-2 flex-wrap">
          ${badge}
          <span class="font-medium text-sm text-paper">${escapeHtml(e.label || "Sem nome")}</span>
          <span class="w-1.5 h-1.5 rounded-full shrink-0 ${printerDotColor}" title="Impressora: ${escapeHtml(printerDotTitle)}"></span>
          <span class="text-[11px] text-mist">${escapeHtml(e.printerName || "impressora padrão")}</span>
        </div>
        <div class="flex gap-2">
          <input type="text" value="${folderVal}" placeholder="C:\\caminho\\para\\pasta"
            class="folder-path-input flex-1 min-w-0 rounded-lg bg-ink border border-ink-line px-3 py-1.5 text-sm" />
          <button type="button" class="btn-browse-folder shrink-0 rounded-lg bg-ink-line hover:bg-ink-line/70 px-3 py-1.5 text-sm">Escolher…</button>
          <button type="button" class="btn-open-folder shrink-0 rounded-lg bg-ink-line hover:bg-ink-line/70 px-3 py-1.5 text-sm${hasFolder ? "" : " hidden"}">Abrir</button>
        </div>
        <div class="flex flex-wrap items-center gap-4 text-sm">
          <label class="inline-flex items-center gap-2"><input type="checkbox" class="folder-enabled accent-sea" ${enabledChk} /> Vigiar e imprimir</label>
          <label class="inline-flex items-center gap-2"><input type="checkbox" class="folder-delete accent-sea" ${deleteChk} /> Apagar após imprimir</label>
          <button type="button" class="btn-save-folder ml-auto rounded-lg bg-sea hover:bg-sea-deep text-white font-semibold px-4 py-1.5 text-sm">Salvar</button>
          <span class="folder-status text-xs text-mist min-h-[1rem]"></span>
        </div>
        ${filesHtml}
      </div>`;
    }).join("");

    // Bind delegated handlers
    inboxFolderList.querySelectorAll("[data-folder-id]").forEach((row) => {
      const id = row.dataset.folderId;
      const entry = _entries.find((e) => e.id === id);
      if (!entry) return;
      const pathInput = row.querySelector(".folder-path-input");
      const enabledChk = row.querySelector(".folder-enabled");
      const deleteChk = row.querySelector(".folder-delete");
      const saveBtn = row.querySelector(".btn-save-folder");
      const statusEl = row.querySelector(".folder-status");
      const browseBtn = row.querySelector(".btn-browse-folder");
      const openBtn = row.querySelector(".btn-open-folder");

      function setStatus(msg, isError = false) {
        if (statusEl) { statusEl.textContent = msg; statusEl.className = `folder-status text-xs min-h-[1rem] ${isError ? "text-bad" : "text-sea-glow"}`; }
      }

      saveBtn?.addEventListener("click", async () => {
        const body = {
          id: entry.id,
          label: entry.label,
          printerName: entry.printerName || "",
          folder: pathInput?.value.trim() || "",
          enabled: !!enabledChk?.checked,
          deleteAfterPrint: !!deleteChk?.checked,
          expectedRevision: getRevision(),
          hasCustomSettings: true,
          imageFit: entry.imageFit || "contain",
          imageScalePercent: entry.imageScalePercent ?? 100,
          paperSize: entry.paperSize || "a4",
          paperWidthMm: entry.paperWidthMm ?? 210,
          paperHeightMm: entry.paperHeightMm ?? 297,
          paperLandscape: !!entry.paperLandscape,
          copies: entry.copies ?? 1,
          webhookUrl: entry.webhookUrl ?? null,
        };
        try {
          const saved = await api(`/api/inbox/entries/${entry.id}`, { method: "PUT", body: JSON.stringify(body) });
          if (saved?.revision && state.applied) state.applied.revision = saved.revision;
          setStatus("Salvo.");
          await refreshInbox();
          // Re-apply status after HTML rebuild (renderFolderList destroys the old element)
          const rebuiltRow = inboxFolderList?.querySelector(`[data-folder-id="${entry.id}"]`);
          const rebuiltStatus = rebuiltRow?.querySelector(".folder-status");
          if (rebuiltStatus) {
            rebuiltStatus.textContent = "Salvo.";
            rebuiltStatus.className = "folder-status text-xs min-h-[1rem] text-sea-glow";
            setTimeout(() => { if (rebuiltStatus.textContent === "Salvo.") rebuiltStatus.textContent = ""; }, 2000);
          }
        } catch (err) {
          setStatus(err.message || "Erro ao salvar.", true);
        }
      });

      browseBtn?.addEventListener("click", async () => {
        try {
          const result = await api(`/api/inbox/entries/${entry.id}/browse`, { method: "POST", body: "{}" });
          if (!result.cancelled && result.folder && pathInput) pathInput.value = result.folder;
        } catch (err) {
          setStatus(err.message || "Falha ao selecionar pasta.", true);
        }
      });

      openBtn?.addEventListener("click", async () => {
        try {
          await api(`/api/inbox/entries/${entry.id}/open`, { method: "POST", body: "{}" });
          feedback("Pasta aberta.");
        } catch (err) { feedback(err.message, true); }
      });
    });
  }

  async function refreshInbox() {
    try {
      const [snap, printers] = await Promise.all([
        api("/api/inbox"),
        api("/api/printers").catch(() => []),
      ]);
      _entries = Array.isArray(snap) ? snap : [];
      _printerStatuses = Object.fromEntries(
        (printers || []).map((p) => [p.name, p])
      );
      renderCards();
      renderFolderList();
    } catch (err) {
      if (inboxCards)
        inboxCards.innerHTML = `<p class="text-sm text-bad">${escapeHtml(err.message || "Falha ao carregar impressoras.")}</p>`;
    }
  }

  document.getElementById("btnNewInboxEntry")?.addEventListener("click", () => openDetail(null));
  document.getElementById("btnCloseInboxDetail")?.addEventListener("click", closeDetail);

  [inboxEntryLabel, inboxEntryPrinter,
   inboxEntryPaperW, inboxEntryPaperH, inboxEntryImageFit, inboxEntryLandscape,
   inboxEntryCopies, inboxEntryRateLimit, inboxEntryWebhookUrl].forEach((el) => {
    el?.addEventListener("input", markDirty);
    el?.addEventListener("change", markDirty);
  });

  // Salvar impressora (Tab 1): preserva folder/enabled/delete existentes.
  document.getElementById("btnSaveInboxEntry")?.addEventListener("click", async () => {
    const existing = _entries.find((e) => e.id === _selectedId);
    const body = {
      id: _selectedId || null,
      label: inboxEntryLabel?.value.trim() || "",
      printerName: inboxEntryPrinter?.value || "",
      folder: existing?.folder ?? "",
      enabled: existing?.enabled ?? false,
      deleteAfterPrint: existing?.deleteAfterPrint ?? false,
      expectedRevision: getRevision(),
      hasCustomSettings: true,
      imageFit: inboxEntryImageFit?.value || "contain",
      imageScalePercent: parseInt(inboxEntryScale?.value || "100", 10),
      paperSize: inboxEntryPaperSize?.value || "a4",
      paperWidthMm: parseFloat(inboxEntryPaperW?.value || "210"),
      paperHeightMm: parseFloat(inboxEntryPaperH?.value || "297"),
      paperLandscape: !!inboxEntryLandscape?.checked,
      copies: Math.max(1, Math.min(99, parseInt(inboxEntryCopies?.value || "1", 10))),
      rateLimitPerMinute: Math.max(0, Math.min(1000, parseInt(inboxEntryRateLimit?.value || "0", 10))),
      webhookUrl: inboxEntryWebhookUrl?.value.trim() || null,
    };
    try {
      let saved;
      if (_selectedId) {
        saved = await api(`/api/inbox/entries/${_selectedId}`, { method: "PUT", body: JSON.stringify(body) });
      } else {
        saved = await api("/api/inbox/entries", { method: "POST", body: JSON.stringify(body) });
      }
      if (saved?.revision && state.applied) state.applied.revision = saved.revision;
      _entryDirty = false;
      await refreshInbox();
      if (!_selectedId) {
        const newEntry = _entries.find((e) => e.label === body.label) || _entries[_entries.length - 1];
        if (newEntry) { _selectedId = newEntry.id; renderCards(); }
      }
      setEntryStatus("Impressora salva.");
    } catch (err) {
      setEntryStatus(err.message || "Erro ao salvar.", true);
    }
  });

  document.getElementById("btnRemoveInboxEntry")?.addEventListener("click", async () => {
    if (!_selectedId) return;
    const entry = _entries.find((e) => e.id === _selectedId);
    if (!confirm(`Remover a impressora "${entry?.label || "esta"}"? A pasta física não será apagada.`)) return;
    try {
      await api(`/api/inbox/entries/${_selectedId}?expectedRevision=${getRevision()}`, { method: "DELETE" });
      closeDetail();
      await refreshInbox();
      feedback("Impressora removida.");
    } catch (err) {
      setEntryStatus(err.message || "Erro ao remover.", true);
    }
  });

  function applyInboxFromSettings() {}

  return { applyInboxFromSettings, refreshInbox };
}

// ── Helpers de UI ─────────────────────────────────────────────────────────────

function formatBytes(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

function inboxStatusInfo(status) {
  return {
    queued:    { label: "Na fila",    css: "bg-warn/15 text-warn" },
    printing:  { label: "Imprimindo", css: "bg-sea/20 text-sea-glow" },
    sent:      { label: "Enviado",    css: "bg-good/15 text-good" },
    simulated: { label: "Simulado",   css: "bg-good/15 text-good" },
    failed:    { label: "Falhou",     css: "bg-bad/15 text-bad" },
    copying:   { label: "Copiando",   css: "bg-ink-line text-mist" },
    waiting:   { label: "Aguardando", css: "bg-ink-line text-mist" },
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

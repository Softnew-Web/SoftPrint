import { state, feedback } from "../state.js";
import { escapeHtml } from "../api.js";
import { drawPaperPreview, resolvePaperMm, PAPER_PRESETS } from "../image-layout.js";
import { buildSettingsPayload } from "../settings-payload.js";
import { loadPdfPreview } from "../pdf-preview.js";
import { modeLabel, renderStats } from "./stats.js";

export function bindPrinterTab({ api, onSaved }) {
  const printers = document.getElementById("printers"); // hidden compat select
  const inboxEntryPrinter = document.getElementById("inboxEntryPrinter");
  const simulation = document.getElementById("simulation");
  const paused = document.getElementById("paused");
  const startup = document.getElementById("startup");
  // Preview reads from entry paper fields (per-entry settings)
  const imageFit = document.getElementById("inboxEntryImageFit");
  const imageScale = document.getElementById("inboxEntryScale");
  const imageScaleLabel = document.getElementById("inboxEntryScaleLabel");
  const paperSize = document.getElementById("inboxEntryPaperSize");
  const paperWidthMm = document.getElementById("inboxEntryPaperW");
  const paperHeightMm = document.getElementById("inboxEntryPaperH");
  const paperLandscape = document.getElementById("inboxEntryLandscape");
  const customPaperRow = document.getElementById("inboxEntryCustomPaperRow");
  const msg = document.getElementById("settingsMsg");
  const summary = document.getElementById("printerSummary");
  const previewMeta = document.getElementById("previewMeta");
  const canvas = document.getElementById("printPreview");
  const pdfControls = document.getElementById("pdfPreviewControls");
  const pdfPageLabel = document.getElementById("pdfPageLabel");
  const pdfZoom = document.getElementById("pdfZoom");
  const previewFile = document.getElementById("previewFile");

  let previewImage = null;
  let previewPdf = false;
  let pdfDocument = null;
  let pdfPage = 1;
  let objectUrl = null;
  let previewMargins = null;
  let marginRequest = 0;

  const syncModeStat = () => {
    renderStats({
      mode: modeLabel({
        paused: !!paused?.checked,
        simulation: !!simulation?.checked,
      }),
    });
  };

  const syncCustomRow = () => {
    if (!customPaperRow || !paperSize) return;
    const isCustom = paperSize.value === "custom";
    customPaperRow.classList.toggle("hidden", !isCustom);
    if (!isCustom && paperWidthMm && paperHeightMm) {
      const preset = PAPER_PRESETS[paperSize.value] || PAPER_PRESETS.a4;
      paperWidthMm.value = String(preset.w);
      paperHeightMm.value = String(preset.h);
    }
  };

  const applyDefaultPaper = (paper) => {
    if (!paper) return false;
    const kind = paper.suggestedKind || "custom";
    const resolved = PAPER_PRESETS[kind] ? kind : "custom";
    if (paperSize) paperSize.value = resolved;
    if (paperWidthMm) paperWidthMm.value = String(paper.widthMm ?? PAPER_PRESETS[resolved]?.w ?? 210);
    if (paperHeightMm) paperHeightMm.value = String(paper.heightMm ?? PAPER_PRESETS[resolved]?.h ?? 297);
    if (paperLandscape) paperLandscape.checked = !!paper.landscape;
    syncCustomRow();
    return true;
  };

  const loadDefaultPaper = async (apiFn, { silent = false } = {}) => {
    const name = inboxEntryPrinter?.value || printers?.value || "";
    if (!name) {
      if (!silent) feedback("Selecione uma impressora primeiro.", true);
      return false;
    }
    try {
      const paper = await apiFn(`/api/printers/default-paper?printerName=${encodeURIComponent(name)}`);
      if (!applyDefaultPaper(paper)) return false;
      const label = paper.paperName
        ? `${paper.paperName} (${paper.widthMm}×${paper.heightMm} mm)`
        : `${paper.widthMm}×${paper.heightMm} mm`;
      if (!silent) feedback(`Papel da impressora: ${label}`);
      return true;
    } catch (err) {
      if (!silent) feedback(err.message || "Não foi possível ler o papel padrão.", true);
      return false;
    }
  };

  const markDirty = () => {
    syncModeStat();
    // Auto-save simulation and paused changes
    if (!state.applied) return;
    const { buildSettingsPayload: bp } = { buildSettingsPayload: (x) => x };
    import("../settings-payload.js").then(({ buildSettingsPayload }) => {
      api("/api/settings", {
        method: "PUT",
        body: JSON.stringify(buildSettingsPayload({
          printerName: state.applied.printerName || "",
          simulation: !!simulation?.checked,
          paused: !!paused?.checked,
          imageFit: state.applied.imageFit || "contain",
          imageScalePercent: state.applied.imageScalePercent ?? 100,
          paperSize: state.applied.paperSize || "a4",
          paperWidthMm: state.applied.paperWidthMm ?? 210,
          paperHeightMm: state.applied.paperHeightMm ?? 297,
          paperLandscape: !!state.applied.paperLandscape,
        })),
      }).then((saved) => {
        state.applied = saved;
        state.dirty = false;
        if (msg) {
          msg.textContent = `v${saved.revision}`;
          msg.className = "text-xs text-sea-glow leading-relaxed";
        }
        onSaved?.();
      }).catch((err) => feedback(err.message, true));
    });
  };

  // Auto-save simulation/paused on change
  ["simulation", "paused"].forEach((id) => {
    const el = document.getElementById(id);
    if (el) el.addEventListener("change", markDirty);
  });
  // Redraw preview when entry paper fields change (managed by connect-tab.js for dirty tracking)
  if (paperSize) paperSize.addEventListener("change", () => { syncCustomRow(); redrawPreview(); refreshPreviewMargins(); });
  if (paperLandscape) paperLandscape.addEventListener("change", () => { redrawPreview(); refreshPreviewMargins(); });
  if (imageFit) imageFit.addEventListener("change", () => redrawPreview());
  if (paperWidthMm) paperWidthMm.addEventListener("input", () => { redrawPreview(); refreshPreviewMargins(); });
  if (paperHeightMm) paperHeightMm.addEventListener("input", () => { redrawPreview(); refreshPreviewMargins(); });
  if (inboxEntryPrinter) {
    inboxEntryPrinter.addEventListener("change", async () => {
      await loadDefaultPaper(api, { silent: true });
      redrawPreview();
    });
  }

  document.getElementById("btnPrinterPaper")?.addEventListener("click", async () => {
    if (await loadDefaultPaper(api)) markDirty();
  });

  if (imageScale) {
    imageScale.addEventListener("input", () => {
      if (imageScaleLabel) imageScaleLabel.textContent = `${imageScale.value}%`;
      markDirty();
    });
  }

  if (startup) {
    startup.addEventListener("change", async (e) => {
      try {
        await api("/api/startup", {
          method: "POST",
          body: JSON.stringify({ enabled: e.target.checked }),
        });
        feedback("Inicialização com Windows atualizada.");
      } catch (err) {
        feedback(err.message, true);
      }
    });
  }

  if (previewFile) {
    // Preferir o botão do bootstrap; manter sync se o módulo carregar.
    previewFile.addEventListener("change", async () => {
      const file = previewFile.files?.[0];
      if (!file) return;
      await pdfDocument?.destroy();
      pdfDocument = null;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = URL.createObjectURL(file);
      previewPdf = file.type === "application/pdf" || file.name.toLowerCase().endsWith(".pdf");
      if (previewPdf) {
        try {
          previewImage = null;
          pdfPage = 1;
          pdfDocument = await loadPdfPreview(file);
          pdfControls?.classList.replace("hidden", "flex");
          await renderPdfPage();
        } catch (err) {
          previewPdf = false;
          pdfControls?.classList.replace("flex", "hidden");
          if (previewMeta) previewMeta.textContent = `PDF inválido ou protegido: ${err.message}`;
        }
        return;
      }
      pdfControls?.classList.replace("flex", "hidden");
      const img = new Image();
      img.onload = () => {
        previewImage = img;
        redrawPreview();
      };
      img.src = objectUrl;
    });
  }

  const btnPick = document.getElementById("btnPickPreview");
  if (btnPick && previewFile) {
    btnPick.addEventListener("click", () => previewFile.click());
  }

  const btnClear = document.getElementById("btnClearPreview");
  if (btnClear) {
    btnClear.addEventListener("click", async () => {
      await pdfDocument?.destroy();
      pdfDocument = null;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = null;
      previewImage = null;
      previewPdf = false;
      pdfControls?.classList.replace("flex", "hidden");
      if (previewFile) previewFile.value = "";
      redrawPreview();
    });
  }

  document.getElementById("btnPdfPrev")?.addEventListener("click", async () => {
    if (!pdfDocument || pdfPage <= 1) return;
    pdfPage--;
    await renderPdfPage();
  });
  document.getElementById("btnPdfNext")?.addEventListener("click", async () => {
    if (!pdfDocument || pdfPage >= pdfDocument.pageCount) return;
    pdfPage++;
    await renderPdfPage();
  });
  pdfZoom?.addEventListener("change", renderPdfPage);

  const btnLoad = document.getElementById("btnLoadPrinters");
  if (btnLoad) btnLoad.addEventListener("click", () => loadPrinters(api));

  const btnFindNet = document.getElementById("btnFindNetworkPrinters");
  const netHint = document.getElementById("networkPrinterHint");
  const netPanel = document.getElementById("networkPrinterPanel");
  const netList = document.getElementById("networkPrinterList");
  let discoveredHosts = [];

  function closeNetworkPanel({ keepHint = false } = {}) {
    discoveredHosts = [];
    if (netList) netList.innerHTML = "";
    netPanel?.classList.add("hidden");
    if (!keepHint && netHint) {
      netHint.textContent = "";
      netHint.classList.add("hidden");
    }
  }

  function renderNetworkHosts(hosts) {
    discoveredHosts = hosts;
    if (!netList || !netPanel) return;
    if (!hosts.length) {
      netPanel.classList.add("hidden");
      netList.innerHTML = "";
      return;
    }
    netPanel.classList.remove("hidden");
    netList.innerHTML = hosts
      .map(
        (h, i) => `<label class="flex items-center gap-2 rounded-lg px-2 py-1.5 hover:bg-ink cursor-pointer">
          <input type="checkbox" class="net-host-check accent-sea" data-idx="${i}" checked />
          <span class="truncate">${h.address}:${h.port} <span class="text-mist">— ${h.hint || "rede"}</span></span>
        </label>`
      )
      .join("");
  }

  document.getElementById("btnCloseNetworkPanel")?.addEventListener("click", () => {
    closeNetworkPanel();
    feedback("Busca de rede fechada.");
  });

  if (btnFindNet) {
    btnFindNet.addEventListener("click", async () => {
      // Já aberto → fecha (não fica preso na tela).
      if (netPanel && !netPanel.classList.contains("hidden")) {
        closeNetworkPanel();
        return;
      }
      try {
        btnFindNet.disabled = true;
        if (netHint) {
          netHint.classList.remove("hidden");
          netHint.textContent = "Varrendo a rede local (portas 9100/515/631)…";
        }
        feedback("Procurando impressoras na rede…");
        const found = await api("/api/printers/discover", { method: "POST", body: "{}" });
        const hosts = preferNetworkHosts(found || []);
        if (!hosts.length) {
          renderNetworkHosts([]);
          if (netHint) netHint.textContent = "Nenhuma impressora de rede encontrada. Confira se ela está ligada e na mesma rede.";
          return feedback("Nenhuma impressora de rede encontrada.", true);
        }

        renderNetworkHosts(hosts);
        if (netHint) netHint.textContent = `${hosts.length} encontrada(s). Marque as desejadas e clique em Instalar selecionadas.`;
        feedback(`${hosts.length} impressora(s) na rede — selecione quais instalar.`);
      } catch (err) {
        renderNetworkHosts([]);
        if (netHint) netHint.textContent = err.message || "Falha na busca.";
        feedback(err.message || "Falha na busca.", true);
      } finally {
        btnFindNet.disabled = false;
      }
    });
  }

  document.getElementById("btnNetworkSelectAll")?.addEventListener("click", () => {
    netList?.querySelectorAll(".net-host-check").forEach((el) => {
      el.checked = true;
    });
  });

  document.getElementById("btnInstallNetworkSelected")?.addEventListener("click", async () => {
    const idxs = [...(netList?.querySelectorAll(".net-host-check:checked") || [])]
      .map((el) => Number(el.dataset.idx))
      .filter((i) => Number.isInteger(i) && i >= 0 && i < discoveredHosts.length);
    if (!idxs.length) return feedback("Marque ao menos uma impressora.", true);

    const items = idxs.map((i) => {
      const h = discoveredHosts[i];
      return {
        address: h.address,
        port: h.port,
        name: `Impressora rede ${h.address}`,
      };
    });

    try {
      if (netHint) netHint.textContent = `Instalando ${items.length} impressora(s)…`;
      const result = await api("/api/printers/install-network-bulk", {
        method: "POST",
        body: JSON.stringify({ items }),
      });
      await loadPrinters(api);
      const firstName = (result.installed || []).find((x) => x.printerName)?.printerName;
      if (printers && firstName) printers.value = firstName;
      const fail = (result.errors || []).length;
      if (fail) {
        if (netHint) {
          netHint.textContent = `${result.count || 0} instalada(s), ${fail} falha(s). Selecione e salve.`;
        }
        feedback(`${result.count || 0} instalada(s), ${fail} com erro.`);
      } else {
        closeNetworkPanel({ keepHint: true });
        if (netHint) {
          netHint.classList.remove("hidden");
          netHint.textContent = `${result.count || 0} instalada(s). Selecione e clique em Salvar configurações.`;
        }
        feedback(`${result.count || 0} impressora(s) instalada(s).`);
      }
    } catch (err) {
      if (netHint) netHint.textContent = err.message || "Falha na instalação.";
      feedback(err.message || "Falha na instalação.", true);
    }
  });


  function currentPaper() {
    const requested = resolvePaperMm(
      paperSize?.value || "a4",
      paperWidthMm?.value || 210,
      paperHeightMm?.value || 297,
      !!paperLandscape?.checked
    );
    const actualW = Number(previewMargins?.pageWidthMm);
    const actualH = Number(previewMargins?.pageHeightMm);
    const hasActual = Number.isFinite(actualW) && Number.isFinite(actualH) && actualW > 10 && actualH > 10;
    return {
      w: hasActual ? actualW : requested.w,
      h: hasActual ? actualH : requested.h,
      requestedW: requested.w,
      requestedH: requested.h,
      honored: previewMargins?.matchesRequest !== false,
      margins: previewMargins ?? undefined,
    };
  }

  async function refreshPreviewMargins() {
    const request = ++marginRequest;
    try {
      const query = new URLSearchParams({
        printerName: inboxEntryPrinter?.value || printers?.value || "",
        paperSize: paperSize?.value || "a4",
        widthMm: paperWidthMm?.value || "210",
        heightMm: paperHeightMm?.value || "297",
        landscape: String(!!paperLandscape?.checked),
      });
      const metrics = await api(`/api/printers/page-metrics?${query}`);
      if (request !== marginRequest) return;
      previewMargins = metrics;
      redrawPreview();
    } catch {
      if (request !== marginRequest) return;
      previewMargins = null;
      redrawPreview();
    }
  }

  function redrawPreview() {
    if (!canvas) return;
    const fit = imageFit?.value || "contain";
    const scale = Number(imageScale?.value) || 100;
    const paper = currentPaper();
    drawPaperPreview(canvas, previewImage, fit, scale, paper);
    const paperLabel = `${fmt(paper.w)}×${fmt(paper.h)} mm`;
    if (!previewMeta) return;
    const mismatch = paper.honored === false
      ? ` · atenção: o driver usará ${paperLabel} (configurado ${fmt(paper.requestedW)}×${fmt(paper.requestedH)} mm)`
      : "";
    const tip =
      paper.requestedW <= 90 && paper.requestedH >= 150
        ? " · cupom estreito (faixa alta)"
        : "";
    if (previewImage) {
      const fitLabel = imageFit?.options?.[imageFit.selectedIndex]?.text || fit;
      const dimensions = previewImage.naturalWidth
        ? `${previewImage.naturalWidth}×${previewImage.naturalHeight}px`
        : `PDF página ${pdfPage}/${pdfDocument?.pageCount || 1}`;
      previewMeta.textContent = `${paperLabel} · ${dimensions} · ${fitLabel} · ${scale}%${mismatch}${tip}`;
    } else {
      previewMeta.textContent = `Papel da impressão: ${paperLabel}${paperLandscape?.checked ? " (paisagem)" : ""}${mismatch}${tip}`;
    }
  }

  async function renderPdfPage() {
    if (!pdfDocument) return;
    try {
      previewImage = await pdfDocument.render(pdfPage, Number(pdfZoom?.value || 1));
      if (pdfPageLabel) pdfPageLabel.textContent = `Página ${pdfPage} / ${pdfDocument.pageCount}`;
      redrawPreview();
    } catch (err) {
      if (err?.name !== "RenderingCancelledException" && previewMeta)
        previewMeta.textContent = `Falha ao renderizar PDF: ${err.message}`;
    }
  }

  async function loadPrinters(apiFn) {
    if (!printers) return;
    try {
      const list = await apiFn("/api/printers");
      const current = printers.value || state.applied?.printerName || "";
      printers.innerHTML =
        `<option value="">Selecionar impressora…</option>` +
        (list || [])
          .map(
            (p) =>
              `<option value="${escapeHtml(p.name)}">${escapeHtml(p.displayLabel || p.name)}</option>`
          )
          .join("");
      if (current) printers.value = current;
      const usb = list.filter((p) => /usb/i.test(p.connection)).length;
      const net = list.filter((p) => /rede/i.test(p.connection)).length;
      const cable = list.filter((p) => /cabo/i.test(p.connection)).length;
      if (summary) summary.textContent = `${list.length} impressoras · USB ${usb} · cabo ${cable} · rede ${net}`;
    } catch (err) {
      printers.innerHTML = `<option value="">Selecionar impressora…</option>`;
      if (summary) summary.textContent = "Não foi possível listar impressoras.";
      throw err;
    }
  }

  function applySettingsToForm() {
    if (!state.applied || state.dirty) return;
    if (simulation) simulation.checked = !!state.applied.simulation;
    if (paused) paused.checked = !!state.applied.paused;
    if (state.applied.printerName && printers) printers.value = state.applied.printerName;
    syncModeStat();
    if (msg) {
      msg.textContent = `v${state.applied.revision}`;
      msg.className = "text-xs text-sea-glow leading-relaxed";
    }
    redrawPreview();
  }

  syncCustomRow();
  redrawPreview();
  window.addEventListener("resize", () => {
    clearTimeout(window.__softprintPreviewResize);
    window.__softprintPreviewResize = setTimeout(() => redrawPreview(), 80);
  });

  return { loadPrinters, applySettingsToForm, redrawPreview };
}

function fmt(n) {
  const v = Number(n);
  return Number.isInteger(v) ? String(v) : v.toFixed(1);
}

function networkPortRank(port) {
  if (port === 9100) return 3;
  if (port === 631) return 2;
  return 1;
}

function preferNetworkHosts(found) {
  const byHost = new Map();
  for (const item of found) {
    if (!item?.reachable || !item.address) continue;
    const prev = byHost.get(item.address);
    const rank = networkPortRank(item.port);
    if (!prev || rank > prev.rank) byHost.set(item.address, { ...item, rank });
  }
  return [...byHost.values()];
}

import { state, feedback } from "../state.js";
import { escapeHtml } from "../api.js";
import { drawPaperPreview, resolvePaperMm, PAPER_PRESETS } from "../image-layout.js";
import { buildSettingsPayload } from "../settings-payload.js";
import { loadPdfPreview } from "../pdf-preview.js";

export function bindPrinterTab({ api, onSaved }) {
  const printers = document.getElementById("printers");
  const simulation = document.getElementById("simulation");
  const paused = document.getElementById("paused");
  const startup = document.getElementById("startup");
  const imageFit = document.getElementById("imageFit");
  const imageScale = document.getElementById("imageScale");
  const imageScaleLabel = document.getElementById("imageScaleLabel");
  const paperSize = document.getElementById("paperSize");
  const paperWidthMm = document.getElementById("paperWidthMm");
  const paperHeightMm = document.getElementById("paperHeightMm");
  const paperLandscape = document.getElementById("paperLandscape");
  const customPaperRow = document.getElementById("customPaperRow");
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
    const name = printers?.value || "";
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
    state.dirty = true;
    if (msg) {
      msg.textContent = "Alterações ainda não salvas.";
      msg.className = "text-sm text-warn leading-relaxed";
    }
    syncCustomRow();
    redrawPreview();
    refreshPreviewMargins();
  };

  ["simulation", "paused", "imageFit", "paperSize", "paperLandscape"].forEach((id) => {
    const el = document.getElementById(id);
    if (el) el.addEventListener("change", markDirty);
  });
  if (printers) {
    printers.addEventListener("change", async () => {
      markDirty();
      await loadDefaultPaper(api, { silent: true });
      markDirty();
    });
  }
  ["paperWidthMm", "paperHeightMm"].forEach((id) => {
    const el = document.getElementById(id);
    if (el) el.addEventListener("input", markDirty);
  });

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
  if (btnFindNet) {
    btnFindNet.addEventListener("click", async () => {
      try {
        btnFindNet.disabled = true;
        if (netHint) {
          netHint.classList.remove("hidden");
          netHint.textContent = "Varrendo a rede local (portas 9100/515/631)…";
        }
        feedback("Procurando impressoras na rede…");
        const found = await api("/api/printers/discover", { method: "POST", body: "{}" });
        const byHost = new Map();
        for (const item of found || []) {
          if (!item?.reachable || !item.address) continue;
          const prev = byHost.get(item.address);
          // Preferir 9100 (JetDirect/RAW), depois 631 (IPP), depois 515.
          const rank = item.port === 9100 ? 3 : item.port === 631 ? 2 : 1;
          if (!prev || rank > prev.rank) byHost.set(item.address, { ...item, rank });
        }
        const hosts = [...byHost.values()];
        if (!hosts.length) {
          if (netHint) netHint.textContent = "Nenhuma impressora de rede encontrada. Confira se ela está ligada e na mesma rede.";
          return feedback("Nenhuma impressora de rede encontrada.", true);
        }

        const lines = hosts.map((h, i) => `${i + 1}. ${h.address}:${h.port} — ${h.hint || "rede"}`);
        const choice = window.prompt(
          `Encontradas ${hosts.length} impressora(s) na rede.\n\n${lines.join("\n")}\n\nDigite o número para instalar no Windows (ou cancele):`,
          "1"
        );
        if (choice == null) {
          if (netHint) netHint.textContent = `${hosts.length} encontrada(s). Instalação cancelada.`;
          return;
        }
        const idx = Number(choice) - 1;
        if (!Number.isInteger(idx) || idx < 0 || idx >= hosts.length) {
          return feedback("Número inválido.", true);
        }
        const selected = hosts[idx];
        if (netHint) netHint.textContent = `Instalando ${selected.address}:${selected.port} no Windows…`;
        const installed = await api("/api/printers/install-network", {
          method: "POST",
          body: JSON.stringify({
            address: selected.address,
            port: selected.port,
            name: `Impressora rede ${selected.address}`,
          }),
        });
        if (!installed?.ok) {
          const err = installed?.error || "Falha ao instalar.";
          if (netHint) netHint.textContent = err;
          return feedback(err, true);
        }
        await loadPrinters(api);
        if (printers && installed.printerName) printers.value = installed.printerName;
        if (netHint) {
          netHint.textContent = `Instalada: ${installed.printerName}. Selecione e clique em Salvar configurações.`;
        }
        feedback(`Impressora instalada: ${installed.printerName}`);
      } catch (err) {
        if (netHint) netHint.textContent = err.message || "Falha na busca.";
        feedback(err.message || "Falha na busca.", true);
      } finally {
        btnFindNet.disabled = false;
      }
    });
  }

  const btnSave = document.getElementById("btnSaveSettings");
  if (btnSave) {
    btnSave.addEventListener("click", async () => {
      if (!state.applied) {
        if (msg) {
          msg.textContent = "Configurações ainda não carregadas. Clique em Atualizar.";
          msg.className = "text-sm text-bad leading-relaxed";
        }
        return;
      }
      try {
        state.applied = await api("/api/settings", {
          method: "PUT",
          body: JSON.stringify(
            buildSettingsPayload({
              printerName: printers?.value || "",
              simulation: !!simulation?.checked,
              paused: !!paused?.checked,
              imageFit: imageFit?.value || "contain",
              imageScalePercent: Number(imageScale?.value || 100),
              paperSize: paperSize?.value || "a4",
              paperWidthMm: Number(paperWidthMm?.value || 210),
              paperHeightMm: Number(paperHeightMm?.value || 297),
              paperLandscape: !!paperLandscape?.checked,
            })
          ),
        });
        state.dirty = false;
        applySettingsToForm();
        feedback("Configuração salva.");
        onSaved?.();
      } catch (err) {
        feedback(err.message, true);
      }
    });
  }

  function currentPaper() {
    return {
      ...resolvePaperMm(
      paperSize?.value || "a4",
      paperWidthMm?.value || 210,
      paperHeightMm?.value || 297,
      !!paperLandscape?.checked
      ),
      margins: previewMargins,
    };
  }

  async function refreshPreviewMargins() {
    const request = ++marginRequest;
    try {
      const query = new URLSearchParams({
        printerName: printers?.value || "",
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
    if (previewImage) {
      const fitLabel = imageFit?.options?.[imageFit.selectedIndex]?.text || fit;
      const dimensions = previewImage.naturalWidth
        ? `${previewImage.naturalWidth}×${previewImage.naturalHeight}px`
        : `PDF página ${pdfPage}/${pdfDocument?.pageCount || 1}`;
      previewMeta.textContent = `${paperLabel} · ${dimensions} · ${fitLabel} · ${scale}%`;
    } else {
      previewMeta.textContent = `Papel padrão: ${paperLabel}${paperLandscape?.checked ? " (paisagem)" : ""}`;
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
    if (imageFit) imageFit.value = state.applied.imageFit || "contain";
    const scale = state.applied.imageScalePercent ?? 100;
    if (imageScale) imageScale.value = String(scale);
    if (imageScaleLabel) imageScaleLabel.textContent = `${scale}%`;
    if (paperSize) paperSize.value = state.applied.paperSize || "a4";
    if (paperWidthMm) paperWidthMm.value = String(state.applied.paperWidthMm ?? 210);
    if (paperHeightMm) paperHeightMm.value = String(state.applied.paperHeightMm ?? 297);
    if (paperLandscape) paperLandscape.checked = !!state.applied.paperLandscape;
    syncCustomRow();
    refreshPreviewMargins();
    if (msg) {
      msg.textContent = `v${state.applied.revision} · ${
        state.applied.printerName || "Nenhuma impressora"
      }`;
      msg.className = "text-sm text-sea-glow leading-relaxed";
    }
    redrawPreview();
  }

  function fmt(n) {
    const v = Number(n);
    return Number.isInteger(v) ? String(v) : v.toFixed(1);
  }

  syncCustomRow();
  redrawPreview();
  window.addEventListener("resize", () => {
    clearTimeout(window.__softprintPreviewResize);
    window.__softprintPreviewResize = setTimeout(() => redrawPreview(), 80);
  });

  return { loadPrinters, applySettingsToForm, redrawPreview };
}

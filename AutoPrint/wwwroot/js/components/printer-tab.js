import { state, feedback } from "../state.js";
import { escapeHtml } from "../api.js";
import { drawPaperPreview, resolvePaperMm, PAPER_PRESETS } from "../image-layout.js";
import { buildSettingsPayload } from "../settings-payload.js";

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
  const pdfPreview = document.getElementById("pdfPreview");
  const previewFile = document.getElementById("previewFile");

  let previewImage = null;
  let previewPdf = false;
  let objectUrl = null;

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

  const markDirty = () => {
    state.dirty = true;
    if (msg) {
      msg.textContent = "Alterações ainda não salvas.";
      msg.className = "text-sm text-warn leading-relaxed";
    }
    syncCustomRow();
    redrawPreview();
  };

  ["simulation", "paused", "printers", "imageFit", "paperSize", "paperLandscape"].forEach((id) => {
    const el = document.getElementById(id);
    if (el) el.addEventListener("change", markDirty);
  });
  ["paperWidthMm", "paperHeightMm"].forEach((id) => {
    const el = document.getElementById(id);
    if (el) el.addEventListener("input", markDirty);
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
    previewFile.addEventListener("change", () => {
      const file = previewFile.files?.[0];
      if (!file) return;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = URL.createObjectURL(file);
      previewPdf = file.type === "application/pdf" || file.name.toLowerCase().endsWith(".pdf");
      if (previewPdf) {
        previewImage = null;
        if (canvas) canvas.classList.add("hidden");
        if (pdfPreview) {
          pdfPreview.src = objectUrl;
          pdfPreview.classList.remove("hidden");
        }
        if (previewMeta) previewMeta.textContent = `${file.name} · PDF usa as opções do driver do Windows; visualização aproximada.`;
        return;
      }
      if (canvas) canvas.classList.remove("hidden");
      if (pdfPreview) {
        pdfPreview.classList.add("hidden");
        pdfPreview.removeAttribute("src");
      }
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
    btnClear.addEventListener("click", () => {
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = null;
      previewImage = null;
      previewPdf = false;
      if (canvas) canvas.classList.remove("hidden");
      if (pdfPreview) {
        pdfPreview.classList.add("hidden");
        pdfPreview.removeAttribute("src");
      }
      if (previewFile) previewFile.value = "";
      redrawPreview();
    });
  }

  const btnLoad = document.getElementById("btnLoadPrinters");
  if (btnLoad) btnLoad.addEventListener("click", () => loadPrinters(api));

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
    return resolvePaperMm(
      paperSize?.value || "a4",
      paperWidthMm?.value || 210,
      paperHeightMm?.value || 297,
      !!paperLandscape?.checked
    );
  }

  function redrawPreview() {
    if (!canvas) return;
    if (previewPdf) return;
    const fit = imageFit?.value || "contain";
    const scale = Number(imageScale?.value) || 100;
    const paper = currentPaper();
    drawPaperPreview(canvas, previewImage, fit, scale, paper);
    const paperLabel = `${fmt(paper.w)}×${fmt(paper.h)} mm`;
    if (!previewMeta) return;
    if (previewImage) {
      const fitLabel = imageFit?.options?.[imageFit.selectedIndex]?.text || fit;
      previewMeta.textContent = `${paperLabel} · ${previewImage.naturalWidth}×${previewImage.naturalHeight}px · ${fitLabel} · ${scale}%`;
    } else {
      previewMeta.textContent = `Papel padrão: ${paperLabel}${paperLandscape?.checked ? " (paisagem)" : ""}`;
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

  return { loadPrinters, applySettingsToForm, redrawPreview };
}

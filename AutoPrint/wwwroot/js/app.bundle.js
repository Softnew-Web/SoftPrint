(() => {
  // AutoPrint/wwwroot/js/api.js
  function createApi(key) {
    const headers = {
      "X-AutoPrint-Key": key,
      "Content-Type": "application/json"
    };
    async function api2(path, opts = {}) {
      const res = await fetch(path, {
        ...opts,
        headers: { ...headers, ...opts.headers || {} }
      });
      if (!res.ok) {
        let msg = `Erro ${res.status}`;
        try {
          const j = await res.json();
          if (j.error) msg = j.error;
        } catch {
        }
        throw new Error(msg);
      }
      if (res.status === 204) return null;
      const text = await res.text();
      return text ? JSON.parse(text) : null;
    }
    return { api: api2, headers };
  }
  var statusLabel = {
    pending: "Na fila",
    processing: "Enviando",
    simulated: "Simulado",
    sent: "Enviado ao Windows",
    uncertain: "Conferir envio"
  };
  function escapeHtml(s) {
    return String(s ?? "").replace(
      /[&<>"']/g,
      (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]
    );
  }

  // AutoPrint/wwwroot/js/state.js
  var state = {
    jobs: [],
    selectedId: null,
    applied: null,
    dirty: false,
    inboxDirty: false
  };
  function feedback(msg, err = false) {
    const el = document.getElementById("feedback");
    if (!el) return;
    el.textContent = msg || "";
    el.className = `text-sm min-h-[1.25rem] ${err ? "text-bad" : "text-sea-glow"}`;
  }
  function openDlg(title, body) {
    document.getElementById("dlgTitle").textContent = title;
    document.getElementById("dlgBody").textContent = body;
    document.getElementById("dlg").showModal();
  }

  // AutoPrint/wwwroot/js/components/stats.js
  function renderStats({ mode, queue, done, bad, healthLine }) {
    document.getElementById("statMode").textContent = mode;
    document.getElementById("statQueue").textContent = queue;
    document.getElementById("statDone").textContent = done;
    document.getElementById("statBad").textContent = bad;
    document.getElementById("healthLine").textContent = healthLine;
  }
  function setConnection(ok) {
    const el = document.getElementById("connDot");
    if (ok) {
      el.innerHTML = `<span class="w-2.5 h-2.5 rounded-full bg-sea-glow animate-pulse"></span> AutoPrint conectado`;
      el.className = "inline-flex items-center gap-2 text-sm text-sea-glow";
    } else {
      el.innerHTML = `<span class="w-2.5 h-2.5 rounded-full bg-bad"></span> Sem conex\xE3o`;
      el.className = "inline-flex items-center gap-2 text-sm text-bad";
    }
  }

  // AutoPrint/wwwroot/js/image-layout.js
  var PAPER_PRESETS = {
    a4: { w: 210, h: 297, label: "A4" },
    a5: { w: 148, h: 210, label: "A5" },
    letter: { w: 215.9, h: 279.4, label: "Letter" },
    legal: { w: 215.9, h: 355.6, label: "Legal" },
    photo4x6: { w: 101.6, h: 152.4, label: "Foto 10\xD715" }
  };
  function resolvePaperMm(kind, widthMm, heightMm, landscape) {
    let w;
    let h;
    if (kind === "custom") {
      w = Math.min(1200, Math.max(20, Number(widthMm) || 210));
      h = Math.min(1200, Math.max(20, Number(heightMm) || 297));
    } else {
      const preset = PAPER_PRESETS[kind] || PAPER_PRESETS.a4;
      w = preset.w;
      h = preset.h;
    }
    return landscape ? { w: h, h: w } : { w, h };
  }
  function computeDestination(pageX, pageY, pageW, pageH, imageW, imageH, fit, scalePercent) {
    const scale = Math.min(200, Math.max(10, scalePercent)) / 100;
    const fitMode = (fit || "contain").toLowerCase();
    if (fitMode === "stretch") {
      return { x: pageX, y: pageY, w: pageW, h: pageH };
    }
    if (fitMode === "center") {
      const w2 = imageW * scale;
      const h2 = imageH * scale;
      return { x: pageX + (pageW - w2) / 2, y: pageY + (pageH - h2) / 2, w: w2, h: h2 };
    }
    if (fitMode === "cover") {
      const ratio2 = Math.max(pageW / imageW, pageH / imageH) * scale;
      const w2 = imageW * ratio2;
      const h2 = imageH * ratio2;
      return { x: pageX + (pageW - w2) / 2, y: pageY + (pageH - h2) / 2, w: w2, h: h2 };
    }
    const ratio = Math.min(pageW / imageW, pageH / imageH) * scale;
    const w = imageW * ratio;
    const h = imageH * ratio;
    return { x: pageX + (pageW - w) / 2, y: pageY + (pageH - h) / 2, w, h };
  }
  function drawPaperPreview(canvas, image, fit, scalePercent, paper) {
    const ctx = canvas.getContext("2d");
    const W = canvas.width;
    const H = canvas.height;
    ctx.clearRect(0, 0, W, H);
    ctx.fillStyle = "#121a22";
    ctx.fillRect(0, 0, W, H);
    const paperWmm = paper?.w || 210;
    const paperHmm = paper?.h || 297;
    const paperRatio = paperWmm / paperHmm;
    let paperH = H * 0.92;
    let paperW = paperH * paperRatio;
    if (paperW > W * 0.86) {
      paperW = W * 0.86;
      paperH = paperW / paperRatio;
    }
    const paperX = (W - paperW) / 2;
    const paperY = (H - paperH) / 2;
    const margin = Math.min(paperW, paperH) * 0.06;
    const area = {
      x: paperX + margin,
      y: paperY + margin,
      w: paperW - margin * 2,
      h: paperH - margin * 2
    };
    ctx.fillStyle = "#f4f7fa";
    ctx.strokeStyle = "#2a3644";
    ctx.lineWidth = 1;
    ctx.fillRect(paperX, paperY, paperW, paperH);
    ctx.strokeRect(paperX, paperY, paperW, paperH);
    ctx.strokeStyle = "#c5d0da";
    ctx.setLineDash([4, 4]);
    ctx.strokeRect(area.x, area.y, area.w, area.h);
    ctx.setLineDash([]);
    ctx.fillStyle = "#6b7c8c";
    ctx.font = "10px 'IBM Plex Sans', sans-serif";
    ctx.textAlign = "left";
    ctx.fillText("\xE1rea imprim\xEDvel aproximada", area.x + 4, area.y + 12);
    ctx.fillStyle = "#6b7c8c";
    ctx.font = "11px 'IBM Plex Sans', sans-serif";
    ctx.textAlign = "center";
    const sizeLabel = `${fmtMm(paperWmm)}\xD7${fmtMm(paperHmm)} mm`;
    ctx.fillText(sizeLabel, W / 2, Math.min(paperY + paperH + 16, H - 6));
    if (!image) {
      ctx.fillStyle = "#6b7c8c";
      ctx.font = "13px 'IBM Plex Sans', sans-serif";
      ctx.fillText("Escolha uma imagem para pr\xE9-visualizar", W / 2, H / 2);
      return { paperW, paperH, area, paperWmm, paperHmm };
    }
    const dest = computeDestination(area.x, area.y, area.w, area.h, image.width, image.height, fit, scalePercent);
    ctx.save();
    ctx.beginPath();
    ctx.rect(area.x, area.y, area.w, area.h);
    ctx.clip();
    ctx.drawImage(image, dest.x, dest.y, dest.w, dest.h);
    ctx.restore();
    const cropped = dest.x < area.x || dest.y < area.y || dest.x + dest.w > area.x + area.w || dest.y + dest.h > area.y + area.h;
    if (cropped) {
      ctx.fillStyle = "rgba(220, 38, 38, 0.12)";
      ctx.fillRect(area.x, area.y, area.w, area.h);
      ctx.strokeStyle = "#dc2626";
      ctx.setLineDash([6, 4]);
      ctx.strokeRect(area.x, area.y, area.w, area.h);
      ctx.setLineDash([]);
      ctx.fillStyle = "#b91c1c";
      ctx.textAlign = "center";
      ctx.font = "bold 11px 'IBM Plex Sans', sans-serif";
      ctx.fillText("partes fora da linha ser\xE3o cortadas", area.x + area.w / 2, area.y + area.h - 8);
    }
    ctx.strokeStyle = "#0d9488";
    ctx.lineWidth = 1.5;
    ctx.strokeRect(
      Math.max(dest.x, area.x),
      Math.max(dest.y, area.y),
      Math.min(dest.w, area.w),
      Math.min(dest.h, area.h)
    );
    return { paperW, paperH, area, dest, paperWmm, paperHmm };
  }
  function fmtMm(n) {
    const v = Number(n);
    return Number.isInteger(v) ? String(v) : v.toFixed(1);
  }

  // AutoPrint/wwwroot/js/settings-payload.js
  function buildSettingsPayload(overrides = {}) {
    const s = state.applied || {};
    return {
      printerName: s.printerName || "",
      simulation: !!s.simulation,
      paused: !!s.paused,
      imageFit: s.imageFit || "contain",
      imageScalePercent: s.imageScalePercent ?? 100,
      paperSize: s.paperSize || "a4",
      paperWidthMm: s.paperWidthMm ?? 210,
      paperHeightMm: s.paperHeightMm ?? 297,
      paperLandscape: !!s.paperLandscape,
      inboxFolder: s.inboxFolder || "",
      inboxEnabled: !!s.inboxEnabled,
      deleteInboxAfterPrint: !!s.deleteInboxAfterPrint,
      expectedRevision: s.revision,
      ...overrides
    };
  }

  // AutoPrint/wwwroot/js/components/printer-tab.js
  function bindPrinterTab({ api: api2, onSaved }) {
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
        msg.textContent = "Altera\xE7\xF5es ainda n\xE3o salvas.";
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
          await api2("/api/startup", {
            method: "POST",
            body: JSON.stringify({ enabled: e.target.checked })
          });
          feedback("Inicializa\xE7\xE3o com Windows atualizada.");
        } catch (err) {
          feedback(err.message, true);
        }
      });
    }
    if (previewFile) {
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
          if (previewMeta) previewMeta.textContent = `${file.name} \xB7 PDF usa as op\xE7\xF5es do driver do Windows; visualiza\xE7\xE3o aproximada.`;
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
    if (btnLoad) btnLoad.addEventListener("click", () => loadPrinters(api2));
    const btnSave = document.getElementById("btnSaveSettings");
    if (btnSave) {
      btnSave.addEventListener("click", async () => {
        if (!state.applied) {
          if (msg) {
            msg.textContent = "Configura\xE7\xF5es ainda n\xE3o carregadas. Clique em Atualizar.";
            msg.className = "text-sm text-bad leading-relaxed";
          }
          return;
        }
        try {
          state.applied = await api2("/api/settings", {
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
                paperLandscape: !!paperLandscape?.checked
              })
            )
          });
          state.dirty = false;
          applySettingsToForm();
          feedback("Configura\xE7\xE3o salva.");
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
      const paperLabel = `${fmt(paper.w)}\xD7${fmt(paper.h)} mm`;
      if (!previewMeta) return;
      if (previewImage) {
        const fitLabel = imageFit?.options?.[imageFit.selectedIndex]?.text || fit;
        previewMeta.textContent = `${paperLabel} \xB7 ${previewImage.naturalWidth}\xD7${previewImage.naturalHeight}px \xB7 ${fitLabel} \xB7 ${scale}%`;
      } else {
        previewMeta.textContent = `Papel padr\xE3o: ${paperLabel}${paperLandscape?.checked ? " (paisagem)" : ""}`;
      }
    }
    async function loadPrinters(apiFn) {
      if (!printers) return;
      try {
        const list = await apiFn("/api/printers");
        const current = printers.value || state.applied?.printerName || "";
        printers.innerHTML = `<option value="">Selecionar impressora\u2026</option>` + (list || []).map(
          (p) => `<option value="${escapeHtml(p.name)}">${escapeHtml(p.displayLabel || p.name)}</option>`
        ).join("");
        if (current) printers.value = current;
        const usb = list.filter((p) => /usb/i.test(p.connection)).length;
        const net = list.filter((p) => /rede/i.test(p.connection)).length;
        const cable = list.filter((p) => /cabo/i.test(p.connection)).length;
        if (summary) summary.textContent = `${list.length} impressoras \xB7 USB ${usb} \xB7 cabo ${cable} \xB7 rede ${net}`;
      } catch (err) {
        printers.innerHTML = `<option value="">Selecionar impressora\u2026</option>`;
        if (summary) summary.textContent = "N\xE3o foi poss\xEDvel listar impressoras.";
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
        msg.textContent = `v${state.applied.revision} \xB7 ${state.applied.printerName || "Nenhuma impressora"}`;
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

  // AutoPrint/wwwroot/js/components/monitor-tab.js
  function bindMonitorTab({ api: api2, onChanged }) {
    const body = document.getElementById("jobsBody");
    const steps = document.getElementById("steps");
    const err = document.getElementById("errorBox");
    const pipeline = document.getElementById("pipeline");
    document.getElementById("statusFilter").addEventListener("change", renderJobs);
    document.getElementById("refFilter").addEventListener("input", renderJobs);
    document.getElementById("btnSendTest").addEventListener("click", sendTest);
    document.getElementById("btnReprint").addEventListener("click", reprintSelected);
    document.getElementById("btnExplain").addEventListener("click", showFull);
    document.getElementById("btnExportJson").addEventListener("click", () => exportJobs(false));
    document.getElementById("btnExportCsv").addEventListener("click", () => exportJobs(true));
    function renderJobs() {
      const sf = document.getElementById("statusFilter").value;
      const rf = document.getElementById("refFilter").value.trim().toLowerCase();
      const filtered = state.jobs.filter(
        (j) => (!sf || j.status === sf) && (!rf || (j.reference || "").toLowerCase().includes(rf))
      );
      body.innerHTML = filtered.slice().reverse().map(
        (j) => `
      <tr data-id="${j.id}" class="job-row border-t border-ink-line cursor-pointer hover:bg-ink/60 ${state.selectedId === j.id ? "bg-ink" : ""} ${j.status === "uncertain" ? "text-bad" : ""}">
        <td class="px-3 py-2.5 font-medium">${escapeHtml(j.reference)}</td>
        <td class="px-3 py-2.5 text-mist">${escapeHtml(j.jobType || "default")}</td>
        <td class="px-3 py-2.5">${escapeHtml(statusLabel[j.status] || j.status)}</td>
        <td class="px-3 py-2.5 text-mist text-xs">${new Date(j.createdAt).toLocaleString()}</td>
      </tr>`
      ).join("") || `<tr><td colspan="4" class="px-3 py-8 text-mist text-center">Nenhum pedido nesta aba.</td></tr>`;
      body.querySelectorAll(".job-row").forEach((row) => {
        row.addEventListener("click", () => {
          state.selectedId = row.dataset.id;
          renderJobs();
        });
      });
      const sel = state.jobs.find((j) => j.id === state.selectedId) || filtered[filtered.length - 1] || null;
      if (sel) state.selectedId = sel.id;
      showTrace(sel);
    }
    function showTrace(job) {
      if (!job) {
        steps.innerHTML = `<p class="text-mist">Selecione um pedido para ver o passo a passo.</p>`;
        err.textContent = "Sem erro neste pedido.";
        err.className = "rounded-xl bg-ink border border-ink-line p-3 text-sm leading-relaxed text-mist";
        return;
      }
      const list = job.steps || [];
      steps.innerHTML = list.length ? list.map(
        (s, i) => `
      <div class="pl-4 timeline-line relative">
        <span class="absolute -left-[5px] top-1.5 w-2 h-2 rounded-full ${s.isError ? "bg-bad" : "bg-sea-glow"}"></span>
        <p class="font-medium ${s.isError ? "text-bad" : ""}">${i + 1}. ${escapeHtml(
          s.message
        )}</p>
        <p class="text-xs text-mist mt-0.5">Onde: ${escapeHtml(s.where)}</p>
        ${s.detail ? `<p class="text-xs text-mist/80 mt-0.5">${escapeHtml(s.detail)}</p>` : ""}
        <p class="text-[11px] text-mist/60 mt-1">${new Date(s.at).toLocaleTimeString()}</p>
      </div>`
      ).join("") : `<p class="text-mist">Ainda sem passos.</p>`;
      if (job.error || job.status === "uncertain") {
        err.innerHTML = `<p class="text-bad font-semibold mb-1">Erro / por que conferir</p>
        <p><span class="text-mist">O que:</span> ${escapeHtml(job.error || "uncertain")}</p>
        <p><span class="text-mist">Onde:</span> ${escapeHtml(job.errorWhere || "\u2014")}</p>
        <p><span class="text-mist">Por qu\xEA:</span> ${escapeHtml(
          job.errorReason || "Confira antes de reenviar."
        )}</p>`;
        err.className = "rounded-xl bg-bad/10 border border-bad/40 p-3 text-sm leading-relaxed";
      } else {
        err.textContent = job.status === "pending" ? "Aguardando o PrintWorker." : job.status === "processing" ? "Em processamento agora." : job.status === "simulated" ? "Conclu\xEDdo em simula\xE7\xE3o." : job.status === "sent" ? "Enviado ao spooler (n\xE3o garante papel)." : "Sem erro.";
        err.className = "rounded-xl bg-ink border border-ink-line p-3 text-sm leading-relaxed text-mist";
      }
    }
    function updatePipeline() {
      const processing = state.jobs.find((j) => j.status === "processing");
      const applied = state.applied;
      pipeline.textContent = !applied ? "Aguardando\u2026" : applied.paused ? "Processamento: PAUSADO \u2014 novos pedidos ficam na fila." : processing ? `Processando agora: ${processing.reference}` : applied.simulation ? "Processamento: ocioso \u2022 modo simula\xE7\xE3o." : `Processamento: ocioso \u2022 modo real \u2192 '${applied.printerName || "\u2014"}'.`;
    }
    async function sendTest() {
      if (state.dirty) return feedback("Salve as configura\xE7\xF5es antes de testar.", true);
      if (state.applied && !state.applied.simulation && !confirm(`Enviar para ${state.applied.printerName}?`))
        return;
      try {
        await api2("/api/jobs", {
          method: "POST",
          body: JSON.stringify({
            reference: "teste-" + Math.random().toString(16).slice(2, 12),
            text: document.getElementById("sample").value,
            jobType: document.getElementById("jobType").value || "default",
            template: document.getElementById("template").value || null
          })
        });
        feedback("Teste recebido. Acompanhe no hist\xF3rico.");
        onChanged?.();
      } catch (err2) {
        feedback(err2.message, true);
      }
    }
    async function reprintSelected() {
      const job = state.jobs.find((j) => j.id === state.selectedId);
      if (!job) return feedback("Selecione um pedido.", true);
      if (!confirm(`Reimprimir ${job.reference}?`)) return;
      try {
        await api2(`/api/jobs/${job.id}/reprint`, { method: "POST", body: "{}" });
        feedback("Reimpress\xE3o enfileirada.");
        onChanged?.();
      } catch (err2) {
        feedback(err2.message, true);
      }
    }
    function showFull() {
      const job = state.jobs.find((j) => j.id === state.selectedId);
      if (!job) return feedback("Selecione um pedido.", true);
      const lines = [
        `Pedido: ${job.reference}`,
        `Situa\xE7\xE3o: ${statusLabel[job.status] || job.status}`,
        `Tipo: ${job.jobType}`,
        `Impressora: ${job.printerName || "\u2014"}`,
        "",
        "PASSOS",
        ...(job.steps || []).map(
          (s, i) => `${i + 1}. [${new Date(s.at).toLocaleTimeString()}] ${s.message}
   Onde: ${s.where}${s.detail ? "\n   " + s.detail : ""}`
        ),
        "",
        job.error ? `ERRO
  ${job.error}
  ${job.errorWhere}
  ${job.errorReason}` : "",
        "",
        "TEXTO",
        job.text
      ];
      openDlg("Explica\xE7\xE3o do pedido", lines.filter(Boolean).join("\n"));
    }
    function exportJobs(csv) {
      const data = state.jobs.slice().reverse();
      let blob, name;
      if (csv) {
        const rows = [["reference", "type", "status", "printer", "created"].join(",")].concat(
          data.map(
            (j) => [j.reference, j.jobType, j.status, j.printerName || "", j.createdAt].map((v) => `"${String(v).replaceAll('"', '""')}"`).join(",")
          )
        );
        blob = new Blob([rows.join("\n")], { type: "text/csv" });
        name = "autoprin-jobs.csv";
      } else {
        blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
        name = "autoprin-jobs.json";
      }
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = name;
      a.click();
    }
    return { renderJobs, updatePipeline };
  }

  // AutoPrint/wwwroot/js/components/connect-tab.js
  function bindConnectTab({ api: api2, apiKey }) {
    const inboxFolder = document.getElementById("inboxFolder");
    const inboxEnabled = document.getElementById("inboxEnabled");
    const deleteInboxAfterPrint = document.getElementById("deleteInboxAfterPrint");
    const inboxStatus = document.getElementById("inboxStatus");
    const inboxFiles = document.getElementById("inboxFiles");
    const markInboxDirty = () => {
      state.inboxDirty = true;
      if (inboxStatus) {
        inboxStatus.textContent = "Altera\xE7\xF5es da pasta ainda n\xE3o salvas.";
        inboxStatus.className = "text-sm text-warn min-h-[1.25rem]";
      }
    };
    document.getElementById("endpoint").value = location.origin + "/api/jobs";
    document.getElementById("apiHint").textContent = location.host;
    document.getElementById("btnCopyEndpoint").addEventListener("click", () => {
      navigator.clipboard.writeText(document.getElementById("endpoint").value);
      feedback("Endere\xE7o copiado.");
    });
    document.getElementById("btnCopyKey").addEventListener("click", () => {
      navigator.clipboard.writeText(apiKey);
      feedback("Chave copiada. Use s\xF3 no sistema autorizado.");
    });
    document.getElementById("btnOpenLogs").addEventListener("click", async () => {
      try {
        await api2("/api/events/open-folder", { method: "POST", body: "{}" });
        feedback("Pasta de logs aberta.");
      } catch (err) {
        feedback(err.message, true);
      }
    });
    document.getElementById("btnEvents").addEventListener("click", async () => {
      try {
        const data = await api2("/api/events");
        const lines = [`Eventos de ${data.day}`, "\u2500".repeat(40)];
        const events = (data.events || []).slice().reverse().slice(0, 80);
        if (!events.length) lines.push("Nenhum evento neste dia.");
        else
          events.forEach((e) => {
            lines.push(
              `${new Date(e.at).toLocaleTimeString()}  ${e.reference}  [${e.status}]  via ${e.delivery || "\u2014"}`
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
        feedback("Varrendo rede local\u2026");
        const found = await api2("/api/printers/discover", { method: "POST", body: "{}" });
        if (!found.length) return feedback("Nenhuma porta de impress\xE3o encontrada.", true);
        openDlg(
          "Impressoras na rede",
          found.map((f) => `${f.address}:${f.port} \u2014 ${f.hint}`).join("\n")
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
      if (inboxStatus) inboxStatus.textContent = "Pasta removida do formul\xE1rio. Clique em Salvar pasta.";
    });
    const btnBrowse = document.getElementById("btnBrowseInbox");
    if (btnBrowse) btnBrowse.addEventListener("click", async () => {
      try {
        if (inboxStatus) {
          inboxStatus.textContent = "Abrindo seletor de pasta\u2026";
          inboxStatus.className = "text-sm text-sea-glow min-h-[1.25rem]";
        }
        const result = await api2("/api/inbox/browse", { method: "POST", body: "{}" });
        if (result.cancelled || !result.folder) {
          if (inboxStatus) inboxStatus.textContent = "Sele\xE7\xE3o cancelada.";
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
        await api2("/api/inbox/open", { method: "POST", body: "{}" });
        feedback("Pasta de entrada aberta.");
      } catch (err) {
        feedback(err.message, true);
      }
    });
    const btnSaveInbox = document.getElementById("btnSaveInbox");
    if (btnSaveInbox) btnSaveInbox.addEventListener("click", async () => {
      if (!state.applied) {
        if (inboxStatus) {
          inboxStatus.textContent = "Configura\xE7\xF5es ainda n\xE3o carregadas. Clique em Atualizar.";
          inboxStatus.className = "text-sm text-bad min-h-[1.25rem]";
        }
        return;
      }
      try {
        state.applied = await api2("/api/settings", {
          method: "PUT",
          body: JSON.stringify(
            buildSettingsPayload({
              inboxFolder: inboxFolder?.value.trim() || "",
              inboxEnabled: !!inboxEnabled?.checked,
              deleteInboxAfterPrint: !!deleteInboxAfterPrint?.checked
            })
          )
        });
        state.inboxDirty = false;
        applyInboxFromSettings();
        await refreshInbox();
        if (inboxStatus) {
          inboxStatus.textContent = state.applied.inboxFolder ? state.applied.inboxEnabled ? "Pasta salva \u2014 vigil\xE2ncia ativa." : "Pasta salva \u2014 vigil\xE2ncia desativada." : "Pasta removida.";
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
        const snap = await api2("/api/inbox");
        if (!state.inboxDirty) {
          const bits = [];
          if (snap.enabled) bits.push("vigil\xE2ncia ativa");
          else bits.push("vigil\xE2ncia off");
          if (snap.deleteAfterPrint) bits.push("apaga ap\xF3s imprimir");
          bits.push(`${snap.fileCount} arquivo(s)`);
          inboxStatus.textContent = bits.join(" \xB7 ");
          inboxStatus.className = "text-sm text-sea-glow min-h-[1.25rem]";
        }
        if (!snap.files?.length) {
          inboxFiles.textContent = snap.folder ? "Pasta vazia \u2014 aguardando PDF/imagens." : "Nenhuma pasta configurada. Escolha e salve acima.";
          return;
        }
        inboxFiles.innerHTML = snap.files.map(
          (f) => {
            const info = inboxStatusInfo(f.status);
            const detail = f.errorReason || f.error || "";
            return `<div class="py-1.5 border-b border-ink-line/60 last:border-0">
              <div class="flex items-center justify-between gap-2">
                <span class="text-paper truncate">${escapeHtml(f.name)}</span>
                <span class="${info.css} rounded-full px-2 py-0.5 text-[11px] shrink-0">${info.label}</span>
              </div>
              <div class="text-mist text-xs">${escapeHtml(f.kind)} \xB7 ${formatBytes(f.size)}${detail ? ` \xB7 <span class="text-bad" title="${escapeHtml(detail)}">${escapeHtml(detail)}</span>` : ""}</div>
            </div>`;
          }
        ).join("");
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
        waiting: { label: "Aguardando", css: "bg-ink-line text-mist" }
      }[status] || { label: status || "Aguardando", css: "bg-ink-line text-mist" };
    }
    return { applyInboxFromSettings, refreshInbox };
  }

  // AutoPrint/wwwroot/js/app.js
  var KEY = window.AUTOPRINT_KEY || "";
  var { api } = createApi(KEY);
  var tabs = [
    { id: "config", title: "Configure a impressora", subtitle: "Impressora, simula\xE7\xE3o e Windows" },
    { id: "connect", title: "Conecte seu sistema", subtitle: "API, pasta de entrada, logs e rede" },
    { id: "monitor", title: "Acompanhe o passo a passo", subtitle: "Fila, erros e testes" }
  ];
  function setTab(id) {
    tabs.forEach((t, index) => {
      const panel = document.getElementById(`panel-${t.id}`);
      const btn = document.getElementById(`tab-${t.id}`);
      if (!panel || !btn) return;
      const active = t.id === id;
      panel.classList.toggle("panel-hidden", !active);
      btn.classList.toggle("active", active);
      btn.setAttribute("aria-current", active ? "page" : "false");
      if (active) {
        const title = document.getElementById("pageTitle");
        const subtitle = document.getElementById("pageSubtitle");
        if (title) title.textContent = t.title;
        if (subtitle) subtitle.textContent = `${String(index + 1).padStart(2, "0")} \xB7 ${t.subtitle}`;
      }
    });
    const main = document.getElementById("mainPanel");
    if (main) {
      main.classList.toggle("overflow-hidden", id === "config");
      main.classList.toggle("overflow-y-auto", id !== "config");
      main.classList.toggle("scroll-thin", id !== "config");
    }
    localStorage.setItem("autoprin-tab", id);
  }
  function showBootError(err) {
    console.error(err);
    const line = document.getElementById("healthLine");
    if (line) {
      line.textContent = `Erro no painel: ${err?.message || err}`;
      line.className = "text-xs text-bad mt-0.5";
    }
    setConnection(false);
  }
  tabs.forEach((t) => {
    const btn = document.getElementById(`tab-${t.id}`);
    if (btn) btn.addEventListener("click", () => setTab(t.id));
  });
  var refreshBtn = document.getElementById("btnRefresh");
  var dlgClose = document.getElementById("dlgClose");
  if (refreshBtn) refreshBtn.addEventListener("click", () => refreshAll());
  if (dlgClose) dlgClose.addEventListener("click", () => document.getElementById("dlg")?.close());
  var printer = {
    applySettingsToForm() {
    },
    loadPrinters: async () => {
    },
    redrawPreview() {
    }
  };
  var monitor = {
    updatePipeline() {
    },
    renderJobs() {
    }
  };
  var connect = {
    applyInboxFromSettings() {
    },
    refreshInbox: async () => {
    }
  };
  try {
    printer = bindPrinterTab({ api, onSaved: () => refreshAll() });
  } catch (err) {
    showBootError(err);
  }
  try {
    monitor = bindMonitorTab({ api, onChanged: () => refreshAll() });
  } catch (err) {
    showBootError(err);
  }
  try {
    connect = bindConnectTab({ api, apiKey: KEY });
  } catch (err) {
    showBootError(err);
  }
  async function refreshAll() {
    try {
      const [status, jobList, metrics] = await Promise.all([
        api("/api/status"),
        api("/api/jobs"),
        api("/api/metrics")
      ]);
      setConnection(true);
      state.applied = status.settings;
      state.jobs = jobList || [];
      printer.applySettingsToForm?.();
      connect.applyInboxFromSettings?.();
      connect.refreshInbox?.();
      const startup = document.getElementById("startup");
      if (startup) startup.checked = !!status.health?.startWithWindows;
      const mode = state.applied.paused ? "Pausado" : state.applied.simulation ? "Simula\xE7\xE3o" : "Real";
      renderStats({
        mode,
        queue: (metrics.pending || 0) + (metrics.processing || 0),
        done: (metrics.simulatedTotal || 0) + (metrics.sentTotal || 0),
        bad: metrics.uncertainTotal || 0,
        healthLine: `uptime \u2022 ${metrics.jobsPerHourLast24h}/h \u2022 incert ${metrics.uncertainRatePercent}% \u2022 fila ${metrics.pending}/${metrics.processing}`
      });
      monitor.updatePipeline?.();
      monitor.renderJobs?.();
    } catch (e) {
      setConnection(false);
      const line = document.getElementById("healthLine");
      if (line) line.textContent = e.message || "Falha ao conectar na API";
      feedback(e.message, true);
    }
  }
  setTab(localStorage.getItem("autoprin-tab") || "config");
  Promise.resolve().then(() => printer.loadPrinters(api)).catch((e) => {
    console.error(e);
    const summary = document.getElementById("printerSummary");
    if (summary) summary.textContent = e.message || "Falha ao listar impressoras.";
  }).finally(() => {
    refreshAll();
    setInterval(refreshAll, 2e3);
  });
  window.__autoprintBooted = true;
})();

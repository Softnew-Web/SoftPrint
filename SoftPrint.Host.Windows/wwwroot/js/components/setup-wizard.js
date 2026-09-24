import { state } from "../state.js";
import { buildSettingsPayload } from "../settings-payload.js";

const DONE_KEY = "softprint-setup-done";

/**
 * Assistente de primeiro uso: impressora → papel Padrão → teste guiado.
 */
export function bindSetupWizard({ api, setTab, onRefresh, loadPrinters }) {
  const overlay = document.getElementById("setupWizard");
  const title = document.getElementById("setupWizardTitle");
  const hint = document.getElementById("setupWizardHint");
  const body = document.getElementById("setupWizardBody");
  const msg = document.getElementById("setupWizardMsg");
  const btnNext = document.getElementById("setupWizardNext");
  const btnBack = document.getElementById("setupWizardBack");
  const btnSkip = document.getElementById("setupWizardSkip");
  const openBtn = document.getElementById("btnSetupWizard");

  let step = 1;

  function setMsg(text, bad = false) {
    if (!msg) return;
    msg.textContent = text || "";
    msg.className = bad ? "text-sm text-bad min-h-[1.25rem]" : "text-sm text-mist min-h-[1.25rem]";
  }

  function paintDots() {
    for (let i = 1; i <= 3; i++) {
      const el = document.getElementById(`setupStepDot${i}`);
      if (!el) continue;
      el.className = `flex-1 rounded-full h-1.5 ${i <= step ? "bg-sea" : "bg-ink-line"}`;
    }
    if (btnBack) btnBack.classList.toggle("hidden", step <= 1);
    if (btnNext) btnNext.textContent = step >= 3 ? "Concluir" : "Continuar";
  }

  function renderStep() {
    paintDots();
    setMsg("");
    if (!body) return;

    if (step === 1) {
      if (title) title.textContent = "1 · Escolha a impressora";
      if (hint) hint.textContent = "Selecione a impressora do Windows (ou use Rede antes) e salve.";
      const options = [...(document.getElementById("printers")?.options || [])]
        .filter((o) => o.value)
        .map((o) => `<option value="${escapeAttr(o.value)}">${escapeHtml(o.text)}</option>`)
        .join("");
      const current = state.applied?.printerName || "";
      body.innerHTML = `
        <label class="block text-sm">Impressora
          <select id="setupPrinter" class="mt-1 w-full rounded-lg bg-ink border border-ink-line px-3 py-2 text-sm">
            <option value="">Selecionar…</option>
            ${options}
          </select>
        </label>
        <label class="inline-flex items-center gap-2 text-sm">
          <input id="setupSimulation" type="checkbox" class="accent-sea" ${state.applied?.simulation ? "checked" : ""} />
          Começar em simulação (sem papel)
        </label>
        <p class="text-xs text-mist">Dica: use o botão Rede na aba Configurar se a impressora ainda não aparece.</p>`;
      const sel = document.getElementById("setupPrinter");
      if (sel && current) sel.value = current;
      return;
    }

    if (step === 2) {
      if (title) title.textContent = "2 · Papel da etiqueta";
      if (hint) hint.textContent = "Recomendado: Padrão 200×70 mm para cupom/etiqueta.";
      const paper = state.applied?.paperSize || "padrao";
      body.innerHTML = `
        <label class="block text-sm">Tamanho
          <select id="setupPaper" class="mt-1 w-full rounded-lg bg-ink border border-ink-line px-3 py-2 text-sm">
            <option value="padrao">Padrão (200×70)</option>
            <option value="receipt80">Cupom 80 mm</option>
            <option value="receipt58">Cupom 58 mm</option>
            <option value="a4">A4</option>
          </select>
        </label>
        <p class="text-xs text-mist">Isso vale para o preview e para o envio real.</p>`;
      const sel = document.getElementById("setupPaper");
      if (sel) sel.value = ["padrao", "receipt80", "receipt58", "a4"].includes(paper) ? paper : "padrao";
      return;
    }

    if (title) title.textContent = "3 · Teste guiado";
    if (hint) hint.textContent = "Envia um pedido de teste com o papel atual e mede o tempo.";
    body.innerHTML = `
      <p>Impressora: <strong class="text-paper">${escapeHtml(state.applied?.printerName || "—")}</strong></p>
      <p>Papel: <strong class="text-paper">${escapeHtml(state.applied?.paperSize || "—")}</strong>
        · modo <strong class="text-paper">${state.applied?.simulation ? "simulação" : "real"}</strong></p>
      <p class="text-xs text-mist">Ao concluir, o SoftPrint marca o assistente como feito neste navegador.</p>`;
  }

  async function saveStep1() {
    const printer = document.getElementById("setupPrinter")?.value || "";
    if (!printer) {
      setMsg("Selecione uma impressora.", true);
      return false;
    }
    const simulation = !!document.getElementById("setupSimulation")?.checked;
    state.applied = await api("/api/settings", {
      method: "PUT",
      body: JSON.stringify(buildSettingsPayload({ printerName: printer, simulation })),
    });
    const sel = document.getElementById("printers");
    if (sel) sel.value = printer;
    await onRefresh?.();
    return true;
  }

  async function saveStep2() {
    const paperSize = document.getElementById("setupPaper")?.value || "padrao";
    const presets = {
      padrao: [200, 70],
      receipt80: [80, 297],
      receipt58: [58, 200],
      a4: [210, 297],
    };
    const [w, h] = presets[paperSize] || presets.padrao;
    state.applied = await api("/api/settings", {
      method: "PUT",
      body: JSON.stringify(
        buildSettingsPayload({
          paperSize,
          paperWidthMm: w,
          paperHeightMm: h,
          paperLandscape: false,
        })
      ),
    });
    const paperSel = document.getElementById("paperSize");
    if (paperSel) paperSel.value = paperSize;
    await onRefresh?.();
    return true;
  }

  async function runStep3() {
    setMsg("Enviando teste…");
    const started = Date.now();
    const job = await api("/api/jobs/guided-test", { method: "POST", body: "{}" });
    await onRefresh?.();
    const deadline = Date.now() + 45_000;
    while (Date.now() < deadline) {
      await onRefresh?.();
      const found = (state.jobs || []).find((j) => j.id === job.id);
      if (found && found.status !== "pending" && found.status !== "processing") {
        const sec = ((Date.now() - started) / 1000).toFixed(1);
        if (found.status === "uncertain") {
          setMsg(`Teste falhou em ${sec}s — veja o Monitor.`, true);
          return false;
        }
        setMsg(`Teste OK em ${sec}s (${found.status}).`);
        return true;
      }
      await new Promise((r) => setTimeout(r, 700));
    }
    setMsg("Teste ainda na fila — acompanhe no Monitor.", true);
    return false;
  }

  async function next() {
    try {
      if (step === 1) {
        if (!(await saveStep1())) return;
        step = 2;
        renderStep();
        return;
      }
      if (step === 2) {
        if (!(await saveStep2())) return;
        step = 3;
        renderStep();
        return;
      }
      const ok = await runStep3();
      if (!ok) return;
      finish(true);
    } catch (err) {
      setMsg(err.message || String(err), true);
    }
  }

  function finish(completed) {
    try {
      localStorage.setItem(DONE_KEY, completed ? "1" : "skipped");
    } catch {
      /* ignore */
    }
    hide();
    if (completed) setTab?.("monitor");
  }

  function show() {
    if (!overlay) return;
    setTab?.("config");
    step = 1;
    overlay.classList.remove("hidden");
    loadPrinters?.(api)?.finally?.(() => renderStep()) || renderStep();
  }

  function hide() {
    overlay?.classList.add("hidden");
  }

  function maybeAutoOpen() {
    try {
      if (localStorage.getItem(DONE_KEY)) return;
    } catch {
      return;
    }
    const needs =
      !state.applied?.printerName ||
      state.applied?.simulation === true;
    if (needs) show();
  }

  btnNext?.addEventListener("click", next);
  btnBack?.addEventListener("click", () => {
    if (step > 1) {
      step -= 1;
      renderStep();
    }
  });
  btnSkip?.addEventListener("click", () => finish(false));
  openBtn?.addEventListener("click", show);
  if (openBtn) openBtn.classList.remove("hidden");

  return { show, maybeAutoOpen, hide };
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll('"', "&quot;");
}

function escapeAttr(value) {
  return escapeHtml(value).replaceAll("'", "&#39;");
}

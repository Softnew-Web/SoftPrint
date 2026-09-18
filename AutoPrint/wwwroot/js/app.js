import { createApi } from "./api.js";
import { state, feedback } from "./state.js";
import { renderStats, setConnection } from "./components/stats.js";
import { bindPrinterTab } from "./components/printer-tab.js";
import { bindMonitorTab } from "./components/monitor-tab.js";
import { bindConnectTab } from "./components/connect-tab.js";

const KEY = window.AUTOPRINT_KEY || "";
const { api } = createApi(KEY);

const tabs = [
  { id: "config", title: "Configure a impressora", subtitle: "Impressora, simulação e Windows" },
  { id: "connect", title: "Conecte seu sistema", subtitle: "API, pasta de entrada, logs e rede" },
  { id: "monitor", title: "Acompanhe o passo a passo", subtitle: "Fila, erros e testes" },
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
      if (subtitle) subtitle.textContent = `${String(index + 1).padStart(2, "0")} · ${t.subtitle}`;
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

// Abas primeiro — mesmo se o resto falhar, dá para navegar.
tabs.forEach((t) => {
  const btn = document.getElementById(`tab-${t.id}`);
  if (btn) btn.addEventListener("click", () => setTab(t.id));
});

const refreshBtn = document.getElementById("btnRefresh");
const dlgClose = document.getElementById("dlgClose");
if (refreshBtn) refreshBtn.addEventListener("click", () => refreshAll());
if (dlgClose) dlgClose.addEventListener("click", () => document.getElementById("dlg")?.close());

let printer = {
  applySettingsToForm() {},
  loadPrinters: async () => {},
  redrawPreview() {},
};
let monitor = {
  updatePipeline() {},
  renderJobs() {},
};
let connect = {
  applyInboxFromSettings() {},
  refreshInbox: async () => {},
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
      api("/api/metrics"),
    ]);
    setConnection(true);
    state.applied = status.settings;
    state.jobs = jobList || [];
    printer.applySettingsToForm?.();
    connect.applyInboxFromSettings?.();
    connect.refreshInbox?.();
    const startup = document.getElementById("startup");
    if (startup) startup.checked = !!status.health?.startWithWindows;

    const mode = state.applied.paused
      ? "Pausado"
      : state.applied.simulation
        ? "Simulação"
        : "Real";
    renderStats({
      mode,
      queue: (metrics.pending || 0) + (metrics.processing || 0),
      done: (metrics.simulatedTotal || 0) + (metrics.sentTotal || 0),
      bad: metrics.uncertainTotal || 0,
      healthLine: `uptime • ${metrics.jobsPerHourLast24h}/h • incert ${metrics.uncertainRatePercent}% • fila ${metrics.pending}/${metrics.processing}`,
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

Promise.resolve()
  .then(() => printer.loadPrinters(api))
  .catch((e) => {
    console.error(e);
    const summary = document.getElementById("printerSummary");
    if (summary) summary.textContent = e.message || "Falha ao listar impressoras.";
  })
  .finally(() => {
    refreshAll();
    setInterval(refreshAll, 2000);
  });

window.__autoprintBooted = true;

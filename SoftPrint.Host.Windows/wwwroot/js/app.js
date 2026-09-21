import { createApi } from "./api.js";
import { state, feedback } from "./state.js";
import { renderStats, setConnection, setApiHint, modeLabel } from "./components/stats.js";
import { bindPrinterTab } from "./components/printer-tab.js";
import { bindMonitorTab } from "./components/monitor-tab.js";
import { bindConnectTab } from "./components/connect-tab.js";
import { bindSystemSettingsTab } from "./components/system-settings-tab.js";
import { applyUpdateInfo, applyVersion } from "./components/update-banner.js";

const KEY = window.SOFTPRINT_KEY || "";
const { api } = createApi(KEY);

setApiHint(location.origin);
setConnection(false);

const tabs = [
  { id: "config", title: "Configure a impressora", subtitle: "Impressora, simulação e Windows" },
  { id: "connect", title: "Conecte seu sistema", subtitle: "API, pasta de entrada, logs e rede" },
  { id: "monitor", title: "Acompanhe o passo a passo", subtitle: "Fila, erros e testes" },
  { id: "settings", title: "Configurações", subtitle: "Opções deste computador" },
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
  localStorage.setItem("softprint-tab", id);
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
if (refreshBtn) refreshBtn.addEventListener("click", async () => {
  await refreshAll();
  // Depois do status, força consulta ao GitHub (senão o cache do /api/status pode esconder a versão nova).
  await refreshUpdate({ force: true });
});
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
let systemSettings = { load: async () => {} };

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

try {
  systemSettings = bindSystemSettingsTab({ api });
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
    const appName = status.application || "SoftPrint";
    setConnection(true, appName);
    setApiHint(status.baseUrl || location.origin);
    applyVersion(status.version || status.update?.currentVersion);
    applyUpdateInfo(
      {
        ...(status.update || {}),
        currentVersion: status.version || status.update?.currentVersion,
      },
      { api }
    );
    const endpoint = document.getElementById("endpoint");
    if (endpoint) {
      const origin = (status.baseUrl || location.origin).replace(/\/$/, "");
      endpoint.value = `${origin}/api/jobs`;
    }
    state.applied = status.settings;
    state.jobs = jobList || [];
    applyCapabilities(status.capabilities);
    printer.applySettingsToForm?.();
    connect.applyInboxFromSettings?.();
    connect.refreshInbox?.();
    const startup = document.getElementById("startup");
    if (startup) startup.checked = !!status.health?.startWithWindows;

    const mode = modeLabel({
      paused: !!state.applied.paused,
      simulation: !!state.applied.simulation,
    });
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

function applyCapabilities(capabilities) {
  if (!capabilities) return;
  document.documentElement.dataset.platform = capabilities.platform;
  const browse = document.getElementById("btnBrowseInbox");
  if (browse) {
    browse.disabled = !capabilities.hasNativeFolderPicker;
    browse.classList.toggle("hidden", !capabilities.hasNativeFolderPicker);
    browse.title = capabilities.hasNativeFolderPicker
      ? ""
      : "Digite o caminho da pasta neste sistema.";
  }
  const startup = document.getElementById("startup");
  const startupLabel = document.getElementById("startupLabel");
  if (startup) startup.title = `Inicialização: ${capabilities.startupRegistration}`;
  if (startupLabel) {
    startupLabel.textContent = capabilities.startupRegistration === "systemd-user"
      ? "Serviço systemd do usuário"
      : capabilities.startupRegistration === "registry"
        ? "Iniciar com o Windows"
        : "Iniciar automaticamente";
  }
  const closeHint = document.getElementById("closeHint");
  if (closeHint) closeHint.classList.toggle("hidden", !capabilities.hasDesktopShell);
  const backend = document.getElementById("printerBackendHint");
  if (backend) {
    backend.textContent = capabilities.printingBackend === "cups"
      ? "Filas CUPS, IPP e ESC/POS TCP 9100"
      : "USB, cabo e rede no Windows";
  }
  const line = document.getElementById("healthLine");
  if (line && capabilities.isLegacy)
    line.textContent = "Modo Legacy · painel no navegador · compatibilidade sem suporte de segurança";
}

setTab(localStorage.getItem("softprint-tab") || "config");

async function refreshUpdate({ force = false } = {}) {
  try {
    const update = await api(force ? "/api/update?refresh=1" : "/api/update");
    applyUpdateInfo(update, { api });
  } catch (err) {
    console.warn("Falha ao verificar atualização", err);
  }
}

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
    setTimeout(refreshUpdate, 4000);
    setInterval(refreshUpdate, 30 * 60 * 1000);
  });
systemSettings.load().catch(showBootError);

window.__softprinttBooted = true;

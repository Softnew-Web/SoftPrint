const DISMISS_KEY = "softprint-update-dismissed";

let applyBound = false;
let pollTimer = null;

export function applyVersion(version) {
  const text = `v${version || "—"}`;
  for (const id of ["appVersionLabel", "headerVersion", "settingsVersion"]) {
    const el = document.getElementById(id);
    if (el) el.textContent = text;
  }
  try {
    document.title = `SoftPrint ${text}`;
  } catch {
    /* ignore */
  }
}

export function applyUpdateInfo(update, { api } = {}) {
  const banner = document.getElementById("updateBanner");
  const text = document.getElementById("updateBannerText");
  const btn = document.getElementById("updateDownloadLink");
  const dismiss = document.getElementById("updateDismissBtn");

  const current = update?.currentVersion || "—";
  applyVersion(current);

  if (!banner || !text || !btn) return;

  if (!update?.updateAvailable) {
    if (update?.error) {
      banner.classList.remove("hidden");
      banner.classList.remove("bg-warn/15", "border-warn/40", "bg-bad/20", "border-bad/40");
      banner.classList.add("bg-ink-line/40", "border-ink-line");
      text.textContent = `Não foi possível verificar atualizações: ${update.error}`;
      btn.classList.add("hidden");
      if (dismiss) {
        dismiss.classList.remove("hidden");
        dismiss.onclick = () => banner.classList.add("hidden");
      }
      return;
    }
    banner.classList.add("hidden");
    return;
  }

  btn.classList.remove("hidden");

  const latest = update.latestVersion || "?";
  const dismissed = sessionStorage.getItem(DISMISS_KEY) === latest;
  if (dismissed && !update.mandatory) {
    banner.classList.add("hidden");
    return;
  }

  banner.classList.remove("hidden");
  banner.classList.toggle("bg-bad/20", !!update.mandatory);
  banner.classList.toggle("border-bad/40", !!update.mandatory);
  banner.classList.toggle("bg-warn/15", !update.mandatory);
  banner.classList.toggle("border-warn/40", !update.mandatory);

  text.textContent = update.mandatory
    ? `Atualização obrigatória: ${current} → ${latest}. Instale a nova versão para continuar.`
    : `Nova versão disponível: ${current} → ${latest}.`;

  if (dismiss) {
    dismiss.classList.toggle("hidden", !!update.mandatory);
    dismiss.onclick = () => {
      sessionStorage.setItem(DISMISS_KEY, latest);
      banner.classList.add("hidden");
    };
  }

  if (api && !applyBound) {
    applyBound = true;
    btn.addEventListener("click", () => startAutoUpdate(api));
    document.getElementById("updateProgressClose")?.addEventListener("click", hideOverlay);
  }
}

function showOverlay() {
  const overlay = document.getElementById("updateOverlay");
  const err = document.getElementById("updateProgressError");
  const close = document.getElementById("updateProgressClose");
  if (overlay) overlay.classList.remove("hidden");
  if (err) {
    err.classList.add("hidden");
    err.textContent = "";
  }
  if (close) close.classList.add("hidden");
  setProgress(1, "Preparando atualização…");
}

function hideOverlay() {
  document.getElementById("updateOverlay")?.classList.add("hidden");
}

function setProgress(percent, message) {
  const bar = document.getElementById("updateProgressBar");
  const pct = document.getElementById("updateProgressPct");
  const text = document.getElementById("updateProgressText");
  const value = Math.max(0, Math.min(100, Number(percent) || 0));
  if (bar) bar.style.width = `${value}%`;
  if (pct) pct.textContent = `${value}%`;
  if (text && message) text.textContent = message;
}

function showError(message) {
  const err = document.getElementById("updateProgressError");
  const close = document.getElementById("updateProgressClose");
  if (err) {
    err.textContent = message || "Falha na atualização.";
    err.classList.remove("hidden");
  }
  if (close) close.classList.remove("hidden");
  const text = document.getElementById("updateProgressText");
  if (text) text.textContent = "Não foi possível concluir a atualização.";
}

async function startAutoUpdate(api) {
  showOverlay();
  try {
    await api("/api/update/apply", { method: "POST", body: "{}" });
  } catch (err) {
    showError(err.message || String(err));
    return;
  }
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(() => pollProgress(api), 500);
  pollProgress(api);
}

async function pollProgress(api) {
  try {
    const s = await api("/api/update/progress");
    setProgress(s.percent, s.message || "Atualizando…");
    if (s.failed) {
      clearInterval(pollTimer);
      pollTimer = null;
      showError(s.error || "Falha na atualização.");
      return;
    }
    if (s.restarting) {
      clearInterval(pollTimer);
      pollTimer = null;
      setProgress(100, "Reiniciando o SoftPrint…");
    }
  } catch (err) {
    setProgress(100, "Reiniciando o SoftPrint…");
  }
}

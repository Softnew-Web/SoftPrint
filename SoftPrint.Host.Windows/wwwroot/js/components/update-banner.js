const DISMISS_KEY = "softprint-update-dismissed";

let applyBound = false;
let rollbackBound = false;
let pollTimer = null;
let rollbackTarget = null;

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

export async function refreshRollback(api) {
  const box = document.getElementById("rollbackBox");
  const hint = document.getElementById("rollbackHint");
  const btn = document.getElementById("btnRollback");
  if (!box || !btn) return;

  try {
    const info = await api("/api/update/rollback");
    if (!info?.available || !info.targetVersion) {
      box.classList.add("hidden");
      rollbackTarget = null;
      return;
    }

    rollbackTarget = info.targetVersion;
    box.classList.remove("hidden");
    if (hint) {
      hint.textContent = `Versão anterior local: v${info.targetVersion} (atual: v${info.currentVersion}).`;
    }
    btn.textContent = `Retroceder agora para v${info.targetVersion}`;

    if (api && !rollbackBound) {
      rollbackBound = true;
      btn.addEventListener("click", () => startRollback(api));
      document.getElementById("updateProgressClose")?.addEventListener("click", hideOverlay);
    }
  } catch (err) {
    box.classList.add("hidden");
    console.warn("Falha ao consultar rollback", err);
  }

  await refreshUpdateHistory(api);
}

export async function refreshUpdateHistory(api) {
  const checkEl = document.getElementById("updateHistoryCheck");
  const applyEl = document.getElementById("updateHistoryApply");
  if (!checkEl && !applyEl) return;

  try {
    const h = await api("/api/update/history");
    if (checkEl) {
      if (h.lastCheckError) {
        checkEl.textContent = `Última verificação: falhou (${fmtWhen(h.lastCheckedAt)}) — ${h.lastCheckError}`;
        checkEl.className = "text-bad";
      } else if (h.lastCheckedAt) {
        const avail =
          h.lastUpdateAvailable === true
            ? ` · nova: v${h.lastLatestVersion || "?"}`
            : h.lastUpdateAvailable === false
              ? " · em dia"
              : "";
        checkEl.textContent = `Última verificação: ${fmtWhen(h.lastCheckedAt)}${avail}`;
        checkEl.className = "text-mist";
      } else {
        checkEl.textContent = "Última verificação: ainda não consultou o servidor.";
        checkEl.className = "text-mist";
      }
    }

    if (applyEl) {
      if (!h.lastApplyAt) {
        applyEl.textContent = "Última instalação: nenhuma neste PC.";
        applyEl.className = "text-mist";
      } else if (h.lastApplyOk === false) {
        applyEl.textContent = `Última instalação: falhou (${fmtWhen(h.lastApplyAt)}) — v${h.lastApplyVersion || "?"} · ${h.lastApplyError || "erro"}`;
        applyEl.className = "text-bad";
      } else if (h.lastApplyOk === true) {
        const kind = h.lastApplyKind === "rollback" ? "retrocesso" : "atualização";
        applyEl.textContent = `Última instalação: OK (${fmtWhen(h.lastApplyAt)}) — ${kind} v${h.lastApplyVersion || "?"}`;
        applyEl.className = "text-sea-glow";
      } else {
        applyEl.textContent = `Última instalação: em andamento (${fmtWhen(h.lastApplyAt)}) — v${h.lastApplyVersion || "?"}`;
        applyEl.className = "text-warn";
      }
    }
  } catch (err) {
    console.warn("Falha ao ler histórico de update", err);
  }
}

function fmtWhen(value) {
  if (!value) return "—";
  try {
    return new Date(value).toLocaleString();
  } catch {
    return String(value);
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

async function startRollback(api) {
  const target = rollbackTarget;
  if (!target) return;
  const ok = window.confirm(
    `Voltar o SoftPrint para a versão ${target}?\n\nSerá restaurada a cópia local salva antes da última atualização (só essa versão fica guardada).`
  );
  if (!ok) return;

  showOverlay();
  setProgress(1, `Preparando retorno para v${target}…`);
  try {
    await api("/api/update/apply", {
      method: "POST",
      body: JSON.stringify({ rollback: true, version: target })
    });
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

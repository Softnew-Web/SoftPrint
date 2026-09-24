/** Status da impressora no topo do painel (e espelho no Monitor). */
export async function refreshHeaderPrinterStatus(api, settings) {
  const targets = [
    document.getElementById("headerPrinterStatus"),
    document.getElementById("printerHealthLine"),
  ].filter(Boolean);

  const apply = (text, tone) => {
    for (const el of targets) {
      el.textContent = text;
      const header = el.id === "headerPrinterStatus";
      el.className = header ? `text-xs mt-0.5 truncate ${tone}` : `text-xs mt-1 ${tone}`;
    }
  };

  const name = settings?.printerName;
  if (!name) {
    apply("Impressora: nenhuma selecionada", "text-bad");
    return { ok: false, offline: false, missing: true };
  }

  try {
    const list = await api("/api/printers");
    const found = (list || []).find((p) => p.name === name);
    if (!found) {
      apply(`Impressora: '${name}' não encontrada`, "text-bad");
      return { ok: false, offline: false, missing: true };
    }
    if (found.isOffline) {
      apply(`Impressora: '${name}' offline / sem papel`, "text-bad");
      return { ok: false, offline: true, missing: false };
    }
    const where = found.connection || found.port || "local";
    apply(`Impressora: '${name}' pronta · ${where}`, "text-sea-glow");
    return { ok: true, offline: false, missing: false };
  } catch {
    apply(`Impressora: '${name}' (status indisponível)`, "text-mist");
    return { ok: null, offline: false, missing: false };
  }
}

export function modeLabel({ paused, simulation } = {}) {
  const base = simulation ? "Simulação" : "Real";
  return paused ? `${base} · pausa` : base;
}

export function renderStats({ mode, queue, done, bad, healthLine }) {
  const modeEl = document.getElementById("statMode");
  if (modeEl && mode != null) modeEl.textContent = mode;
  const queueEl = document.getElementById("statQueue");
  if (queueEl && queue != null) queueEl.textContent = queue;
  const doneEl = document.getElementById("statDone");
  if (doneEl && done != null) doneEl.textContent = done;
  const badEl = document.getElementById("statBad");
  if (badEl && bad != null) badEl.textContent = bad;
  const healthEl = document.getElementById("healthLine");
  if (healthEl && healthLine != null) healthEl.textContent = healthLine;
}

export function setConnection(ok, application = "SoftPrint") {
  const el = document.getElementById("connDot");
  if (!el) return;
  if (ok) {
    el.innerHTML = `<span class="w-2.5 h-2.5 rounded-full bg-sea-glow animate-pulse"></span> ${application} conectado`;
    el.className = "inline-flex items-center gap-2 text-sm text-sea-glow";
  } else {
    el.innerHTML = `<span class="w-2.5 h-2.5 rounded-full bg-bad"></span> Sem conexão`;
    el.className = "inline-flex items-center gap-2 text-sm text-bad";
  }
}

export function setApiHint(baseUrl) {
  const hint = document.getElementById("apiHint");
  if (!hint) return;
  try {
    const host = baseUrl ? new URL(baseUrl).host : location.host;
    hint.textContent = host || "—";
  } catch {
    hint.textContent = location.host || "—";
  }
}

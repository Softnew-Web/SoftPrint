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

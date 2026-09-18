export function renderStats({ mode, queue, done, bad, healthLine }) {
  document.getElementById("statMode").textContent = mode;
  document.getElementById("statQueue").textContent = queue;
  document.getElementById("statDone").textContent = done;
  document.getElementById("statBad").textContent = bad;
  document.getElementById("healthLine").textContent = healthLine;
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

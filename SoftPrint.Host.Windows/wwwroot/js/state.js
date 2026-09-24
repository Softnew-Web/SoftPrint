export const state = {
  jobs: [],
  selectedId: null,
  applied: null,
  dirty: false,
  inboxDirty: false,
};

export function feedback(msg, err = false) {
  const el = document.getElementById("feedback");
  if (!el) return;
  el.textContent = msg || "";
  el.className = `text-sm min-h-[1.25rem] ${err ? "text-bad" : "text-sea-glow"}`;
}

export function openDlg(title, body) {
  const dlg = document.getElementById("dlg");
  dlg.classList.remove("max-w-3xl");
  dlg.classList.add("max-w-2xl");
  document.getElementById("dlgTitle").textContent = title;
  const el = document.getElementById("dlgBody");
  el.className = "p-5 text-sm whitespace-pre-wrap overflow-auto max-h-[70vh] scroll-thin leading-relaxed font-body";
  el.textContent = body;
  dlg.showModal();
}

/** Diálogo com HTML formatado (logs, explicação de pedido). */
export function openDlgHtml(title, html, { wide = false } = {}) {
  const dlg = document.getElementById("dlg");
  dlg.classList.toggle("max-w-3xl", wide);
  dlg.classList.toggle("max-w-2xl", !wide);
  document.getElementById("dlgTitle").textContent = title;
  const el = document.getElementById("dlgBody");
  el.className = "p-5 text-sm overflow-auto max-h-[75vh] scroll-thin leading-relaxed font-body";
  el.innerHTML = html;
  dlg.showModal();
}

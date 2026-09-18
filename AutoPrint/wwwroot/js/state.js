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
  document.getElementById("dlgTitle").textContent = title;
  document.getElementById("dlgBody").textContent = body;
  document.getElementById("dlg").showModal();
}

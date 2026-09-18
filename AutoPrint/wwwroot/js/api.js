export function createApi(key) {
  const headers = {
    "X-AutoPrint-Key": key,
    "Content-Type": "application/json",
  };

  async function api(path, opts = {}) {
    const res = await fetch(path, {
      ...opts,
      headers: { ...headers, ...(opts.headers || {}) },
    });
    if (!res.ok) {
      let msg = `Erro ${res.status}`;
      try {
        const j = await res.json();
        if (j.error) msg = j.error;
      } catch {}
      throw new Error(msg);
    }
    if (res.status === 204) return null;
    const text = await res.text();
    return text ? JSON.parse(text) : null;
  }

  return { api, headers };
}

export const statusLabel = {
  pending: "Na fila",
  processing: "Enviando",
  simulated: "Simulado",
  sent: "Enviado ao Windows",
  uncertain: "Conferir envio",
};

export function escapeHtml(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])
  );
}

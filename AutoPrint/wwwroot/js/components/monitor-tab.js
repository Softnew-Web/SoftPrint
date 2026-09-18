import { state, feedback, openDlg } from "../state.js";
import { statusLabel, escapeHtml } from "../api.js";

export function bindMonitorTab({ api, onChanged }) {
  const body = document.getElementById("jobsBody");
  const steps = document.getElementById("steps");
  const err = document.getElementById("errorBox");
  const pipeline = document.getElementById("pipeline");

  document.getElementById("statusFilter").addEventListener("change", renderJobs);
  document.getElementById("refFilter").addEventListener("input", renderJobs);
  document.getElementById("btnSendTest").addEventListener("click", sendTest);
  document.getElementById("btnReprint").addEventListener("click", reprintSelected);
  document.getElementById("btnExplain").addEventListener("click", showFull);
  document.getElementById("btnExportJson").addEventListener("click", () => exportJobs(false));
  document.getElementById("btnExportCsv").addEventListener("click", () => exportJobs(true));

  function renderJobs() {
    const sf = document.getElementById("statusFilter").value;
    const rf = document.getElementById("refFilter").value.trim().toLowerCase();
    const filtered = state.jobs.filter(
      (j) =>
        (!sf || j.status === sf) &&
        (!rf || (j.reference || "").toLowerCase().includes(rf))
    );

    body.innerHTML =
      filtered
        .slice()
        .reverse()
        .map(
          (j) => `
      <tr data-id="${j.id}" class="job-row border-t border-ink-line cursor-pointer hover:bg-ink/60 ${
            state.selectedId === j.id ? "bg-ink" : ""
          } ${j.status === "uncertain" ? "text-bad" : ""}">
        <td class="px-3 py-2.5 font-medium">${escapeHtml(j.reference)}</td>
        <td class="px-3 py-2.5 text-mist">${escapeHtml(j.jobType || "default")}</td>
        <td class="px-3 py-2.5">${escapeHtml(statusLabel[j.status] || j.status)}</td>
        <td class="px-3 py-2.5 text-mist text-xs">${new Date(j.createdAt).toLocaleString()}</td>
      </tr>`
        )
        .join("") ||
      `<tr><td colspan="4" class="px-3 py-8 text-mist text-center">Nenhum pedido nesta aba.</td></tr>`;

    body.querySelectorAll(".job-row").forEach((row) => {
      row.addEventListener("click", () => {
        state.selectedId = row.dataset.id;
        renderJobs();
      });
    });

    const sel =
      state.jobs.find((j) => j.id === state.selectedId) ||
      filtered[filtered.length - 1] ||
      null;
    if (sel) state.selectedId = sel.id;
    showTrace(sel);
  }

  function showTrace(job) {
    if (!job) {
      steps.innerHTML = `<p class="text-mist">Selecione um pedido para ver o passo a passo.</p>`;
      err.textContent = "Sem erro neste pedido.";
      err.className =
        "rounded-xl bg-ink border border-ink-line p-3 text-sm leading-relaxed text-mist";
      return;
    }

    const list = job.steps || [];
    steps.innerHTML = list.length
      ? list
          .map(
            (s, i) => `
      <div class="pl-4 timeline-line relative">
        <span class="absolute -left-[5px] top-1.5 w-2 h-2 rounded-full ${
          s.isError ? "bg-bad" : "bg-sea-glow"
        }"></span>
        <p class="font-medium ${s.isError ? "text-bad" : ""}">${i + 1}. ${escapeHtml(
              s.message
            )}</p>
        <p class="text-xs text-mist mt-0.5">Onde: ${escapeHtml(s.where)}</p>
        ${
          s.detail
            ? `<p class="text-xs text-mist/80 mt-0.5">${escapeHtml(s.detail)}</p>`
            : ""
        }
        <p class="text-[11px] text-mist/60 mt-1">${new Date(s.at).toLocaleTimeString()}</p>
      </div>`
          )
          .join("")
      : `<p class="text-mist">Ainda sem passos.</p>`;

    if (job.error || job.status === "uncertain") {
      err.innerHTML = `<p class="text-bad font-semibold mb-1">Erro / por que conferir</p>
        <p><span class="text-mist">O que:</span> ${escapeHtml(job.error || "uncertain")}</p>
        <p><span class="text-mist">Onde:</span> ${escapeHtml(job.errorWhere || "—")}</p>
        <p><span class="text-mist">Por quê:</span> ${escapeHtml(
          job.errorReason || "Confira antes de reenviar."
        )}</p>`;
      err.className =
        "rounded-xl bg-bad/10 border border-bad/40 p-3 text-sm leading-relaxed";
    } else {
      err.textContent =
        job.status === "pending"
          ? "Aguardando o PrintWorker."
          : job.status === "processing"
            ? "Em processamento agora."
            : job.status === "simulated"
              ? "Concluído em simulação."
              : job.status === "sent"
                ? "Enviado ao spooler (não garante papel)."
                : "Sem erro.";
      err.className =
        "rounded-xl bg-ink border border-ink-line p-3 text-sm leading-relaxed text-mist";
    }
  }

  function updatePipeline() {
    const processing = state.jobs.find((j) => j.status === "processing");
    const applied = state.applied;
    pipeline.textContent = !applied
      ? "Aguardando…"
      : applied.paused
        ? "Processamento: PAUSADO — novos pedidos ficam na fila."
        : processing
          ? `Processando agora: ${processing.reference}`
          : applied.simulation
            ? "Processamento: ocioso • modo simulação."
            : `Processamento: ocioso • modo real → '${applied.printerName || "—"}'.`;
  }

  async function sendTest() {
    if (state.dirty) return feedback("Salve as configurações antes de testar.", true);
    if (
      state.applied &&
      !state.applied.simulation &&
      !confirm(`Enviar para ${state.applied.printerName}?`)
    )
      return;
    try {
      await api("/api/jobs", {
        method: "POST",
        body: JSON.stringify({
          reference: "teste-" + Math.random().toString(16).slice(2, 12),
          text: document.getElementById("sample").value,
          jobType: document.getElementById("jobType").value || "default",
          template: document.getElementById("template").value || null,
        }),
      });
      feedback("Teste recebido. Acompanhe no histórico.");
      onChanged?.();
    } catch (err) {
      feedback(err.message, true);
    }
  }

  async function reprintSelected() {
    const job = state.jobs.find((j) => j.id === state.selectedId);
    if (!job) return feedback("Selecione um pedido.", true);
    if (!confirm(`Reimprimir ${job.reference}?`)) return;
    try {
      await api(`/api/jobs/${job.id}/reprint`, { method: "POST", body: "{}" });
      feedback("Reimpressão enfileirada.");
      onChanged?.();
    } catch (err) {
      feedback(err.message, true);
    }
  }

  function showFull() {
    const job = state.jobs.find((j) => j.id === state.selectedId);
    if (!job) return feedback("Selecione um pedido.", true);
    const lines = [
      `Pedido: ${job.reference}`,
      `Situação: ${statusLabel[job.status] || job.status}`,
      `Tipo: ${job.jobType}`,
      `Impressora: ${job.printerName || "—"}`,
      "",
      "PASSOS",
      ...(job.steps || []).map(
        (s, i) =>
          `${i + 1}. [${new Date(s.at).toLocaleTimeString()}] ${s.message}\n   Onde: ${s.where}${
            s.detail ? "\n   " + s.detail : ""
          }`
      ),
      "",
      job.error ? `ERRO\n  ${job.error}\n  ${job.errorWhere}\n  ${job.errorReason}` : "",
      "",
      "TEXTO",
      job.text,
    ];
    openDlg("Explicação do pedido", lines.filter(Boolean).join("\n"));
  }

  function exportJobs(csv) {
    const data = state.jobs.slice().reverse();
    let blob, name;
    if (csv) {
      const rows = [["reference", "type", "status", "printer", "created"].join(",")].concat(
        data.map((j) =>
          [j.reference, j.jobType, j.status, j.printerName || "", j.createdAt]
            .map((v) => `"${String(v).replaceAll('"', '""')}"`)
            .join(",")
        )
      );
      blob = new Blob([rows.join("\n")], { type: "text/csv" });
      name = "autoprin-jobs.csv";
    } else {
      blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
      name = "autoprin-jobs.json";
    }
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
  }

  return { renderJobs, updatePipeline };
}

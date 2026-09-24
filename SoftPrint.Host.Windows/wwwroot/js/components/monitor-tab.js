import { state, feedback, openDlgHtml } from "../state.js";
import { statusLabel, escapeHtml } from "../api.js";
import { buildSettingsPayload } from "../settings-payload.js";
import { refreshHeaderPrinterStatus } from "./stats.js";

const STATUS_OPTIONS = [
  { value: "pending", label: "Na fila" },
  { value: "processing", label: "Enviando" },
  { value: "simulated", label: "Simulado" },
  { value: "sent", label: "Enviado" },
  { value: "uncertain", label: "Conferir" },
];

export function bindMonitorTab({ api, onChanged }) {
  const body = document.getElementById("jobsBody");
  const steps = document.getElementById("steps");
  const err = document.getElementById("errorBox");
  const pipeline = document.getElementById("pipeline");
  const filterBtn = document.getElementById("statusFilterBtn");
  const filterMenu = document.getElementById("statusFilterMenu");
  const filterLabel = document.getElementById("statusFilterLabel");
  const filterWrap = document.getElementById("statusFilterWrap");

  const selectedStatuses = () =>
    [...document.querySelectorAll(".status-filter-opt:checked")].map((el) => el.value);

  const syncFilterLabel = () => {
    if (!filterLabel) return;
    const selected = selectedStatuses();
    if (!selected.length) {
      filterLabel.textContent = "Todos";
      return;
    }
    if (selected.length === 1) {
      const opt = STATUS_OPTIONS.find((o) => o.value === selected[0]);
      filterLabel.textContent = opt?.label || selected[0];
      return;
    }
    filterLabel.textContent = `${selected.length} situações`;
  };

  filterBtn?.addEventListener("click", (e) => {
    e.stopPropagation();
    filterMenu?.classList.toggle("hidden");
  });
  filterMenu?.addEventListener("click", (e) => e.stopPropagation());
  document.addEventListener("click", (e) => {
    if (!filterWrap || filterWrap.contains(e.target)) return;
    filterMenu?.classList.add("hidden");
  });
  document.querySelectorAll(".status-filter-opt").forEach((el) => {
    el.addEventListener("change", () => {
      syncFilterLabel();
      renderJobs();
    });
  });
  document.getElementById("statusFilterClear")?.addEventListener("click", () => {
    document.querySelectorAll(".status-filter-opt").forEach((el) => {
      el.checked = false;
    });
    syncFilterLabel();
    renderJobs();
  });

  document.getElementById("refFilter").addEventListener("input", renderJobs);
  document.getElementById("btnSendTest").addEventListener("click", sendTest);
  document.getElementById("btnGuidedTest")?.addEventListener("click", sendGuidedTest);
  document.getElementById("btnReprint").addEventListener("click", reprintSelected);
  document.getElementById("btnReprintBulk")?.addEventListener("click", reprintBulk);
  document.getElementById("btnExplain").addEventListener("click", showFullExplanation);
  document.getElementById("btnExportJson").addEventListener("click", () => exportJobs(false));
  document.getElementById("btnExportCsv").addEventListener("click", () => exportJobs(true));
  document.getElementById("btnQueuePause")?.addEventListener("click", () => setPaused(true));
  document.getElementById("btnQueueResume")?.addEventListener("click", () => setPaused(false));
  document.getElementById("jobsSelectAll")?.addEventListener("change", (e) => {
    const on = !!e.target.checked;
    body.querySelectorAll(".job-check").forEach((el) => {
      el.checked = on;
    });
  });

  function renderJobs() {
    const statuses = selectedStatuses();
    const rf = document.getElementById("refFilter").value.trim().toLowerCase();
    const filtered = state.jobs.filter(
      (j) =>
        (!statuses.length || statuses.includes(j.status)) &&
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
        <td class="px-3 py-2.5" onclick="event.stopPropagation()">
          <input type="checkbox" class="job-check accent-sea" data-id="${j.id}" aria-label="Selecionar ${escapeHtml(
            j.reference
          )}" />
        </td>
        <td class="px-3 py-2.5 font-medium">${escapeHtml(j.reference)}</td>
        <td class="px-3 py-2.5 text-mist">${escapeHtml(j.jobType || "default")}</td>
        <td class="px-3 py-2.5">${escapeHtml(statusLabel[j.status] || j.status)}</td>
        <td class="px-3 py-2.5 text-mist text-xs">${new Date(j.createdAt).toLocaleString()}</td>
      </tr>`
        )
        .join("") ||
      `<tr><td colspan="5" class="px-3 py-8 text-mist text-center">Nenhum pedido nesta aba.</td></tr>`;

    body.querySelectorAll(".job-row").forEach((row) => {
      row.addEventListener("click", () => {
        state.selectedId = row.dataset.id;
        renderJobs();
      });
    });

    const sel =
      state.jobs.find((j) => j.id === state.selectedId) ||
      filtered.at(-1) ||
      null;
    if (sel) state.selectedId = sel.id;
    showTrace(sel);
  }

  syncFilterLabel();

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
      ? list.map((s, i) => renderTraceStep(s, i)).join("")
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
      return;
    }

    err.textContent = idleTraceMessage(job.status);
    err.className =
      "rounded-xl bg-ink border border-ink-line p-3 text-sm leading-relaxed text-mist";
  }

  function updatePipeline() {
    const processing = state.jobs.find((j) => j.status === "processing");
    const pendingCount = state.jobs.filter((j) => j.status === "pending").length;
    const applied = state.applied;
    const pauseBtn = document.getElementById("btnQueuePause");
    const resumeBtn = document.getElementById("btnQueueResume");
    if (pauseBtn) pauseBtn.disabled = !!applied?.paused;
    if (resumeBtn) resumeBtn.disabled = !applied?.paused;

    pipeline.textContent = pipelineStatusText(applied, processing, pendingCount);
    void refreshHeaderPrinterStatus(api, state.applied);
  }

  async function setPaused(paused) {
    if (state.dirty) return feedback("Salve as configurações antes de alterar a fila.", true);
    if (!state.applied) return feedback("Configurações ainda não carregadas.", true);
    try {
      await api("/api/settings", {
        method: "PUT",
        body: JSON.stringify(buildSettingsPayload({ paused })),
      });
      feedback(paused ? "Fila pausada." : "Fila retomada.");
      onChanged?.();
    } catch (e) {
      feedback(e.message, true);
    }
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
          reference: "teste-" + randomToken(),
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

  async function sendGuidedTest() {
    if (state.dirty) return feedback("Salve as configurações antes do teste guiado.", true);
    if (!state.applied) return feedback("Configurações ainda não carregadas.", true);

    const paper = (state.applied.paperSize || "").toLowerCase();
    if (paper !== "padrao") {
      const ok = confirm(
        "O teste guiado usa o papel configurado agora.\n\n" +
          "Recomendado: Padrão (200×70). Deseja mudar para Padrão e salvar antes de imprimir?"
      );
      if (ok) {
        try {
          await api("/api/settings", {
            method: "PUT",
            body: JSON.stringify(
              buildSettingsPayload({
                paperSize: "padrao",
                paperWidthMm: 200,
                paperHeightMm: 70,
                paperLandscape: false,
              })
            ),
          });
          await onChanged?.();
        } catch (e) {
          return feedback(e.message, true);
        }
      }
    }

    if (!state.applied.simulation && !confirm(`Teste guiado para ${state.applied.printerName}?`))
      return;

    const started = Date.now();
    feedback("Teste guiado enviado…");
    try {
      const job = await api("/api/jobs/guided-test", { method: "POST", body: "{}" });
      await onChanged?.();
      const done = await waitForJob(job.id, 45_000);
      const sec = ((Date.now() - started) / 1000).toFixed(1);
      if (!done) {
        feedback(`Teste ainda na fila após ${sec}s — acompanhe no histórico.`);
        return;
      }
      if (done.status === "uncertain") {
        feedback(`Teste falhou em ${sec}s — veja o erro no pedido.`, true);
        return;
      }
      feedback(
        `Teste guiado OK em ${sec}s (${done.status === "simulated" ? "simulação" : "spooler"}). Confira o papel na impressora.`
      );
    } catch (err) {
      feedback(err.message, true);
    }
  }

  async function waitForJob(id, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      await onChanged?.();
      const job = state.jobs.find((j) => j.id === id);
      if (job && job.status !== "pending" && job.status !== "processing") return job;
      await new Promise((r) => setTimeout(r, 700));
    }
    return null;
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

  async function reprintBulk() {
    const ids = [...body.querySelectorAll(".job-check:checked")].map((el) => el.dataset.id).filter(Boolean);
    if (!ids.length) return feedback("Marque um ou mais pedidos na lista.", true);
    if (!confirm(`Reimprimir ${ids.length} pedido(s)?`)) return;
    try {
      const result = await api("/api/jobs/reprint-bulk", {
        method: "POST",
        body: JSON.stringify({ ids }),
      });
      const n = result.count || 0;
      const failed = (result.errors || []).length;
      feedback(
        failed
          ? `${n} reimpressão(ões) ok, ${failed} falha(s).`
          : `${n} reimpressão(ões) enfileirada(s).`
      );
      onChanged?.();
    } catch (err) {
      feedback(err.message, true);
    }
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
      name = "softprint-jobs.csv";
    } else {
      blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
      name = "softprint-jobs.json";
    }
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
  }

  return { renderJobs, updatePipeline };
}

function showFullExplanation() {
  const job = state.jobs.find((j) => j.id === state.selectedId);
  if (!job) return feedback("Selecione um pedido.", true);
  openDlgHtml("Explicação do pedido", renderJobExplanation(job), { wide: true });
}

function randomToken() {
  const bytes = new Uint8Array(5);
  crypto.getRandomValues(bytes);
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function idleTraceMessage(status) {
  if (status === "pending") return "Aguardando o PrintWorker.";
  if (status === "processing") return "Em processamento agora.";
  if (status === "simulated") return "Concluído em simulação.";
  if (status === "sent") return "Enviado ao spooler (não garante papel).";
  return "Sem erro.";
}

function pipelineStatusText(applied, processing, pendingCount) {
  if (!applied) return "Aguardando…";
  if (applied.paused) return `Fila PAUSADA — ${pendingCount} aguardando. Use Retomar para continuar.`;
  if (processing) return `Processando agora: ${processing.reference} · ${pendingCount} na fila`;
  if (applied.simulation) return `Ocioso · simulação · ${pendingCount} na fila`;
  return `Ocioso · real → '${applied.printerName || "—"}' · ${pendingCount} na fila`;
}

function renderTraceStep(s, i) {
  const detail = s.detail
    ? `<p class="text-xs text-mist/80 mt-0.5">${escapeHtml(s.detail)}</p>`
    : "";
  return `
      <div class="pl-4 timeline-line relative">
        <span class="absolute -left-[5px] top-1.5 w-2 h-2 rounded-full ${
          s.isError ? "bg-bad" : "bg-sea-glow"
        }"></span>
        <p class="font-medium ${s.isError ? "text-bad" : ""}">${i + 1}. ${escapeHtml(s.message)}</p>
        <p class="text-xs text-mist mt-0.5">Onde: ${escapeHtml(s.where)}</p>
        ${detail}
        <p class="text-[11px] text-mist/60 mt-1">${new Date(s.at).toLocaleTimeString()}</p>
      </div>`;
}

function jobStatusBadge(status) {
  const s = (status || "").toLowerCase();
  const label = statusLabel[s] || status || "—";
  if (s === "sent")
    return { label, css: "bg-sea/20 text-sea-glow border-sea/30", tone: "ok" };
  if (s === "simulated")
    return { label, css: "bg-good/15 text-good border-good/30", tone: "ok" };
  if (s === "uncertain")
    return { label, css: "bg-bad/20 text-bad border-bad/30", tone: "bad" };
  if (s === "processing")
    return { label, css: "bg-sea/15 text-sea-glow border-sea/25", tone: "mid" };
  if (s === "pending")
    return { label, css: "bg-warn/15 text-warn border-warn/30", tone: "mid" };
  return { label, css: "bg-ink-line text-mist border-ink-line", tone: "mid" };
}

function formatTime(at) {
  try {
    return new Date(at).toLocaleTimeString("pt-BR", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
  } catch {
    return "—";
  }
}

function formatDateTime(at) {
  try {
    return new Date(at).toLocaleString("pt-BR", {
      day: "2-digit",
      month: "2-digit",
      year: "numeric",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
  } catch {
    return "—";
  }
}

function renderJobExplanation(job) {
  const badge = jobStatusBadge(job.status);
  const steps = job.steps || [];
  const meta = [
    ["Tipo", job.jobType || "default"],
    ["Impressora", job.printerName || "—"],
    ["Criado", formatDateTime(job.createdAt)],
    job.finishedAt ? ["Concluído", formatDateTime(job.finishedAt)] : null,
    job.contentKind ? ["Conteúdo", job.contentKind] : null,
  ].filter(Boolean);

  const stepsHtml = steps.length
    ? `<ol class="space-y-0">${steps.map((s, i) => renderExplanationStep(s, i, steps.length)).join("")}</ol>`
    : `<p class="text-mist text-sm px-1 py-4">Ainda sem passos registrados.</p>`;

  return `
    <div class="space-y-5">
      <header class="rounded-2xl border border-ink-line bg-ink/60 p-4">
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div class="min-w-0">
            <p class="text-[11px] uppercase tracking-wider text-sea-glow font-semibold">Pedido</p>
            <h4 class="font-display text-xl font-bold text-white mt-0.5 truncate" title="${escapeHtml(
              job.reference
            )}">${escapeHtml(job.reference || "—")}</h4>
          </div>
          <span class="shrink-0 rounded-full border px-3 py-1 text-xs font-semibold ${badge.css}">${escapeHtml(
            badge.label
          )}</span>
        </div>
        <dl class="mt-3 grid sm:grid-cols-2 gap-x-4 gap-y-2 text-sm">
          ${meta
            .map(
              ([k, v]) =>
                `<div class="min-w-0"><dt class="text-[11px] text-mist">${escapeHtml(
                  k
                )}</dt><dd class="text-paper truncate" title="${escapeHtml(v)}">${escapeHtml(
                  v
                )}</dd></div>`
            )
            .join("")}
        </dl>
      </header>

      <section>
        <div class="flex items-center justify-between gap-2 mb-3">
          <h4 class="text-[11px] uppercase tracking-wider text-mist font-semibold">Passo a passo</h4>
          <span class="text-[11px] text-mist">${steps.length} etapa${steps.length === 1 ? "" : "s"}</span>
        </div>
        ${stepsHtml}
      </section>

      ${renderExplanationError(job)}

      <section>
        <h4 class="text-[11px] uppercase tracking-wider text-mist font-semibold mb-2">Conteúdo enviado</h4>
        ${renderJobTextPreview(job.text)}
      </section>
    </div>`;
}

function renderExplanationStep(s, i, total) {
  const bad = !!s.isError;
  const last = i === total - 1;
  const connector = last
    ? ""
    : `<span class="mt-1 w-px flex-1 min-h-[1.25rem] bg-ink-line"></span>`;
  const where = s.where
    ? `<p class="mt-1.5 text-[11px] text-mist"><span class="text-mist/60">Onde</span> · ${escapeHtml(
        s.where
      )}</p>`
    : "";
  const detail = s.detail
    ? `<p class="mt-1 text-[11px] text-paper/75 leading-relaxed">${escapeHtml(s.detail)}</p>`
    : "";
  return `<li class="grid grid-cols-[2rem_1fr] gap-3">
            <div class="flex flex-col items-center">
              <span class="mt-1 flex h-6 w-6 items-center justify-center rounded-full text-[11px] font-bold ${
                bad ? "bg-bad/20 text-bad" : "bg-sea/20 text-sea-glow"
              }">${i + 1}</span>
              ${connector}
            </div>
            <div class="pb-4 min-w-0">
              <div class="rounded-xl border ${
                bad ? "border-bad/35 bg-bad/5" : "border-ink-line bg-ink/50"
              } px-3 py-2.5">
                <div class="flex items-start justify-between gap-2">
                  <p class="text-paper font-medium text-sm leading-snug">${escapeHtml(s.message)}</p>
                  <time class="shrink-0 text-[11px] text-mist tabular-nums">${escapeHtml(
                    formatTime(s.at)
                  )}</time>
                </div>
                ${where}
                ${detail}
              </div>
            </div>
          </li>`;
}

function renderExplanationError(job) {
  if (!(job.error || job.status === "uncertain")) return "";
  return `<section class="rounded-xl border border-bad/40 bg-bad/10 p-4 space-y-2">
          <h4 class="text-bad font-semibold text-sm">Erro / por que conferir</h4>
          <dl class="space-y-1.5 text-sm">
            <div><dt class="text-mist text-[11px]">O quê</dt><dd class="text-paper">${escapeHtml(
              job.error || "uncertain"
            )}</dd></div>
            <div><dt class="text-mist text-[11px]">Onde</dt><dd class="text-paper">${escapeHtml(
              job.errorWhere || "—"
            )}</dd></div>
            <div><dt class="text-mist text-[11px]">Por quê</dt><dd class="text-paper">${escapeHtml(
              job.errorReason || "Confira antes de reenviar."
            )}</dd></div>
          </dl>
        </section>`;
}

function renderJobTextPreview(text) {
  const raw = String(text ?? "").trim();
  if (!raw) {
    return `<p class="rounded-xl border border-ink-line bg-ink/40 px-3 py-4 text-mist text-sm">Sem texto neste pedido.</p>`;
  }

  const lines = raw.split(/\r?\n/);
  const title = lines[0] || "";
  const guided = parseGuidedTestText(title, lines);
  if (guided) return guided;

  return `<pre class="rounded-xl border border-ink-line bg-ink/70 px-4 py-3 text-[13px] text-paper/90 whitespace-pre-wrap break-words font-body leading-relaxed max-h-56 overflow-auto scroll-thin">${escapeHtml(
    raw
  )}</pre>`;
}

function parseGuidedTestText(title, lines) {
  if (!/^TESTE GUIADO/i.test(title)) return null;

  const fields = [];
  let note = "";
  for (let i = 1; i < lines.length; i++) {
    const line = lines[i];
    if (/^-+$/.test(line.trim())) continue;
    const field = parseGuidedFieldLine(line);
    if (field) {
      fields.push(field);
      continue;
    }
    if (line.trim() === "") continue;
    if (fields.length) {
      note = lines.slice(i).join("\n").trim();
      break;
    }
  }
  if (!fields.length) return null;

  const noteHtml = note
    ? `<p class="px-4 pb-3 text-[12px] text-mist leading-relaxed border-t border-ink-line/60 pt-3">${escapeHtml(
        note
      )}</p>`
    : "";

  return `<div class="rounded-xl border border-sea/30 bg-gradient-to-b from-sea/10 to-ink/40 overflow-hidden">
      <div class="px-4 py-3 border-b border-ink-line/80 flex items-center gap-2">
        <span class="rounded-md bg-sea/20 text-sea-glow text-[10px] font-bold uppercase tracking-wider px-2 py-0.5">Teste</span>
        <p class="text-paper font-semibold text-sm">${escapeHtml(title)}</p>
      </div>
      <dl class="px-4 py-3 grid sm:grid-cols-2 gap-3 text-sm">
        ${fields
          .map(
            ([k, v]) =>
              `<div><dt class="text-[11px] text-mist">${escapeHtml(k)}</dt><dd class="text-paper">${escapeHtml(
                v
              )}</dd></div>`
          )
          .join("")}
      </dl>
      ${noteHtml}
    </div>`;
}

function parseGuidedFieldLine(line) {
  const colon = line.indexOf(":");
  if (colon <= 0) return null;
  const key = line.slice(0, colon).trim();
  const value = line.slice(colon + 1).trim();
  if (!value) return null;
  if (key !== "Papel" && key !== "Impressora" && key !== "Modo" && key !== "Quando") return null;
  return [key, value];
}

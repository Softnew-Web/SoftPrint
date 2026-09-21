/** Espelha ImageLayoutCalculator / PaperSizeCatalog do backend. */

export const PAPER_PRESETS = {
  a4: { w: 210, h: 297, label: "A4" },
  a5: { w: 148, h: 210, label: "A5" },
  letter: { w: 215.9, h: 279.4, label: "Letter" },
  legal: { w: 215.9, h: 355.6, label: "Legal" },
  photo4x6: { w: 101.6, h: 152.4, label: "Foto 10×15" },
  receipt58: { w: 58, h: 200, label: "Cupom 58 mm" },
  receipt80: { w: 80, h: 297, label: "Cupom 80 mm" },
};

export function resolvePaperMm(kind, widthMm, heightMm, landscape) {
  let w;
  let h;
  if (kind === "custom") {
    w = Math.min(1200, Math.max(20, Number(widthMm) || 210));
    h = Math.min(1200, Math.max(20, Number(heightMm) || 297));
  } else {
    const preset = PAPER_PRESETS[kind] || PAPER_PRESETS.a4;
    w = preset.w;
    h = preset.h;
  }
  return landscape ? { w: h, h: w } : { w, h };
}

export function computeDestination(pageX, pageY, pageW, pageH, imageW, imageH, fit, scalePercent) {
  const scale = Math.min(200, Math.max(10, scalePercent)) / 100;
  const fitMode = (fit || "contain").toLowerCase();

  if (fitMode === "stretch") {
    return { x: pageX, y: pageY, w: pageW, h: pageH };
  }
  if (fitMode === "center") {
    const w = imageW * scale;
    const h = imageH * scale;
    return { x: pageX + (pageW - w) / 2, y: pageY + (pageH - h) / 2, w, h };
  }
  if (fitMode === "cover") {
    const ratio = Math.max(pageW / imageW, pageH / imageH) * scale;
    const w = imageW * ratio;
    const h = imageH * ratio;
    return { x: pageX + (pageW - w) / 2, y: pageY + (pageH - h) / 2, w, h };
  }
  const ratio = Math.min(pageW / imageW, pageH / imageH) * scale;
  const w = imageW * ratio;
  const h = imageH * ratio;
  return { x: pageX + (pageW - w) / 2, y: pageY + (pageH - h) / 2, w, h };
}

export function drawPaperPreview(canvas, image, fit, scalePercent, paper) {
  const ctx = canvas.getContext("2d");
  if (!ctx) return null;

  const paperWmm = paper?.w || 210;
  const paperHmm = paper?.h || 297;
  const paperRatio = paperWmm / Math.max(1e-6, paperHmm);

  // Ajusta o canvas à proporção do papel, preenchendo o container (sem “folha minúscula”).
  const parent = canvas.parentElement;
  const maxW = Math.max(180, parent?.clientWidth || canvas.clientWidth || 520);
  const maxH = Math.max(180, parent?.clientHeight || canvas.clientHeight || 680);
  const dpr = Math.min(typeof window !== "undefined" ? window.devicePixelRatio || 1 : 1, 2);
  const padCss = 10;
  let cssW = maxW - padCss;
  let cssH = cssW / paperRatio;
  if (cssH > maxH - padCss) {
    cssH = maxH - padCss;
    cssW = cssH * paperRatio;
  }
  cssW = Math.max(120, Math.floor(cssW));
  cssH = Math.max(120, Math.floor(cssH));
  canvas.style.width = `${cssW}px`;
  canvas.style.height = `${cssH}px`;
  const W = Math.round(cssW * dpr);
  const H = Math.round(cssH * dpr);
  if (canvas.width !== W) canvas.width = W;
  if (canvas.height !== H) canvas.height = H;

  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, W, H);
  ctx.fillStyle = "#121a22";
  ctx.fillRect(0, 0, W, H);

  // Papel quase tela cheia (só uma margem mínima para borda/rótulo).
  const edge = Math.max(4 * dpr, Math.min(W, H) * 0.015);
  const labelRoom = 16 * dpr;
  let paperW = W - edge * 2;
  let paperH = paperW / paperRatio;
  if (paperH > H - edge * 2 - labelRoom) {
    paperH = H - edge * 2 - labelRoom;
    paperW = paperH * paperRatio;
  }
  const paperX = (W - paperW) / 2;
  const paperY = (H - paperH - labelRoom) / 2;
  const fallbackMargin = Math.min(paperW, paperH) * 0.04;
  const margins = paper?.margins || {};
  const mmToX = paperW / paperWmm;
  const mmToY = paperH / paperHmm;
  const marginLeft = Number.isFinite(margins.leftMm) ? margins.leftMm * mmToX : fallbackMargin;
  const marginRight = Number.isFinite(margins.rightMm) ? margins.rightMm * mmToX : fallbackMargin;
  const marginTop = Number.isFinite(margins.topMm) ? margins.topMm * mmToY : fallbackMargin;
  const marginBottom = Number.isFinite(margins.bottomMm) ? margins.bottomMm * mmToY : fallbackMargin;
  const area = {
    x: paperX + marginLeft,
    y: paperY + marginTop,
    w: Math.max(1, paperW - marginLeft - marginRight),
    h: Math.max(1, paperH - marginTop - marginBottom),
  };

  ctx.fillStyle = "#f4f7fa";
  ctx.strokeStyle = "#2a3644";
  ctx.lineWidth = Math.max(1, dpr);
  ctx.fillRect(paperX, paperY, paperW, paperH);
  ctx.strokeRect(paperX, paperY, paperW, paperH);

  ctx.strokeStyle = "#0f766e";
  ctx.lineWidth = Math.max(2, 2.25 * dpr);
  ctx.setLineDash([8 * dpr, 5 * dpr]);
  ctx.strokeRect(area.x, area.y, area.w, area.h);
  ctx.setLineDash([]);
  ctx.fillStyle = "#0f766e";
  ctx.font = `600 ${Math.max(11, 11 * dpr)}px 'IBM Plex Sans', sans-serif`;
  ctx.textAlign = "left";
  ctx.fillText("área imprimível aproximada", area.x + 5 * dpr, area.y + 14 * dpr);

  ctx.fillStyle = "#6b7c8c";
  ctx.font = `${Math.max(11, 11 * dpr)}px 'IBM Plex Sans', sans-serif`;
  ctx.textAlign = "center";
  const sizeLabel = `${fmtMm(paperWmm)}×${fmtMm(paperHmm)} mm`;
  ctx.fillText(sizeLabel, W / 2, Math.min(paperY + paperH + 14 * dpr, H - 4 * dpr));

  if (!image) {
    ctx.fillStyle = "#6b7c8c";
    ctx.font = `${Math.max(13, 13 * dpr)}px 'IBM Plex Sans', sans-serif`;
    ctx.fillText("Escolha uma imagem para pré-visualizar", W / 2, paperY + paperH / 2);
    return { paperW, paperH, area, paperWmm, paperHmm };
  }

  const dest = computeDestination(area.x, area.y, area.w, area.h, image.width, image.height, fit, scalePercent);
  ctx.save();
  ctx.beginPath();
  ctx.rect(area.x, area.y, area.w, area.h);
  ctx.clip();
  ctx.drawImage(image, dest.x, dest.y, dest.w, dest.h);
  ctx.restore();

  const cropped = dest.x < area.x || dest.y < area.y ||
    dest.x + dest.w > area.x + area.w || dest.y + dest.h > area.y + area.h;
  if (cropped) {
    ctx.fillStyle = "rgba(220, 38, 38, 0.12)";
    ctx.fillRect(area.x, area.y, area.w, area.h);
    ctx.strokeStyle = "#dc2626";
    ctx.setLineDash([6 * dpr, 4 * dpr]);
    ctx.strokeRect(area.x, area.y, area.w, area.h);
    ctx.setLineDash([]);
    ctx.fillStyle = "#b91c1c";
    ctx.textAlign = "center";
    ctx.font = `bold ${Math.max(11, 11 * dpr)}px 'IBM Plex Sans', sans-serif`;
    ctx.fillText("partes fora da linha serão cortadas", area.x + area.w / 2, area.y + area.h - 8 * dpr);
  }

  ctx.strokeStyle = "#0d9488";
  ctx.lineWidth = Math.max(1.5, 1.5 * dpr);
  ctx.strokeRect(
    Math.max(dest.x, area.x),
    Math.max(dest.y, area.y),
    Math.min(dest.w, area.w),
    Math.min(dest.h, area.h)
  );

  return { paperW, paperH, area, dest, paperWmm, paperHmm };
}

function fmtMm(n) {
  const v = Number(n);
  return Number.isInteger(v) ? String(v) : v.toFixed(1);
}

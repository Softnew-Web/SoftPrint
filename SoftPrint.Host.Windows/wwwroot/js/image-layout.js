/** Espelha ImageLayoutCalculator / PaperSizeCatalog do backend. */

export const PAPER_PRESETS = {
  a4: { w: 210, h: 297, label: "A4" },
  a5: { w: 148, h: 210, label: "A5" },
  letter: { w: 215.9, h: 279.4, label: "Letter" },
  legal: { w: 215.9, h: 355.6, label: "Legal" },
  photo4x6: { w: 101.6, h: 152.4, label: "Foto 10×15" },
  receipt58: { w: 58, h: 200, label: "Cupom 58 mm" },
  receipt80: { w: 80, h: 297, label: "Cupom 80 mm" },
  padrao: { w: 200, h: 70, label: "Padrão (200×70)" },
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

export function computeDestination(opts) {
  const {
    pageX,
    pageY,
    pageW,
    pageH,
    imageW,
    imageH,
    fit,
    scalePercent,
  } = opts;
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

  const layout = layoutPaperOnCanvas(canvas, paper);
  const { dpr, W, H, paperX, paperY, paperW, paperH, paperWmm, paperHmm, isNarrow, isWide } = layout;

  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, W, H);
  ctx.fillStyle = "#121a22";
  ctx.fillRect(0, 0, W, H);

  const area = resolvePrintableArea(layout, paper?.margins);
  drawPaperSheet(ctx, dpr, paperX, paperY, paperW, paperH);
  drawPrintableOutline(ctx, dpr, area, paper?.margins, isNarrow, isWide);
  drawSizeCaption(ctx, {
    dpr,
    W,
    H,
    paperX,
    paperY,
    paperW,
    paperH,
    paperWmm,
    paperHmm,
  });

  if (!image) {
    drawEmptyTip(ctx, dpr, paperX, paperY, paperW, paperH, isNarrow);
    return { paperW, paperH, area, paperWmm, paperHmm };
  }

  const dest = computeDestination({
    pageX: area.x,
    pageY: area.y,
    pageW: area.w,
    pageH: area.h,
    imageW: image.width,
    imageH: image.height,
    fit,
    scalePercent,
  });
  drawPreviewImage(ctx, dpr, image, dest, area, isNarrow || isWide);
  return { paperW, paperH, area, dest, paperWmm, paperHmm };
}

function layoutPaperOnCanvas(canvas, paper) {
  const paperWmm = paper?.w || 210;
  const paperHmm = paper?.h || 297;
  const paperRatio = paperWmm / Math.max(1e-6, paperHmm);
  const isNarrow = paperRatio < 0.45;
  const isWide = paperRatio > 1.6;

  const parent = canvas.parentElement;
  const cssW = Math.max(200, Math.floor(parent?.clientWidth || canvas.clientWidth || 520));
  const cssH = Math.max(200, Math.floor(parent?.clientHeight || canvas.clientHeight || 360));
  const dpr = Math.min(typeof window !== "undefined" ? window.devicePixelRatio || 1 : 1, 2);
  canvas.style.width = `${cssW}px`;
  canvas.style.height = `${cssH}px`;
  const W = Math.round(cssW * dpr);
  const H = Math.round(cssH * dpr);
  if (canvas.width !== W) canvas.width = W;
  if (canvas.height !== H) canvas.height = H;

  const padX = Math.max(18 * dpr, W * 0.06);
  const padTop = Math.max(18 * dpr, H * 0.06);
  const labelRoom = 22 * dpr;
  const padBottom = Math.max(14 * dpr, H * 0.04) + labelRoom;
  const availW = Math.max(40 * dpr, W - padX * 2);
  const availH = Math.max(40 * dpr, H - padTop - padBottom);

  let paperW = availW;
  let paperH = paperW / paperRatio;
  if (paperH > availH) {
    paperH = availH;
    paperW = paperH * paperRatio;
  }
  if (isWide) {
    paperW = Math.min(paperW, availW * 0.92);
    paperH = paperW / paperRatio;
  }
  if (isNarrow) {
    paperH = Math.min(paperH, availH * 0.92);
    paperW = paperH * paperRatio;
  }

  return {
    dpr,
    W,
    H,
    paperWmm,
    paperHmm,
    isNarrow,
    isWide,
    paperW,
    paperH,
    paperX: (W - paperW) / 2,
    paperY: padTop + (availH - paperH) / 2,
  };
}

function resolvePrintableArea(layout, margins = {}) {
  const { paperX, paperY, paperW, paperH, paperWmm, paperHmm } = layout;
  const fallbackMargin = Math.min(paperW, paperH) * 0.045;
  const mmToX = paperW / paperWmm;
  const mmToY = paperH / paperHmm;
  let marginLeft = Number.isFinite(margins.leftMm) ? margins.leftMm * mmToX : fallbackMargin;
  let marginRight = Number.isFinite(margins.rightMm) ? margins.rightMm * mmToX : fallbackMargin;
  let marginTop = Number.isFinite(margins.topMm) ? margins.topMm * mmToY : fallbackMargin;
  let marginBottom = Number.isFinite(margins.bottomMm) ? margins.bottomMm * mmToY : fallbackMargin;

  const balanced = balanceAsymmetricMargins(
    { marginLeft, marginRight, marginTop, marginBottom },
    paperW,
    paperH
  );

  return {
    x: paperX + balanced.marginLeft,
    y: paperY + balanced.marginTop,
    w: Math.max(1, paperW - balanced.marginLeft - balanced.marginRight),
    h: Math.max(1, paperH - balanced.marginTop - balanced.marginBottom),
  };
}

function balanceAsymmetricMargins(m, paperW, paperH) {
  const minVis = Math.min(paperW, paperH) * 0.012;
  let { marginLeft, marginRight, marginTop, marginBottom } = m;
  if (marginRight < minVis && marginLeft > minVis * 2) marginRight = Math.min(marginLeft, paperW * 0.08);
  if (marginBottom < minVis && marginTop > minVis * 2) marginBottom = Math.min(marginTop, paperH * 0.08);
  if (marginLeft < minVis && marginRight > minVis * 2) marginLeft = Math.min(marginRight, paperW * 0.08);
  if (marginTop < minVis && marginBottom > minVis * 2) marginTop = Math.min(marginBottom, paperH * 0.08);
  return { marginLeft, marginRight, marginTop, marginBottom };
}

function drawPaperSheet(ctx, dpr, paperX, paperY, paperW, paperH) {
  ctx.fillStyle = "rgba(0,0,0,0.35)";
  ctx.fillRect(paperX + 3 * dpr, paperY + 4 * dpr, paperW, paperH);
  ctx.fillStyle = "#f4f7fa";
  ctx.strokeStyle = "#3a4a5a";
  ctx.lineWidth = Math.max(1, dpr);
  ctx.fillRect(paperX, paperY, paperW, paperH);
  ctx.strokeRect(paperX, paperY, paperW, paperH);
}

function printableAreaLabel(margins, compact) {
  const fromDriver = margins?.source === "driver" || margins?.source === "cups-configured";
  if (!fromDriver) return "área imprimível aproximada";
  return compact ? "área imprimível" : "área imprimível (mesmo recorte do envio)";
}

function drawPrintableOutline(ctx, dpr, area, margins, isNarrow, isWide) {
  ctx.strokeStyle = "#0f766e";
  ctx.lineWidth = Math.max(1.75, 2 * dpr);
  ctx.setLineDash([7 * dpr, 5 * dpr]);
  ctx.strokeRect(area.x, area.y, area.w, area.h);
  ctx.setLineDash([]);

  const areaFont = Math.max(9, Math.min(12, Math.min(area.w / 22, area.h / 4)) * dpr);
  ctx.fillStyle = "#0f766e";
  ctx.font = `600 ${areaFont}px 'IBM Plex Sans', sans-serif`;
  ctx.textAlign = "left";
  if (area.w <= 36 * dpr || area.h <= 16 * dpr) return;

  ctx.save();
  ctx.beginPath();
  ctx.rect(area.x + 1, area.y + 1, area.w - 2, Math.min(area.h - 2, areaFont + 8 * dpr));
  ctx.clip();
  ctx.fillText(printableAreaLabel(margins, isNarrow || isWide), area.x + 5 * dpr, area.y + areaFont + 3 * dpr);
  ctx.restore();
}

function drawSizeCaption(ctx, box) {
  const { dpr, W, H, paperY, paperH, paperWmm, paperHmm } = box;
  ctx.fillStyle = "#8a9aab";
  ctx.font = `${Math.max(10, 11 * dpr)}px 'IBM Plex Sans', sans-serif`;
  ctx.textAlign = "center";
  ctx.fillText(
    `${fmtMm(paperWmm)}×${fmtMm(paperHmm)} mm`,
    W / 2,
    Math.min(paperY + paperH + 16 * dpr, H - 6 * dpr)
  );
}

function drawEmptyTip(ctx, dpr, paperX, paperY, paperW, paperH, isNarrow) {
  const tipFont = Math.max(11, Math.min(14, Math.min(paperW / 18, paperH / 5)) * dpr);
  ctx.fillStyle = "#6b7c8c";
  ctx.font = `${tipFont}px 'IBM Plex Sans', sans-serif`;
  ctx.textAlign = "center";
  const tip = isNarrow ? "Escolha um\narquivo" : "Escolha uma imagem para pré-visualizar";
  const tipLines = tip.split("\n");
  const lineH = tipFont * 1.35;
  const startY = paperY + paperH / 2 - ((tipLines.length - 1) * lineH) / 2;
  tipLines.forEach((line, i) => {
    ctx.fillText(line, paperX + paperW / 2, startY + i * lineH);
  });
}

function drawPreviewImage(ctx, dpr, image, dest, area, compact) {
  ctx.save();
  ctx.beginPath();
  ctx.rect(area.x, area.y, area.w, area.h);
  ctx.clip();
  ctx.drawImage(image, dest.x, dest.y, dest.w, dest.h);
  ctx.restore();

  const cropped =
    dest.x < area.x ||
    dest.y < area.y ||
    dest.x + dest.w > area.x + area.w ||
    dest.y + dest.h > area.y + area.h;
  if (cropped) {
    ctx.fillStyle = "rgba(220, 38, 38, 0.12)";
    ctx.fillRect(area.x, area.y, area.w, area.h);
    ctx.strokeStyle = "#dc2626";
    ctx.setLineDash([6 * dpr, 4 * dpr]);
    ctx.strokeRect(area.x, area.y, area.w, area.h);
    ctx.setLineDash([]);
    ctx.fillStyle = "#b91c1c";
    ctx.textAlign = "center";
    ctx.font = `bold ${Math.max(10, Math.min(11, area.w / 12) * dpr)}px 'IBM Plex Sans', sans-serif`;
    ctx.fillText(
      compact ? "será cortado" : "partes fora da linha serão cortadas",
      area.x + area.w / 2,
      area.y + area.h - 8 * dpr
    );
  }

  ctx.strokeStyle = "#0d9488";
  ctx.lineWidth = Math.max(1.5, 1.5 * dpr);
  ctx.strokeRect(
    Math.max(dest.x, area.x),
    Math.max(dest.y, area.y),
    Math.min(dest.w, area.w),
    Math.min(dest.h, area.h)
  );
}

function fmtMm(n) {
  const v = Number(n);
  return Number.isInteger(v) ? String(v) : v.toFixed(1);
}

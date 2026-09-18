import { state } from "./state.js";

/** Monta o payload completo de settings para não apagar campos de outra aba. */
export function buildSettingsPayload(overrides = {}) {
  const s = state.applied || {};
  return {
    printerName: s.printerName || "",
    simulation: !!s.simulation,
    paused: !!s.paused,
    imageFit: s.imageFit || "contain",
    imageScalePercent: s.imageScalePercent ?? 100,
    paperSize: s.paperSize || "a4",
    paperWidthMm: s.paperWidthMm ?? 210,
    paperHeightMm: s.paperHeightMm ?? 297,
    paperLandscape: !!s.paperLandscape,
    inboxFolder: s.inboxFolder || "",
    inboxEnabled: !!s.inboxEnabled,
    deleteInboxAfterPrint: !!s.deleteInboxAfterPrint,
    expectedRevision: s.revision,
    ...overrides,
  };
}

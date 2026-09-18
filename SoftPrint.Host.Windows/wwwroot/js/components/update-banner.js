const DISMISS_KEY = "softprint-update-dismissed";

export function applyUpdateInfo(update) {
  const versionLabel = document.getElementById("appVersionLabel");
  const banner = document.getElementById("updateBanner");
  const text = document.getElementById("updateBannerText");
  const link = document.getElementById("updateDownloadLink");
  const dismiss = document.getElementById("updateDismissBtn");
  if (!banner || !text || !link) return;

  const current = update?.currentVersion || "—";
  if (versionLabel) versionLabel.textContent = `v${current}`;

  if (!update?.updateAvailable) {
    banner.classList.add("hidden");
    return;
  }

  const latest = update.latestVersion || "?";
  const dismissed = sessionStorage.getItem(DISMISS_KEY) === latest;
  if (dismissed && !update.mandatory) {
    banner.classList.add("hidden");
    return;
  }

  banner.classList.remove("hidden");
  banner.classList.toggle("bg-bad/20", !!update.mandatory);
  banner.classList.toggle("border-bad/40", !!update.mandatory);
  banner.classList.toggle("bg-warn/15", !update.mandatory);
  banner.classList.toggle("border-warn/40", !update.mandatory);

  text.textContent = update.mandatory
    ? `Atualização obrigatória: ${current} → ${latest}. Instale a nova versão para continuar com segurança.`
    : `Nova versão disponível: ${current} → ${latest}.`;

  const href = update.downloadUrl || update.releaseUrl || "#";
  link.href = href;
  link.classList.toggle("pointer-events-none", href === "#");

  if (dismiss) {
    dismiss.classList.toggle("hidden", !!update.mandatory);
    dismiss.onclick = () => {
      sessionStorage.setItem(DISMISS_KEY, latest);
      banner.classList.add("hidden");
    };
  }
}

import { GlobalWorkerOptions, getDocument } from "pdfjs-dist/legacy/build/pdf.mjs";

GlobalWorkerOptions.workerSrc = "/js/pdf.worker.min.mjs";

export async function loadPdfPreview(file) {
  const bytes = new Uint8Array(await file.arrayBuffer());
  const task = getDocument({ data: bytes });
  const pdf = await task.promise;
  let renderTask = null;

  return {
    pageCount: pdf.numPages,
    async render(pageNumber, zoom = 1) {
      renderTask?.cancel();
      const page = await pdf.getPage(Math.min(pdf.numPages, Math.max(1, pageNumber)));
      const viewport = page.getViewport({ scale: Math.min(3, Math.max(0.5, zoom)) });
      const canvas = document.createElement("canvas");
      canvas.width = Math.ceil(viewport.width);
      canvas.height = Math.ceil(viewport.height);
      renderTask = page.render({ canvasContext: canvas.getContext("2d"), viewport });
      await renderTask.promise;
      renderTask = null;
      return canvas;
    },
    async destroy() {
      renderTask?.cancel();
      await task.destroy();
    },
  };
}

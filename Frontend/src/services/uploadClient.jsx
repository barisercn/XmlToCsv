const BASE_URL = import.meta.env.VITE_API_BASE_URL || "";
export async function uploadFileFetch({ file, endpoint = "/api/upload", fields = {}, signal }) {
    const url = BASE_URL + endpoint;
    const form = new FormData();
    form.append("file", file);
    Object.entries(fields).forEach(([k, v]) => form.append(k, v));

    const res = await fetch(url, { method: "POST", body: form, signal });
    if (!res.ok) {
        const text = await res.text().catch(() => "");
        throw new Error(`Upload failed (${res.status}) ${text}`);
    }

    const contentType = res.headers.get("Content-Type");
    if (contentType && contentType.includes("application/zip")) {
        const blob = await res.blob();
        const contentDisposition = res.headers.get("Content-Disposition");
        let fileName = "download.zip";
        if (contentDisposition) {
            const match = contentDisposition.match(/filename="([^"]+)"/);
            if (match && match[1]) {
                fileName = match[1];
            }
        }
        return { ok: true, isFile: true, file: blob, fileName: fileName };
    } else {
        try {
            const jsonResponse = await res.json();
            return { ok: true, isFile: false, data: jsonResponse };
        } catch {
            return { ok: true, isFile: false, data: null }; // Yedek
        }
    }
} export function humanSize(bytes) {
    if (!Number.isFinite(bytes)) return "-";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let i = 0, v = bytes;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
}

export function validateFile(file, { maxSizeMB, accept = [".xml", ".csv"] } = {}) {
    const maxBytes = maxSizeMB * 1024 * 1024;
    if (file.size > maxBytes) return { ok: false, reason: `Dosya çok büyük (${humanSize(file.size)}).Limit: ${maxSizeMB} MB` };
    if (accept && accept.length > 0) {
        const lower = file.name.toLowerCase();
        const hit = accept.some(ext => lower.endsWith(ext.trim().toLowerCase()));
        if (!hit) return { ok: false, reason: `İzin verilen uzantılar: ${accept.join(", ")}` };
    }
    return { ok: true };
}

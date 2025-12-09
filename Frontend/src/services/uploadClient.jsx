// src/services/uploadClient.jsx

const BASE_URL = import.meta.env.VITE_API_BASE_URL || "";

/**
 * XML dosyasını /api/upload endpoint'ine gönderir.
 * Backend şu formatta cevap veriyor:
 *   { jobId: "..." }
 */
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

    const contentType = res.headers.get("Content-Type") || "";

    // Beklenen durum: JSON dönmesi (jobId vb.)
    if (contentType.includes("application/json")) {
        return await res.json();
    }

    // İhtimal dahilinde: bazı endpoint'ler zip dönebilir
    if (contentType.includes("application/zip")) {
        const blob = await res.blob();
        // Content-Disposition'dan dosya adı yakalamaya çalış
        const disposition = res.headers.get("Content-Disposition") || "";
        let filename = "download.zip";

        const match = /filename="?([^"]+)"?/i.exec(disposition);
        if (match && match[1]) {
            filename = match[1];
        }

        return { blob, filename };
    }

    // Fallback: text dönmüşse
    const text = await res.text().catch(() => "");
    try {
        return JSON.parse(text);
    } catch {
        return { raw: text };
    }
}

/**
 * Belirli bir jobId için API'den durum çeker.
 * GET /api/jobs/{id}
 */
export async function fetchJobStatus(jobId, signal) {
    const url = `${BASE_URL}/api/jobs/${encodeURIComponent(jobId)}`;
    const res = await fetch(url, { method: "GET", signal });

    if (!res.ok) {
        const text = await res.text().catch(() => "");
        throw new Error(`Job status failed (${res.status}) ${text}`);
    }

    const contentType = res.headers.get("Content-Type") || "";
    if (!contentType.includes("application/json")) {
        const text = await res.text().catch(() => "");
        throw new Error(`Beklenmeyen yanıt türü: ${contentType} - ${text}`);
    }

    return await res.json();
}

// Yardımcılar: validateFile + humanSize (senin eski fonksiyonlarından uyarladık)

export function humanSize(bytes) {
    if (bytes === 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let i = 0;
    let v = bytes;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 10 || i === 0 ? 0 : 1)} ${units[i]}`;
}

export function validateFile(file, { maxSizeMB, accept = [".xml", ".csv"] } = {}) {
    const maxBytes = maxSizeMB * 1024 * 1024;
    if (file.size > maxBytes) {
        return {
            ok: false,
            reason: `Dosya çok büyük (${humanSize(file.size)}). Limit: ${maxSizeMB} MB`
        };
    }

    if (accept && accept.length > 0) {
        const lower = file.name.toLowerCase();
        const hit = accept.some(ext => lower.endsWith(ext.trim().toLowerCase()));
        if (!hit) {
            return { ok: false, reason: `İzin verilen uzantılar: ${accept.join(", ")}` };
        }
    }

    return { ok: true };
}

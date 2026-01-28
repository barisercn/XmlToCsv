// src/components/FileUpload.jsx

import React, { useRef, useState, useCallback } from "react";
import {
    uploadFileFetch,
    validateFile,
    humanSize,
    fetchJobStatus,
} from "../services/uploadClient";
import "../styles/modern-upload.css";

export default function FileUpload({
    endpoint = "/api/upload",
    multiple = false,
    accept = [".xml"],
    maxSizeMB = 512,
}) {
    const [files, setFiles] = useState([]);
    const [messages, setMessages] = useState([]);
    const [status, setStatus] = useState("idle"); // idle | uploading | done | error | saving_db
    const [busyIndex, setBusyIndex] = useState(-1);
    const [loadType, setLoadType] = useState("Full");   // Full / Daily
    const [dataDate, setDataDate] = useState("");

    const [lastJobId, setLastJobId] = useState("");
    const [jobIdInput, setJobIdInput] = useState("");
    const [jobStatusResult, setJobStatusResult] = useState(null);

    const inputRef = useRef(null);
    const abortRef = useRef(null);

    const addMessage = useCallback((msg) => {
        setMessages((prev) => [...prev, msg]);
    }, []);

    // Dosya seçimi
    const onFileChange = (e) => {
        const selected = Array.from(e.target.files || []);
        if (!selected.length) return;

        const valid = [];
        const newMessages = [];

        selected.forEach((file) => {
            const v = validateFile(file, { maxSizeMB, accept });
            if (!v.ok) {
                newMessages.push(`❌ ${file.name}: ${v.reason}`);
            } else {
                valid.push(file);
                newMessages.push(`✅ ${file.name} eklendi (${humanSize(file.size)})`);
            }
        });

        setFiles(valid);
        setMessages((prev) => [...prev, ...newMessages]);
    };

    // Her şeyi sıfırla
    const clearAll = () => {
        setFiles([]);
        setMessages([]);
        setStatus("idle");
        setBusyIndex(-1);
        setLastJobId("");
        setJobIdInput("");
        setJobStatusResult(null);
        if (inputRef.current) {
            inputRef.current.value = "";
        }
    };

    // Yüklemeyi iptal et
    const cancelUpload = () => {
        if (abortRef.current) {
            abortRef.current.abort();
            abortRef.current = null;
            setStatus("idle");
            setBusyIndex(-1);
            addMessage("⏹ Yükleme iptal edildi.");
        }
    };

    // Yüklemeyi başlat (jobId alma)
    const startUpload = useCallback(
        async () => {
            if (!files.length) {
                addMessage("Lütfen önce bir dosya seçin.");
                return;
            }

            const file = files[0]; // Şimdilik tek dosya ile çalışıyoruz
            setStatus("uploading");
            setMessages([]);
            setBusyIndex(0);
            setJobStatusResult(null);

            const controller = new AbortController();
            abortRef.current = controller;

            try {
                addMessage(
                    `⬆️ Yükleniyor: ${file.name} (${humanSize(file.size)})...`
                );

                const result = await uploadFileFetch({
                    file,
                    endpoint,
                    signal: controller.signal,
                });

                // Beklenen format: { jobId: "..." }
                if (result && result.jobId) {
                    setLastJobId(result.jobId);
                    setJobIdInput(result.jobId);

                    setStatus("done");
                    setBusyIndex(-1);

                    addMessage(
                        `✅ İş başarıyla oluşturuldu. JobId: ${result.jobId}`
                    );
                    addMessage(
                        "Bu JobId'yi kaydedebilir veya aşağıdaki alandan durumunu sorgulayabilirsiniz."
                    );
                } else {
                    setStatus("error");
                    setBusyIndex(-1);
                    addMessage("❌ Sunucudan beklenen jobId bilgisi alınamadı.");
                }
            } catch (err) {
                if (controller.signal.aborted) {
                    addMessage("⚠️ Yükleme client tarafından iptal edildi.");
                    setStatus("idle");
                } else {
                    console.error(err);
                    setStatus("error");
                    setBusyIndex(-1);
                    addMessage(`❌ Hata: ${err.message}`);
                }
            } finally {
                abortRef.current = null;
            }
        },
        [files, endpoint, addMessage]
    );

    // JobId'yi panoya kopyala
    const handleCopyJobId = async () => {
        if (!lastJobId) return;
        try {
            await navigator.clipboard.writeText(lastJobId);
            addMessage("📋 JobId panoya kopyalandı.");
        } catch {
            addMessage(
                "⚠️ JobId'yi kopyalarken bir sorun oluştu, elle seçip kopyalayabilirsiniz."
            );
        }
    };

    // Job durumunu sorgula
    const handleCheckJobStatus = async () => {
        if (!jobIdInput) {
            addMessage(
                "Lütfen durumunu sorgulamak için bir JobId girin."
            );
            return;
        }

        try {
            addMessage(`🔎 Durum sorgulanıyor: ${jobIdInput} ...`);
            const result = await fetchJobStatus(jobIdInput);
            setJobStatusResult(result);

            addMessage(
                `📊 Durum: ${result.status} | Mesaj: ${result.message ?? "-"
                }`
            );

            if (result.downloadFileName && result.status === "Completed") {
                addMessage(
                    `✅ ZIP hazır: ${result.downloadFileName} - İndirmek için aşağıdaki "ZIP'i İndir" butonunu kullanabilirsiniz.`
                );
            }
        } catch (err) {
            console.error(err);
            addMessage(`❌ Durum sorgulama hatası: ${err.message}`);
        }
    };

    // ZIP dosyasını indir
    const handleDownloadZip = () => {
        if (
            !jobStatusResult ||
            jobStatusResult.status !== "Completed" ||
            !jobStatusResult.downloadFileName
        ) {
            addMessage(
                "⚠️ İndirilebilir bir dosya bulunamadı. Job durumunu 'Completed' olacak şekilde yeniden kontrol edin."
            );
            return;
        }

        const fileName = jobStatusResult.downloadFileName;
        const BASE_URL = import.meta.env.VITE_API_BASE_URL || "";
        const url =
            BASE_URL +
            "/api/download/" +
            encodeURIComponent(fileName);

        // Doğrudan indir
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);

        addMessage(`⬇️ ${fileName} indiriliyor...`);
    };

    // ZIP'i PostgreSQL'e kaydet (DB import)
    const handleSaveToDb = async () => {
        if (
            !jobStatusResult ||
            jobStatusResult.status !== "Completed" ||
            !jobStatusResult.downloadFileName
        ) {
            addMessage(
                "⚠️ Veritabanına kaydetmek için hazır bir ZIP dosyası yok. Önce job durumunun 'Completed' olduğundan emin olun."
            );
            return;
        }

        const fileName = jobStatusResult.downloadFileName;
        const BASE_URL = import.meta.env.VITE_API_BASE_URL || "";

        // ✅ TARİH ZORUNLU KONTROLÜ (burada hata fırlatıyoruz)
        if (!dataDate || dataDate.trim() === "") {
            addMessage("❌ Tarih seçilmedi. Lütfen verinin hangi tarihe ait olduğunu girin (YYYY-AA-GG).");
            setStatus("error"); // veya "idle"
            return; // fetch'e gitmesin
        }

        setStatus("saving_db");
        addMessage(`💾 PostgreSQL'e kaydediliyor: ${fileName} ...`);

        const payload = { fileName, loadType, dataDate: dataDate.trim() };

        try {
            const res = await fetch(`${BASE_URL}/api/dbyekaydet`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(payload),
            });

            if (!res.ok) {
                const errorText = await res.text();
                throw new Error(errorText || `Sunucu hatası: ${res.status}`);
            }

            const result = await res.json();
            addMessage(`✅ Veritabanı kaydı başarılı: ${result.message}`);
            setStatus("done");
        } catch (err) {
            console.error(err);
            addMessage(`❌ Veritabanı Kayıt Hatası: ${err.message}`);
            setStatus("error");
        }
    };



    return (
        <div className="upload-wrap">
            <h2>XML Yükleme ve İş Durumu Takip</h2>

            {/* Dosya Seçimi */}
            <div className="file-select-box">
                <input
                    ref={inputRef}
                    type="file"
                    accept={
                        Array.isArray(accept) ? accept.join(",") : accept
                    }
                    multiple={multiple}
                    onChange={onFileChange}
                />
                <p className="helper-text">
                    İzin verilen uzantılar:{" "}
                    {Array.isArray(accept)
                        ? accept.join(", ")
                        : accept}{" "}
                    – Maksimum {maxSizeMB} MB
                </p>
            </div>

            {/* Seçilen Dosyalar */}
            {files.length > 0 && (
                <div className="file-list">
                    <h4>Seçilen dosyalar</h4>
                    <ul>
                        {files.map((f, idx) => (
                            <li
                                key={idx}
                                className={idx === busyIndex ? "busy" : ""}
                            >
                                {f.name} ({humanSize(f.size)})
                            </li>
                        ))}
                    </ul>
                </div>
            )}

            {/* Upload Butonları */}
            <div className="button-row">
                <button
                    type="button"
                    className="btn btn-primary"
                    onClick={startUpload}
                    disabled={status === "uploading" || files.length === 0}
                >
                    {status === "uploading"
                        ? "Yükleniyor..."
                        : "Yüklemeyi Başlat"}
                </button>

                <button
                    type="button"
                    className="btn btn-danger"
                    onClick={cancelUpload}
                    disabled={status !== "uploading"}
                >
                    İptal
                </button>

                <button
                    type="button"
                    className="btn btn-ghost"
                    onClick={clearAll}
                    disabled={status === "uploading"}
                >
                    Temizle
                </button>
            </div>

            {/* JobId Bilgisi */}
            {lastJobId && (
                <div className="job-info-box">
                    <h4>Oluşturulan İş (Job)</h4>
                    <p>
                        JobId: <code>{lastJobId}</code>
                    </p>
                    <button
                        type="button"
                        className="btn btn-secondary"
                        onClick={handleCopyJobId}
                    >
                        JobId'yi Kopyala
                    </button>
                </div>
            )}

            {/* Job Durumu Sorgulama */}
            <div className="job-status-box">
                <h3>İş Durumu Sorgula</h3>
                <p>
                    Herhangi bir zamanda job durumunu görmek için, JobId'yi
                    girip sorgulayabilirsiniz.
                </p>
                <div className="job-status-form">
                    <input
                        type="text"
                        value={jobIdInput}
                        onChange={(e) =>
                            setJobIdInput(e.target.value)
                        }
                        placeholder="JobId girin..."
                        className="jobid-input"
                    />
                    <button
                        type="button"
                        className="btn btn-info"
                        onClick={handleCheckJobStatus}
                    >
                        Durumu Getir
                    </button>
                </div>

                {jobStatusResult && (
                    <div className="job-status-result">
                        <h4>Son Durum</h4>
                        <pre>
                            {JSON.stringify(
                                jobStatusResult,
                                null,
                                2
                            )}
                        </pre>
                    </div>
                )}
                {/* DB import ayarları */}
                <div className="db-import-options" style={{ marginTop: "16px" }}>
                    <h4>Veritabanı Yükleme Ayarları</h4>
                    <div style={{ display: "flex", gap: "12px", flexWrap: "wrap", alignItems: "center" }}>
                        <label>
                            Yükleme Türü:&nbsp;
                            <select
                                value={loadType}
                                onChange={(e) => setLoadType(e.target.value)}
                            >
                                <option value="Full">İlk Yük (Full)</option>
                                <option value="Daily">Günlük / Artış (Daily)</option>
                            </select>
                        </label>

                        <label>
                            Veri Tarihi: (opsiyonel)
                            <input
                                type="date"
                                value={dataDate}
                                onChange={(e) => setDataDate(e.target.value)}
                            />
                            <small>Boş bırakırsan backend bugünün tarihini kullanır.</small>
                        </label>
                    </div>
                </div>


                {/* ZIP hazırsa İNDİR ve DB'ye KAYDET butonları */}
                {jobStatusResult &&
                    jobStatusResult.status === "Completed" &&
                    jobStatusResult.downloadFileName && (
                        <div
                            className="download-actions"
                            style={{
                                marginTop: "16px",
                                display: "flex",
                                gap: "8px",
                                flexWrap: "wrap",
                            }}
                        >
                            <button
                                type="button"
                                className="btn btn-success"
                                onClick={handleDownloadZip}
                            >
                                ZIP&apos;i İndir (
                                {jobStatusResult.downloadFileName})
                            </button>
                            <button
                                type="button"
                                className="btn btn-warning"
                                onClick={handleSaveToDb}
                                disabled={
                                    status === "saving_db" ||
                                    status === "uploading"
                                }
                            >
                                {status === "saving_db"
                                    ? "PostgreSQL'e Kaydediliyor..."
                                    : "PostgreSQL'e Kaydet"}
                            </button>
                        </div>
                    )}
            </div>

            {/* Mesajlar / Log */}
            {messages.length > 0 && (
                <div className="log-box">
                    <h4>İşlem Günlüğü</h4>
                    <ul>
                        {messages.map((m, i) => (
                            <li key={i}>{m}</li>
                        ))}
                    </ul>
                </div>
            )}
        </div>
    );
}

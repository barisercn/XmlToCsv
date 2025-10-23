import { uploadFileFetch, validateFile, humanSize } from "../services/uploadClient";
import React, { useRef, useState, useCallback } from "react";
import { useNavigate } from "react-router-dom";
import '../styles/buttons.css';
import '../styles/file-upload-modern.css';
export default function FileUpload({
    endpoint = "/api/upload",
    multiple = true,
    accept = [".xml"],
    maxSizeMB = 512,
}) {
    const [files, setFiles] = useState([]);
    const [messages, setMessages] = useState([]);
    const [status, setStatus] = useState("idle"); // idle | uploading | done | error | cancelled
    const [busyIndex, setBusyIndex] = useState(-1);
    const inputRef = useRef(null);
    const abortRef = useRef(null);
    const [downloadableFile, setDownloadableFile] = useState(null);
    const handleDownloadClick = () => {
        if (!downloadableFile) return;
        // API adresini VITE_API_BASE_URL'den alıyoruz (uploadClient.jsx'teki gibi)
        const BASE_URL = import.meta.env.VITE_API_BASE_URL || "";
        const downloadUrl = `${BASE_URL}/api/download/${downloadableFile}`;

        // Dosyayı indirmek için görünmez bir link oluşturup tıklıyoruz
        const a = document.createElement('a');
        a.href = downloadUrl;
        a.download = downloadableFile; // Tarayıcıya indirme adını belirtir
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);

        setMessages((m) => [...m, `⬇️${downloadableFile} indiriliyor...`]);
    };

    const onPick = (picked) => {
        const list = Array.from(picked || []);
        const valids = [];
        // Değişiklik burada: 'accept' prop'u dizi değilse diziye çevir
        const acceptArray = Array.isArray(accept) ? accept : accept.split(',');
        list.forEach((f) => {
            const ok = validateFile(f, { maxSizeMB, accept: acceptArray });
            if (!ok.ok) {
                setMessages((m) => [...m, `⚠️${f.name}: ${ok.reason}`]);
            } else {
                valids.push(f);
            }
        });
        setFiles(valids);
        setDownloadableFile(null);
    };
    const onInputChange = (e) => onPick(e.target.files);

    const onDrop = (e) => {
        e.preventDefault();
        onPick(e.dataTransfer?.files);
    };


    const onDragOver = (e) => e.preventDefault();

    const startUpload = useCallback(async () => {
        if (!files.length) {
            setMessages((m) => [...m, "Lütfen dosya seçin."]);
            return;
        }
        // Önceki mesajları ve indirme linkini temizle
        setStatus("uploading");
        setMessages([]);
        setDownloadableFile(null);
        abortRef.current = new AbortController();
        let lastResult = null;

        try {
            for (let i = 0; i < files.length; i++) {
                setBusyIndex(i);
                const f = files[i];
                // uploadClient'tan dönen sonucu yakala
                lastResult = await uploadFileFetch({
                    file: f,
                    endpoint,
                    signal: abortRef.current.signal,
                });
                setMessages((m) => [...m, `✅ ${f.name} işlenmek üzere sunucuya gönderildi.`]);
            }
            setStatus("done");
            setBusyIndex(-1);

            // Sonuç bir JSON ve içinde fileName varsa state'i güncelle
            if (lastResult && !lastResult.isFile && lastResult.data && lastResult.data.fileName) {
                setDownloadableFile(lastResult.data.fileName);
                setMessages((m) => [...m, `🎉 Dönüştürme tamamlandı. Dosyanız indirilmeye hazır!`]);
            } else {
                throw new Error("Sunucudan beklenen dosya adı alınamadı.");
            }

        } catch (err) {
            setStatus("error");
            setMessages((m) => [...m, `❌ Hata: ${err.message}`]);
            setBusyIndex(-1);
        } finally {
            abortRef.current = null;
        }
    }, [files, endpoint]);

    const cancelUpload = () => {
        abortRef.current?.abort();
        setStatus("cancelled");
        setBusyIndex(-1);
        setMessages((m) => [...m, "⏹️ Yükleme iptal edildi."]);
    };

    const clearAll = () => {
        setFiles([]);
        setMessages([]);
        setStatus("idle");
        setDownloadableFile(null);
        setBusyIndex(-1);
        if (inputRef.current) inputRef.current.value = null;
    };

    return (
        <div className="upload-wrap">
            <h2 className="upload-title">Dosya Yükleme</h2>

            <div
                className={`dropzone ${status === "uploading" ? "is-uploading" : ""}`}
                onDrop={onDrop}
                onDragOver={onDragOver}
            >
                <p>Dosyaları buraya sürükleyip bırak veya</p>
                <button
                    type="button"
                    className="btn"
                    onClick={() => inputRef.current?.click()}
                    disabled={status === "uploading"}
                >
                    Gözat
                </button>
                <input
                    ref={inputRef}
                    type="file"
                    multiple={multiple}
                    accept={accept}
                    onChange={onInputChange}
                    style={{ display: "none" }}
                />
                <small className="hint">
                    İzin verilen: <b>{accept}</b> • Boyut limiti: <b>{maxSizeMB} MB</b>
                </small>
            </div>

            {files.length > 0 && (
                <div className="file-list">
                    {files.map((f, i) => (
                        <div key={i} className="file-item">
                            <div className="file-meta">
                                <div className="file-name">{f.name}</div>
                                <div className="file-size">{humanSize(f.size)}</div>
                            </div>
                            {status === "uploading" && busyIndex === i ? (
                                <span className="badge">Yükleniyor…</span>
                            ) : null}
                        </div>
                    ))}
                </div>
            )}

            <div className="actions">
                <button
                    type="button"
                    className="btn btn-primary"
                    onClick={startUpload}
                    disabled={status === "uploading" || files.length === 0}
                >
                    Yüklemeyi Başlat
                </button>
                <button
                    type="button"
                    className="btn btn-danger"
                    onClick={cancelUpload}
                    disabled={status !== "uploading"}
                >
                    İptal
                </button>
                <button type="button" className="btn btn-ghost" onClick={clearAll}>
                    Temizle
                </button>

            </div>

            {messages.length > 0 && (
                <div className="messages">
                    {messages.map((m, idx) => (
                        <div key={idx} className="msg">{m}</div>
                    ))}
                </div>
            )}

            {/* --- YENİ İNDİRME BÖLÜMÜNÜ BURAYA EKLEYİN --- */}
            {downloadableFile && (
                <div className="download-section" style={{ marginTop: '20px', borderTop: '1px solid #eee', paddingTop: '20px', textAlign: 'center' }}>
                    <h4>İndirme</h4>
                    <p>İşlenmiş dosyanız hazır:</p>
                    <button
                        type="button"
                        className="btn btn-success"
                        onClick={handleDownloadClick}
                    >
                        {downloadableFile} İndir
                    </button>
                </div>
            )}

        </div> // Burası upload-wrap'in kapanışı
    );
}
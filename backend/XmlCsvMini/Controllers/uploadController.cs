using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Encodings.Web;
using XmlCsvMini.Models;
using XmlCsvMini.Services;
using System.Text;

namespace XmlCsvMini.Controllers
{
    [ApiController]
    public class UploadController : ControllerBase
    {
        private readonly IIslemeGoreviYonetimi _gorevYonetimi;
        private readonly ILogger<UploadController> _log;


        public UploadController(IIslemeGoreviYonetimi gorevYonetimi,
                                ILogger<UploadController> log)
        {
            _gorevYonetimi = gorevYonetimi;
            _log = log;

            // C# uygulamalarında Türkçe karakterlerle ilgili sorunları önlemek için
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// Uzun süren XML -> CSV -> ZIP işlemini artık request içinde çalıştırmıyoruz.
        /// Sadece dosyayı kaydedip, bir iş (job) oluşturuyoruz ve jobId dönüyoruz.
        /// Asıl ağır işlem arka planda Task.Run ile çalışıyor.
        /// </summary>
        [HttpPost("/api/upload")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(object))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(string))]
        [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(string))]
        public async Task<IActionResult> UploadFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("Lütfen bir dosya seçin.");

            // Geçici XML dosya yolu
            var tempFilePath = Path.Combine(Path.GetTempPath(),
                "XmlCsvMini_Input_" + Guid.NewGuid().ToString("N") + Path.GetExtension(file.FileName));

            try
            {
                // 1) Gelen dosyayı geçici bir yola kaydet
                await using (var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(stream);
                }

                // 2) Bir iş kaydı oluştur (status: Pending)
                var gorev = _gorevYonetimi.YeniGorevOlustur(tempFilePath, file.FileName);

                _log.LogInformation("Yeni XML işleme görevi oluşturuldu. JobId={JobId}, Dosya={File}",
                    gorev.Id, file.FileName);

                // 3) Uzun süren işlemi arka planda başlat
                _ = Task.Run(() => IslemeGoreviniCalistirAsync(gorev.Id));

                // 4) Kullanıcıyı bekletmeden sadece jobId döndür
                return Ok(new { jobId = gorev.Id });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Upload sırasında hata oluştu.");
                return StatusCode(500, $"Sunucu hatası: {ex.Message}");
            }
        }

        /// <summary>
        /// Eski download endpoint'i aynı kalabilir; ZIP adını artık job durumundan öğreneceğiz.
        /// (İleriki adımda JobStatus endpoint'i ekleyince birlikte kullanacağız.)
        /// </summary>
        [HttpGet("/api/download/{filename}")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileContentResult))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(string))]
        public IActionResult DownloadFile(string filename)
        {
            var zipPath = Path.Combine(Path.GetTempPath(), filename);

            if (!System.IO.File.Exists(zipPath))
                return NotFound("Dosya bulunamadı veya zaten indirilmiş.");

            var fileBytes = System.IO.File.ReadAllBytes(zipPath);

            return File(fileBytes, "application/zip", filename);
        }

        /// <summary>
        /// Job'ı arka planda çalıştıran metot.
        /// Burada durumları güncelliyoruz: Pending -> Running -> Completed / Failed
        /// ve ZIP dosyasının adını job kaydına yazıyoruz.
        /// </summary>
        private async Task IslemeGoreviniCalistirAsync(string gorevId)
        {
            var gorev = _gorevYonetimi.GorevGetir(gorevId);
            if (gorev == null)
            {
                _log.LogWarning("Arka plan işleminde job bulunamadı. JobId={JobId}", gorevId);
                return;
            }

            string girdiXml = gorev.DosyaYolu;
            string orijinalDosyaAdi = gorev.OrijinalDosyaAdi;

            // Çıktı klasörü ve zip yolu
            var outputDirectory = Path.Combine(Path.GetTempPath(), "XmlCsvMini_Output_" + gorev.Id);
            var zipPath = Path.Combine(
                Path.GetTempPath(),
                Path.GetFileNameWithoutExtension(orijinalDosyaAdi) + "_csv.zip"
            );

            try
            {
                _gorevYonetimi.GorevDurumGuncelle(gorevId, "Running", "XML işleniyor, CSV dosyaları üretiliyor.");

                Directory.CreateDirectory(outputDirectory);

                // --- Asıl XML -> CSV işleme ---
                RunProcessingLogic(girdiXml, outputDirectory, Path.GetFileNameWithoutExtension(orijinalDosyaAdi));

                // Çıktı klasöründe dosya yoksa hata kabul ediyoruz
                if (!Directory.EnumerateFiles(outputDirectory).Any())
                {
                    _gorevYonetimi.GorevDurumGuncelle(
                        gorevId,
                        "Failed",
                        "XML işlendi fakat dönüştürülecek uygun veri bulunamadı. Lütfen XML yapısını kontrol edin."
                    );
                    return;
                }

                // Eski zip varsa sil ve yeniden oluştur
                if (System.IO.File.Exists(zipPath))
                    System.IO.File.Delete(zipPath);

                ZipFile.CreateFromDirectory(outputDirectory, zipPath);

                _gorevYonetimi.GorevDurumGuncelle(
                    gorevId,
                    "Completed",
                    "İşleme tamamlandı, ZIP dosyası hazır.",
                    Path.GetFileName(zipPath)
                );

                _log.LogInformation("Job tamamlandı. JobId={JobId}, Zip={Zip}", gorevId, Path.GetFileName(zipPath));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Job çalışırken hata oluştu. JobId={JobId}", gorevId);
                _gorevYonetimi.GorevDurumGuncelle(
                    gorevId,
                    "Failed",
                    "İşleme sırasında hata oluştu: " + ex.Message
                );
            }
            finally
            {
                try
                {
                    // Girdi XML ve ara output klasörünü temizle
                    if (System.IO.File.Exists(girdiXml))
                        System.IO.File.Delete(girdiXml);

                    if (Directory.Exists(outputDirectory))
                        Directory.Delete(outputDirectory, true);

                    // ZIP dosyasını SİLMIYORUZ; download / dbyekaydet için lazım.
                }
                catch (Exception temizlikHatasi)
                {
                    _log.LogWarning(temizlikHatasi, "Job temizlik aşamasında hata oluştu. JobId={JobId}", gorevId);
                }
            }
        }

        /// <summary>
        /// Daha önce Program.cs'te kullandığımız, XML keşfi + hiyerarşi + CSV üretim mantığı.
        /// İçeriği aynen bırakıyoruz, sadece burada çağrılıyor.
        /// </summary>
        private void RunProcessingLogic(string girdiXml, string ciktiKlasoru, string originalFileName)
        {
            string geciciKlasor = Path.Combine(Path.GetTempPath(), "XmlCsvMini_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(geciciKlasor);
                string raporYolu = Path.Combine(geciciKlasor, originalFileName + ".OnRapor.json");

                // --- Keşif ---
                var kesifci = new XmlKesifci();
                var rapor = kesifci.Kesfet(
                    girdiXml,
                    ornekSayisi: 50_000_000,
                    maksDerinlik: 15,
                    adayMinTekrar: 1
                );

                var jsonAyar = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var raporJsonMetni = JsonSerializer.Serialize(rapor, jsonAyar);
                System.IO.File.WriteAllText(raporYolu, raporJsonMetni);

                // --- Konteynerleri filtreleme ---
                var gecerliAdaylar = rapor.AdayKayitlar
                    .Where(aday =>
                        aday.Alanlar != null &&
                        aday.Alanlar.Any(alan =>
                            alan.Yol.Contains('@') ||
                            alan.Yol.Contains("text()") ||
                            (alan.Ornekler != null && alan.Ornekler.Count > 0)
                        )
                    )
                    .ToList();

                rapor.AdayKayitlar = gecerliAdaylar;

                if (rapor.AdayKayitlar == null || rapor.AdayKayitlar.Count == 0)
                    return;

                // --- Hiyerarşi Analizi ---
                var potansiyelAnaAdaylar = new List<AdayKayit>();
                foreach (var aday in rapor.AdayKayitlar)
                {
                    bool buBirAltTablo = rapor.AdayKayitlar.Any(digerAday =>
                        aday != digerAday &&
                        aday.KayitYolu.StartsWith(digerAday.KayitYolu + "/",
                            StringComparison.Ordinal));

                    if (!buBirAltTablo)
                        potansiyelAnaAdaylar.Add(aday);
                }

                List<AdayKayit> anaAdaylar;
                if (potansiyelAnaAdaylar.Count == 1 &&
                    potansiyelAnaAdaylar.First().TahminiKayitSayisi == 1)
                {
                    var kokKonteyner = potansiyelAnaAdaylar.First();
                    var digerAdaylar = rapor.AdayKayitlar.Where(a => a != kokKonteyner).ToList();
                    anaAdaylar = digerAdaylar
                        .Where(aday =>
                            !digerAdaylar.Any(diger =>
                                aday != diger &&
                                aday.KayitYolu.StartsWith(diger.KayitYolu + "/", StringComparison.Ordinal)))
                        .ToList();
                }
                else
                {
                    anaAdaylar = potansiyelAnaAdaylar;
                }

                var hiyerarsiListesi = new List<TabloHiyerarsisi>();
                foreach (var anaAday in anaAdaylar)
                {
                    var hiyerarsi = new TabloHiyerarsisi(anaAday);
                    string anaYolPrefix = anaAday.KayitYolu + "/";

                    foreach (var aday in rapor.AdayKayitlar)
                    {
                        if (aday != anaAday &&
                            aday.KayitYolu.StartsWith(anaYolPrefix, StringComparison.Ordinal))
                        {
                            hiyerarsi.AltTablolar.Add(aday);
                        }
                    }

                    hiyerarsiListesi.Add(hiyerarsi);
                }

                // --- XML → CSV (hiyerarşik) ---
                XmlToCsvExporter.CalistirHiyerarsik(
                    girdiXml,
                    hiyerarsiListesi,
                    ciktiKlasoru,
                    rapor
                );
            }
            finally
            {
                // Keşif çıktı klasörünü istersen silebilirsin
                // try { if (Directory.Exists(geciciKlasor)) Directory.Delete(geciciKlasor, true); } catch { }
            }
        }
    }
}

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

            try
            {
                // FileShare.Read ile dosyayı oku - başka process kullanıyorsa bile okuyabilir
                using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var fileBytes = new byte[fs.Length];
                fs.Read(fileBytes, 0, (int)fs.Length);
                return File(fileBytes, "application/zip", filename);
            }
            catch (IOException)
            {
                return StatusCode(503, "Dosya henüz hazırlanıyor, lütfen biraz bekleyip tekrar deneyin.");
            }
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
                _log.LogInformation("XML işleme başlıyor: {FileName}", orijinalDosyaAdi);
                
                // SmartXmlToCsvExporter.Calistir senkron bir metot.
                // Task.Run ile CPU-bound işi arka plan thread'ine alabiliriz ama zaten buradayız.
                // Doğrudan çağırmak, bu Task içinde senkron olarak çalışmasını sağlar.
                SmartXmlToCsvExporter.Calistir(girdiXml, outputDirectory);
                
                _log.LogInformation("XML işleme tamamlandı: {FileName}", orijinalDosyaAdi);
                // --- Bitti ---

                // Çıktı klasöründe dosya yoksa hata kabul ediyoruz
                if (!Directory.EnumerateFiles(outputDirectory).Any())
                {
                    _gorevYonetimi.GorevDurumGuncelle(
                        gorevId,
                        "Failed",
                        "XML işlendi fakat dönüştürülecek uygun veri bulunamadı. Lütfen XML yapısını kontrol edin."
                    );
                    return; // finally bloğu çalışacak ama return sonrası
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
                // Dosya handle'larının serbest bırakılmasını garanti et
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // Retry ile temizlik: dosya kilidi gecikmeli kalkabilir
                await Task.Delay(200);

                TryDeleteFile(girdiXml, _log, gorevId);
                TryDeleteDirectory(outputDirectory, _log, gorevId);
                // ZIP dosyasını SİLMİYORUZ; download / dbyekaydet için lazım.
            }
        }


        /// <summary>
        /// Dosya silmeyi 3 kez dener (arada 500ms bekleme). Dosya kilidi gecikmeli kalkarsa yakalanır.
        /// </summary>
        private static void TryDeleteFile(string path, ILogger log, string gorevId)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (System.IO.File.Exists(path))
                        System.IO.File.Delete(path);
                    return;
                }
                catch (IOException) when (i < 2)
                {
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Dosya silinemedi: {Path}, JobId={JobId}", path, gorevId);
                    return;
                }
            }
        }

        /// <summary>
        /// Klasör silmeyi 3 kez dener.
        /// </summary>
        private static void TryDeleteDirectory(string path, ILogger log, string gorevId)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                    return;
                }
                catch (IOException) when (i < 2)
                {
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Klasör silinemedi: {Path}, JobId={JobId}", path, gorevId);
                    return;
                }
            }
        }
    }
}

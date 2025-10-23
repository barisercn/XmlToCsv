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
        public UploadController()
        {
            // C# uygulamalarında Türkçe karakterlerle ilgili sorunları önlemek için
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        [HttpPost("/api/upload")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileContentResult))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(string))]
        [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(string))]
        public async Task<IActionResult> UploadFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest("Lütfen bir dosya seçin.");
            }

            var tempFilePath = Path.GetTempFileName();
            var outputDirectory = Path.Combine(Path.GetTempPath(), "XmlCsvMini_Output_" + Guid.NewGuid().ToString("N"));
            var zipPath = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(file.FileName) + "_csv.zip");

            try
            {
                // 1. Gelen dosyayı geçici bir yola kaydet
                await using (var stream = new FileStream(tempFilePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // 2. Çıktı klasörünü oluştur
                Directory.CreateDirectory(outputDirectory);

                // 3. XML işleme mantığını çalıştır
                RunProcessingLogic(tempFilePath, outputDirectory, Path.GetFileNameWithoutExtension(file.FileName));

                // 4. Eğer çıktı klasöründe dosya varsa, zip'le
                if (!Directory.EnumerateFiles(outputDirectory).Any())
                {
                    return StatusCode(500, "XML işlendi fakat dönüştürülecek uygun veri bulunamadı. Lütfen XML yapısını kontrol edin.");
                }

                if (System.IO.File.Exists(zipPath))
                {
                    System.IO.File.Delete(zipPath);
                }
                ZipFile.CreateFromDirectory(outputDirectory, zipPath);

                // 5. Zip dosyasını byte dizisi olarak oku ve kullanıcıya gönder
                return Ok(new { fileName = Path.GetFileName(zipPath) });
            }
            catch (Exception ex)
            {
                // Geliştirme aşamasında hatanın detayını görmek önemlidir.
                return StatusCode(500, $"Sunucu hatası: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // 6. Arkada bırakılan geçici dosyaları ve klasörleri temizle
                if (System.IO.File.Exists(tempFilePath)) System.IO.File.Delete(tempFilePath);
                if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, true);
                // if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
            }
        }
        [HttpGet("/api/download/{filename}")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(FileContentResult))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(string))]
        public IActionResult DownloadFile(string filename)
        {


            var zipPath = Path.Combine(Path.GetTempPath(), filename);

            if (!System.IO.File.Exists(zipPath))
            {
                return NotFound("Dosya bulunamadı veya zaten indirilmiş.");
            }

            var fileBytes = System.IO.File.ReadAllBytes(zipPath);

            // Dosyayı kullanıcıya gönderdikten sonra sunucudan silerek temizlik yapıyoruz.
            try
            {
                System.IO.File.Delete(zipPath);
            }
            catch
            {
                // Silme başarısız olursa loglanabilir ama indirmeyi engellememeli.
            }

            return File(fileBytes, "application/zip", filename);
        }

        /// <summary>
        /// Orijinal Program.cs'teki 'CalistirTamOtomatikIslem' metodunun API için uyarlanmış hali.
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
                var rapor = kesifci.Kesfet(girdiXml, ornekSayisi: 50_000_000, maksDerinlik: 15, adayMinTekrar: 1);

                var jsonAyar = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var raporJsonMetni = JsonSerializer.Serialize(rapor, jsonAyar);
                System.IO.File.WriteAllText(raporYolu, raporJsonMetni);

                // --- Konteynerleri Filtreleme ---
                var orijinalAdaySayisi = rapor.AdayKayitlar.Count;
                var gecerliAdaylar = rapor.AdayKayitlar.Where(aday =>
                    aday.Alanlar != null && aday.Alanlar.Any(alan =>
                        alan.Yol.Contains('@') ||
                        alan.Yol.Contains("text()") ||
                        (alan.Ornekler != null && alan.Ornekler.Count > 0)
                    )
                ).ToList();
                rapor.AdayKayitlar = gecerliAdaylar;

                if (rapor.AdayKayitlar == null || rapor.AdayKayitlar.Count == 0)
                {
                    // Hata fırlatmak yerine burada durabiliriz, üst katman bunu kontrol edecek.
                    return;
                }

                // --- Hiyerarşi Analizi ---
                var potansiyelAnaAdaylar = new List<AdayKayit>();
                foreach (var aday in rapor.AdayKayitlar)
                {
                    bool buBirAltTablo = rapor.AdayKayitlar.Any(digerAday =>
                        aday != digerAday && aday.KayitYolu.StartsWith(digerAday.KayitYolu + "/"));

                    if (!buBirAltTablo)
                    {
                        potansiyelAnaAdaylar.Add(aday);
                    }
                }

                var anaAdaylar = new List<AdayKayit>();
                if (potansiyelAnaAdaylar.Count == 1 && potansiyelAnaAdaylar.First().TahminiKayitSayisi == 1)
                {
                    var kokKonteyner = potansiyelAnaAdaylar.First();
                    var digerAdaylar = rapor.AdayKayitlar.Where(a => a != kokKonteyner).ToList();
                    anaAdaylar = digerAdaylar.Where(aday =>
                        !digerAdaylar.Any(diger =>
                            aday != diger && aday.KayitYolu.StartsWith(diger.KayitYolu + "/"))
                    ).ToList();
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
                        if (aday != anaAday && aday.KayitYolu.StartsWith(anaYolPrefix))
                        {
                            hiyerarsi.AltTablolar.Add(aday);
                        }
                    }
                    hiyerarsiListesi.Add(hiyerarsi);
                }

                // --- Dönüştürme ---
                XmlToCsvExporter.CalistirHiyerarsik(girdiXml, hiyerarsiListesi, ciktiKlasoru, rapor);
            }
            finally
            {
                try
                {
                    // if (Directory.Exists(geciciKlasor)) Directory.Delete(geciciKlasor, true);
                }
                catch { /* Sorun değil */ }
            }
        }
    }
}
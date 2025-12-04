using Microsoft.AspNetCore.Mvc; // [ApiController], ControllerBase, [HttpPost], IActionResult vb. için
using Microsoft.AspNetCore.Http; // StatusCodes (örn: Status500InternalServerError) için
using System;                     // Exception yakalama için
using System.IO;                  // Path.Combine, Path.GetTempPath gibi dosya yolu işlemleri için
using System.Threading.Tasks;     // 'async Task<IActionResult>' için
using XmlCsvMini.Models;          // Frontend'den gelecek 'DbKaydetRequest' modelini kullanmak için
using XmlCsvMini.Services;        // 'DbImporterService' gibi bir servisi inject etmek (eklemek) için
using Microsoft.Extensions.Logging; // Hata loglaması için (en iyi pratiktir)
namespace XmlCsvMini.Controllers
{
    [ApiController]
    public class DatabaseController : ControllerBase
    {
        // --- Servisleri ve günlüğü (logger) içeriye almak için ---
        private readonly IVeriAktarimServisi _veriAktarimServisi;  // (Bir sonraki adımda bu servisi oluşturacağız)
        private readonly ILogger<DatabaseController> _gunluk; // logger = günlük kaydedici

        // --- Constructor: framework bu servisleri otomatik olarak "inject" eder ---
        public DatabaseController(IVeriAktarimServisi veriAktarimServisi, ILogger<DatabaseController> log)
        {
            _veriAktarimServisi = veriAktarimServisi;
            _gunluk = log;
        }

        // --- Frontend tarafından çağrılacak API ucu ---
        [HttpPost("/api/dbyekaydet")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(object))]
        [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(string))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(string))]
        [ProducesResponseType(StatusCodes.Status500InternalServerError, Type = typeof(string))]
        public async Task<IActionResult> VeritabaniKaydet([FromBody] DbKaydetRequest istek)
        {
            if (istek == null || string.IsNullOrEmpty(istek.DosyaAdi))
            {
                _gunluk.LogWarning("VeritabaniKaydet çağrısı 'DosyaAdi' olmadan yapıldı.");
                return BadRequest("Dosya adı (DosyaAdi) eksik.");
            }

            // Zip dosyasının tam yolunu bul
            var zipYolu = Path.Combine(Path.GetTempPath(), istek.DosyaAdi);
            if (!System.IO.File.Exists(zipYolu))
            {
                _gunluk.LogWarning($"İşlenecek zip dosyası bulunamadı: {zipYolu}");
                return NotFound("İşlenecek zip dosyası sunucuda bulunamadı.");
            }

            try
            {
                // Asıl işi yapan servisi çağır
                var sonuc = await _veriAktarimServisi.ZiptenVeritabaninaAktarAsync(zipYolu);

                // 👉 Yeni fonksiyonu burada, doğru değişken adıyla çağırıyoruz:
                await _veriAktarimServisi.OlusanTablolariTerminaleYazdirAsync();

                _gunluk.LogInformation($"{istek.DosyaAdi} veritabanına başarıyla aktarıldı.");
                return Ok(new { message = $"Veri aktarımı tamamlandı. {sonuc.AktarilanTabloSayisi} tablo, {sonuc.ToplamSatirSayisi} satır işlendi." });
            }
            catch (Exception hata)
            {
                _gunluk.LogError(hata, $"Veritabanına kaydederken hata oluştu: {istek.DosyaAdi}");
                return StatusCode(500, $"Sunucu hatası: {hata.Message}");
            }
        }
    }
}
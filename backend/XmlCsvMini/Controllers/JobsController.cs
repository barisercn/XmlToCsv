using Microsoft.AspNetCore.Mvc;
using XmlCsvMini.Models;
using XmlCsvMini.Services;
using System.Linq;

namespace XmlCsvMini.Controllers
{
    [ApiController]
    [Route("api/jobs")]
    public class JobsController : ControllerBase
    {
        private readonly IIslemeGoreviYonetimi _gorevYonetimi;
        private readonly ILogger<JobsController> _logger;

        public JobsController(IIslemeGoreviYonetimi gorevYonetimi,
                              ILogger<JobsController> logger)
        {
            _gorevYonetimi = gorevYonetimi;
            _logger = logger;
        }

        /// <summary>
        /// Belirli bir jobId için, o işin durumunu döner.
        /// Örn: GET /api/jobs/ab12cd34...
        /// </summary>
        [HttpGet("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(object))]
        [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(string))]
        public IActionResult GetJobStatus(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest("Geçersiz jobId.");

            var gorev = _gorevYonetimi.GorevGetir(id);
            if (gorev == null)
                return NotFound("Bu ID ile kayıtlı bir iş bulunamadı.");

            // Frontend'in rahat kullanabilmesi için sade bir DTO dönüyoruz
            var dto = new
            {
                jobId = gorev.Id,
                status = gorev.Durum,              // Pending, Running, Completed, Failed
                message = gorev.Mesaj,
                originalFileName = gorev.OrijinalDosyaAdi,
                downloadFileName = gorev.CiktiZipAdi,  // Completed ise dolu olacak
                createdAtUtc = gorev.OlusturmaZamaniUtc,
                updatedAtUtc = gorev.SonGuncellemeZamaniUtc
            };

            return Ok(dto);
        }

        // İsteğe bağlı: Tüm işleri görmek için (debug amaçlı) basit bir endpoint.
        // İstemezsen bu metodu silebilirsin.
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(object))]
        public IActionResult GetAllJobs()
        {
            var tumGorevler = _gorevYonetimi
                .TumGorevler()
                .OrderByDescending(g => g.OlusturmaZamaniUtc)
                .Select(g => new
                {
                    jobId = g.Id,
                    status = g.Durum,
                    originalFileName = g.OrijinalDosyaAdi,
                    downloadFileName = g.CiktiZipAdi,
                    createdAtUtc = g.OlusturmaZamaniUtc,
                    updatedAtUtc = g.SonGuncellemeZamaniUtc
                })
                .ToList();

            return Ok(tumGorevler);
        }
    }
}

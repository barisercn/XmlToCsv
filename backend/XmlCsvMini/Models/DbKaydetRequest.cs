// Models/DbKaydetRequest.cs
using System.Text.Json.Serialization;

namespace XmlCsvMini.Models
{

    public class DbKaydetRequest
    {
        [JsonPropertyName("fileName")]
        public string DosyaAdi { get; set; } = "";

        // "Full" / "Daily" / "Direct" vb.
        [JsonPropertyName("loadType")]
        public string? YuklemeTuru { get; set; }

        // Frontend'den "2025-12-10" gibi gelecek → DateTime? olarak alıyoruz
        [JsonPropertyName("dataDate")]
        public DateTime? VeriTarihi { get; set; }
    }

}
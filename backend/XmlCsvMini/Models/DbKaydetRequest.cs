// Models/DbKaydetRequest.cs
using System.Text.Json.Serialization;

namespace XmlCsvMini.Models
{
    public class DbKaydetRequest
    {
        [JsonPropertyName("fileName")]
        public string DosyaAdi { get; set; } = "";
    }
}
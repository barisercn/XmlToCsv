using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XmlCsvMini.Models
{
    /// <summary>Dönüştürme için mapping/eşleme yapılandırması.</summary>
    public sealed class EslemeYapilandirmasi
    {
        public string Mod { get; set; } = "manual";          // "auto" | "guided" | "manual"

        [JsonPropertyName("kayitYolu")]
        public string? KayitYolu { get; set; }

        // JSON: "adAlanlari"  →  C# : AdUzaylari
        [JsonPropertyName("adAlanlari")]
        public SozlukAdUzayi? AdUzaylari { get; set; }

        // JSON: "sutunlar" → C#: Sutunlar
        [JsonPropertyName("sutunlar")]
        public List<KolonEsleme> Sutunlar { get; set; } = new();

        // JSON: "diziler" → C#: Diziler (opsiyonel; yoksa null olabilir)
        [JsonPropertyName("diziler")]
        public DiziStratejisi? Diziler { get; set; } = new();

        // Aşağıdakiler mapping’inde olmayabilir; sorun değil (opsiyonel)
        [JsonPropertyName("tercihler")]
        public TurCikarim Tercihler { get; set; } = new();

        [JsonPropertyName("kimlik")]
        public KimlikYapisi Kimlik { get; set; } = new();

        [JsonPropertyName("cikti")]
        public CiktiSecenekleri Cikti { get; set; } = new();

        [JsonPropertyName("kisitlar")]
        public Kisitlar? Kisitlar { get; set; }
    }

    public sealed class SozlukAdUzayi : Dictionary<string, string>
    {
        // === YENİ EKLENEN KISIM BAŞLANGICI ===
        public SozlukAdUzayi() { } // Boş constructor
        public SozlukAdUzayi(IDictionary<string, string> dictionary) : base(dictionary) { } // Dönüşüm constructor'ı
                                                                                            // === YENİ EKLENEN KISIM SONU ===
    }

    public sealed class KolonEsleme
    {
        // JSON: "ad" → C#: Csv (sütun başlığı)
        [JsonPropertyName("ad")]
        public string Csv { get; set; } = "";

        // JSON: "sec" → C#: Kaynak (XPath)
        [JsonPropertyName("sec")]
        public string Kaynak { get; set; } = "";

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public HedefVeriTuru Tur { get; set; } = HedefVeriTuru.String;

        public string? Normalize { get; set; }

        // JSON: "zorunlu" → C#: Zorunlu
        [JsonPropertyName("zorunlu")]
        public bool Zorunlu { get; set; } = false;

        // JSON: "varsayilan" → C#: Varsayilan (mapping’inde varsa)
        [JsonPropertyName("varsayilan")]
        public string? Varsayilan { get; set; }

        // JSON: "birlestir" → C#: Birlestir (sütun bazlı join için)
        [JsonPropertyName("birlestir")]
        public string? Birlestir { get; set; }
    }

    public sealed class DiziStratejisi
    {
        public string Strateji { get; set; } = "join";   // "split" | "join"  (flat için join mantıklı)
        public string Birlestirici { get; set; } = " | ";
        public string TabloAdlandirma { get; set; } = "{parent}_{name}";
    }

    public sealed class TurCikarim
    {
        public bool CikarimAcik { get; set; } = true;
        public List<string> TarihBicimleri { get; set; } = new() { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssZ" };
        public List<string> MantiksalDegerler { get; set; } = new() { "true", "false", "1", "0", "True", "False" };
    }

    public sealed class KimlikYapisi
    {
        public List<string> BirincilAnahtar { get; set; } = new() { "id" };
        public DeterministikId DeterministikId { get; set; } = new();
    }

    public sealed class DeterministikId
    {
        public bool Etkin { get; set; } = true;
        public List<string> TohumAlanlar { get; set; } = new() { "./Id" };
        public string Algoritma { get; set; } = "sha1";   // "sha1" | "sha256"
        public string KolonAdi { get; set; } = "entity_id";
    }

    public sealed class CiktiSecenekleri
    {
        public string Kok { get; set; } = "output";
        public string CekirdekKlasor { get; set; } = "core";
        public string LogKlasor { get; set; } = "_logs";
        public string ReddKlasor { get; set; } = "_rejects";
        public string DosyaAdiBicimi { get; set; } = "PascalCase";
        public string KolonAdiBicimi { get; set; } = "snake_case";
        public int MaksDosyaBoyutuMb { get; set; } = 150;

        // YENİ:
        [System.Text.Json.Serialization.JsonPropertyName("ayirici")]
        public string Ayirici { get; set; } = ";";   // ";" veya "\t" (TSV) veya ","

        [System.Text.Json.Serialization.JsonPropertyName("tumAlanlariTirnakla")]
        public bool TumAlanlariTirnakla { get; set; } = false; // İsteğe bağlı
    }

    public sealed class Kisitlar
    {
        public List<string>? Zorunlu { get; set; }
        public List<string>? Tekil { get; set; }
    }

    /// <summary>Hedef veri türleri — ÜYE adları (JSON için) İngilizce.</summary>
    public enum HedefVeriTuru
    {
        String, Integer, Decimal, Boolean, Date, DateTime
    }
}

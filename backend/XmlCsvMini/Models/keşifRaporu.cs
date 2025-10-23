using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XmlCsvMini.Models
{
    /// <summary>Keşif modunun ürettiği raporun kök modeli.</summary>
    public sealed class KesifRaporu
    {
        public string Kaynak { get; set; } = "";
        public Dictionary<string, string> AdUzaylari { get; set; } = new();
        public List<AdayKayit> AdayKayitlar { get; set; } = new();
        public KesifMeta Bilgi { get; set; } = new();
    }


    /// <summary>Keşif raporu ve çalıştırma hakkında meta bilgiler.</summary>
    public sealed class KesifMeta
    {
        public string SematikSurum { get; set; } = "1.0.0";
        public DateTime UretimZamaniUtc { get; set; } = DateTime.UtcNow;
        public string ParametreOzeti { get; set; } = "";
        public long TarananDugumSayisi { get; set; }
        public List<string> Notlar { get; set; } = new();
    }

    /// <summary>Bir kayıt kökü adayı (ör: /Root/Person).</summary>
    public class AdayKayit
    {
        public string KayitYolu { get; set; } = "";
        public long TahminiKayitSayisi { get; set; }
        public List<AlanBilgisi> Alanlar { get; set; } = new();
        public List<IcIceDizi> IcIceDiziler { get; set; } = new();
        public List<string> Uyarilar { get; set; } = new();
    }

    /// <summary>Bir alanı ve özelliklerini tanımlar.</summary>
    public sealed class AlanBilgisi
    {
        public string Yol { get; set; } = "";

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DugumTuru Tur { get; set; } = DugumTuru.Element;

        public string Kardinalite { get; set; } = "0..1";
        public List<string> Ornekler { get; set; } = new();

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CikarilanVeriTuru CikarilanTur { get; set; } = CikarilanVeriTuru.String;

        public int? MaksKarakterUzunlugu { get; set; }
        public int? YaklasikFarkliDegerSayisi { get; set; }
        public List<string> Notlar { get; set; } = new();
    }

    /// <summary>Tekrarlayan (dizi) alt düğüm önerisi.</summary>
    public sealed class IcIceDizi
    {
        public string TekrarYolu { get; set; } = "";
        public string OnerilenTablo { get; set; } = "";
        public List<string> Notlar { get; set; } = new();
    }

    /// <summary>XML düğüm türü — ÜYE adları (JSON için) İngilizce.</summary>
    public enum DugumTuru
    {
        Element,   // öğe
        Attribute, // nitelik
        Text       // metin
    }

    /// <summary>Keşifle çıkarılan veri türü — ÜYE adları (JSON için) İngilizce.</summary>
    public enum CikarilanVeriTuru
    {
        String, Integer, Decimal, Boolean, Date, DateTime
    }
}

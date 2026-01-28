using System.Collections.Generic;

namespace XmlCsvMini.Models
{
    /// <summary>
    /// Düzleştirilmiş tablo yapısını temsil eder.
    /// Tekil (0..1) alt elemanlar ana tabloya dahil edilir,
    /// Çoklu (0..N) alt elemanlar ayrı tablo olarak kalır.
    /// </summary>
    public class DuzlestirilmisTabloYapisi
    {
        /// <summary>Tablonun adı (CSV dosya adı için)</summary>
        public string TabloAdi { get; set; } = "";

        /// <summary>XML'deki kayıt yolu (örn: /PFA/Records/Entity)</summary>
        public string KayitYolu { get; set; } = "";

        /// <summary>
        /// Bu tabloya ait sütunlar.
        /// Düzleştirilmiş alanlar prefix ile gelir (örn: address_city)
        /// </summary>
        public List<DuzSutun> Sutunlar { get; set; } = new();

        /// <summary>
        /// Bu tablodan türeyen alt tablolar (0..N kardinaliteli elemanlar)
        /// </summary>
        public List<DuzlestirilmisTabloYapisi> AltTablolar { get; set; } = new();

        /// <summary>
        /// Üst tablo yolu (ilişki kurmak için)
        /// </summary>
        public string? UstTabloYolu { get; set; }
    }

    /// <summary>
    /// Düzleştirilmiş bir sütunu temsil eder.
    /// </summary>
    public class DuzSutun
    {
        /// <summary>CSV'deki sütun adı (örn: address_city)</summary>
        public string SutunAdi { get; set; } = "";

        /// <summary>XML'deki kaynak yol (göreli XPath benzeri)</summary>
        public string KaynakYol { get; set; } = "";

        /// <summary>Veri türü</summary>
        public CikarilanVeriTuru VeriTuru { get; set; } = CikarilanVeriTuru.String;

        /// <summary>Birden fazla değer varsa birleştirici</summary>
        public string? Birlestirici { get; set; }

        /// <summary>Bu sütun bir attribute mi?</summary>
        public bool AttributeMi { get; set; }

        /// <summary>Bu sütun text() mi?</summary>
        public bool TextMi { get; set; }
    }
}

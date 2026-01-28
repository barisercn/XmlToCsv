// DuzlestirilmisCsvExporter.cs - Düzleştirilmiş tablo yapısıyla CSV export

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using XmlCsvMini.Altyapi;
using XmlCsvMini.Models;

namespace XmlCsvMini.Services
{
    /// <summary>
    /// Düzleştirilmiş tablo yapısını kullanarak XML'den CSV'ye export yapar.
    /// Tekil alt elemanlar ana tabloya düzleştirilir.
    /// </summary>
    public static class DuzlestirilmisCsvExporter
    {
        /// <summary>
        /// Düzleştirilmiş tablo yapılarından CSV dosyaları oluşturur.
        /// </summary>
        public static void Calistir(
            string inputXmlPath,
            List<DuzlestirilmisTabloYapisi> tabloYapilari,
            string ciktiKlasoru)
        {
            Directory.CreateDirectory(ciktiKlasoru);

            var csvYazicilari = new Dictionary<string, CsvYaziciPaketi>();
            var kaynaklar = new List<IDisposable>();

            try
            {
                // 1. Tüm tablolar için CSV dosyalarını ve header'ları oluştur
                foreach (var tabloYapisi in tabloYapilari)
                {
                    CsvDosyalariniOlusturRecursive(tabloYapisi, ciktiKlasoru, csvYazicilari, kaynaklar);
                }

                // 2. XML'i stream olarak oku ve verileri yaz
                var settings = new XmlReaderSettings
                {
                    IgnoreWhitespace = true,
                    IgnoreComments = true,
                    DtdProcessing = DtdProcessing.Ignore,
                    CloseInput = true
                };

                foreach (var tabloYapisi in tabloYapilari)
                {
                    var elemanAdi = SonSegmentAdi(tabloYapisi.KayitYolu);

                    using var reader = EncodingAwareXml.CreateAutoXmlReader(inputXmlPath, settings);

                    while (reader.ReadToFollowing(elemanAdi))
                    {
                        using var subTree = reader.ReadSubtree();
                        var recordEl = XElement.Load(subTree);

                        // Ana tablo verisini yaz
                        KayitYazRecursive(tabloYapisi, recordEl, csvYazicilari);
                    }
                }
            }
            finally
            {
                // Kaynakları temizle
                foreach (var kaynak in kaynaklar)
                {
                    try { kaynak?.Dispose(); } catch { }
                }
            }
        }

        /// <summary>
        /// Recursive olarak CSV dosyalarını oluşturur.
        /// </summary>
        private static void CsvDosyalariniOlusturRecursive(
            DuzlestirilmisTabloYapisi tabloYapisi,
            string ciktiKlasoru,
            Dictionary<string, CsvYaziciPaketi> csvYazicilari,
            List<IDisposable> kaynaklar)
        {
            // Benzersiz dosya adı oluştur (üst tablonun adını da dahil et)
            var dosyaAdi = TabloAdiOlustur(tabloYapisi);
            var csvYolu = Path.Combine(ciktiKlasoru, $"{dosyaAdi}.csv");

            // Eğer bu yol için zaten yazıcı varsa atla
            if (csvYazicilari.ContainsKey(tabloYapisi.KayitYolu))
                return;

            var writer = new StreamWriter(csvYolu, false, new UTF8Encoding(true));
            var csv = YeniCsvWriter(writer);

            kaynaklar.Add(csv);
            kaynaklar.Add(writer);

            csvYazicilari[tabloYapisi.KayitYolu] = new CsvYaziciPaketi
            {
                Yazici = csv,
                TabloYapisi = tabloYapisi
            };

            // Header yaz
            foreach (var sutun in tabloYapisi.Sutunlar)
            {
                csv.WriteField(sutun.SutunAdi);
            }
            csv.NextRecord();

            // Alt tabloları da oluştur
            foreach (var altTablo in tabloYapisi.AltTablolar)
            {
                CsvDosyalariniOlusturRecursive(altTablo, ciktiKlasoru, csvYazicilari, kaynaklar);
            }
        }

        /// <summary>
        /// Recursive olarak kayıt yazar.
        /// </summary>
        private static void KayitYazRecursive(
            DuzlestirilmisTabloYapisi tabloYapisi,
            XElement recordEl,
            Dictionary<string, CsvYaziciPaketi> csvYazicilari)
        {
            if (!csvYazicilari.TryGetValue(tabloYapisi.KayitYolu, out var paket))
                return;

            var csv = paket.Yazici;

            // Ana tablo sütunlarını yaz
            foreach (var sutun in tabloYapisi.Sutunlar)
            {
                var deger = DegerCek(recordEl, sutun, tabloYapisi.KayitYolu);
                csv.WriteField(deger);
            }
            csv.NextRecord();

            // Alt tablolar için kayıtları yaz
            foreach (var altTablo in tabloYapisi.AltTablolar)
            {
                var goreliYol = altTablo.KayitYolu.Substring(tabloYapisi.KayitYolu.Length);
                var altElemanlar = BulAltElemanlar(recordEl, goreliYol);

                foreach (var altEl in altElemanlar)
                {
                    KayitYazRecursive(altTablo, altEl, csvYazicilari);
                }
            }
        }

        /// <summary>
        /// Sütun için değer çeker (düzleştirilmiş yapıyı destekler).
        /// </summary>
        private static string DegerCek(XElement el, DuzSutun sutun, string tabloYolu)
        {
            var kaynakYol = sutun.KaynakYol;

            // Eğer kaynak yol sadece attribute ise
            if (sutun.AttributeMi || kaynakYol.StartsWith("./@") || kaynakYol.StartsWith("@"))
            {
                var attrAdi = kaynakYol
                    .Replace("./@", "")
                    .Replace("@", "");

                var attr = el.Attribute(attrAdi);
                return attr?.Value?.Trim() ?? "";
            }

            // Eğer text() ise
            if (sutun.TextMi || kaynakYol.Contains("text()"))
            {
                // Doğrudan elemanın metnini al
                var textParts = kaynakYol.Replace("./text()", "").Replace("text()", "").Trim('/');

                if (string.IsNullOrEmpty(textParts))
                {
                    return el.Value?.Trim() ?? "";
                }
                else
                {
                    // Önce yolu takip et, sonra text al
                    var hedef = YoluTakipEt(el, "./" + textParts);
                    return hedef?.Value?.Trim() ?? "";
                }
            }

            // Normal element yolu
            var degerler = DegerleriCek(el, kaynakYol);

            if (degerler.Count == 0)
                return "";

            if (degerler.Count == 1)
                return degerler[0];

            // Birden fazla değer varsa birleştir
            var birlestirici = sutun.Birlestirici ?? " | ";
            return string.Join(birlestirici, degerler);
        }

        /// <summary>
        /// Yolu takip ederek tüm değerleri çeker.
        /// </summary>
        private static List<string> DegerleriCek(XElement el, string yol)
        {
            var sonuc = new List<string>();

            if (el == null || string.IsNullOrWhiteSpace(yol))
                return sonuc;

            // Yolu temizle
            var temizYol = yol.TrimStart('.').TrimStart('/');

            if (string.IsNullOrEmpty(temizYol))
            {
                var v = el.Value?.Trim();
                if (!string.IsNullOrEmpty(v))
                    sonuc.Add(v);
                return sonuc;
            }

            var parcalar = temizYol.Split('/', StringSplitOptions.RemoveEmptyEntries);

            IEnumerable<XElement> mevcutElemanlar = new[] { el };

            foreach (var parca in parcalar)
            {
                if (parca.StartsWith("@"))
                {
                    // Attribute
                    var attrAdi = parca.Substring(1);
                    foreach (var e in mevcutElemanlar)
                    {
                        var attr = e.Attribute(attrAdi);
                        if (attr != null && !string.IsNullOrEmpty(attr.Value?.Trim()))
                        {
                            sonuc.Add(attr.Value.Trim());
                        }
                    }
                    return sonuc;
                }
                else if (parca == "text()")
                {
                    // Text
                    foreach (var e in mevcutElemanlar)
                    {
                        var v = e.Value?.Trim();
                        if (!string.IsNullOrEmpty(v))
                            sonuc.Add(v);
                    }
                    return sonuc;
                }
                else
                {
                    // Child element
                    mevcutElemanlar = mevcutElemanlar
                        .SelectMany(e => e.Elements()
                            .Where(c => c.Name.LocalName == parca));
                }
            }

            // Son elemanlarin değerlerini al
            foreach (var e in mevcutElemanlar)
            {
                var v = e.Value?.Trim();
                if (!string.IsNullOrEmpty(v))
                    sonuc.Add(v);
            }

            return sonuc;
        }

        /// <summary>
        /// Yolu takip ederek element bulur.
        /// </summary>
        private static XElement? YoluTakipEt(XElement el, string yol)
        {
            if (el == null || string.IsNullOrWhiteSpace(yol))
                return el;

            var temizYol = yol.TrimStart('.').TrimStart('/');

            if (string.IsNullOrEmpty(temizYol))
                return el;

            var parcalar = temizYol.Split('/', StringSplitOptions.RemoveEmptyEntries);

            XElement? mevcut = el;

            foreach (var parca in parcalar)
            {
                if (parca == "text()" || parca.StartsWith("@"))
                    break;

                mevcut = mevcut?.Elements().FirstOrDefault(e => e.Name.LocalName == parca);

                if (mevcut == null)
                    break;
            }

            return mevcut;
        }

        /// <summary>
        /// Göreli yola göre alt elemanları bulur.
        /// </summary>
        private static List<XElement> BulAltElemanlar(XElement context, string goreliYol)
        {
            var sonuc = new List<XElement>();

            if (context == null || string.IsNullOrWhiteSpace(goreliYol))
                return sonuc;

            var temizYol = goreliYol.TrimStart('/');
            var parcalar = temizYol.Split('/', StringSplitOptions.RemoveEmptyEntries);

            IEnumerable<XElement> mevcut = new[] { context };

            foreach (var parca in parcalar)
            {
                mevcut = mevcut.SelectMany(e => e.Elements()
                    .Where(c => c.Name.LocalName == parca));
            }

            return mevcut.ToList();
        }

        /// <summary>
        /// Tablo adı oluşturur (üst tablo adını da içerir).
        /// </summary>
        private static string TabloAdiOlustur(DuzlestirilmisTabloYapisi tablo)
        {
            var parcalar = tablo.KayitYolu.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parcalar.Length <= 2)
            {
                return MetniSnakeCaseYap(parcalar[^1]);
            }

            // Anlamlı parçaları al (ilk 1-2 container'ı atla)
            var anlamliParcalar = parcalar.Skip(Math.Max(0, parcalar.Length - 2)).ToArray();

            return string.Join("_", anlamliParcalar.Select(MetniSnakeCaseYap));
        }

        /// <summary>
        /// Son segment adını alır.
        /// </summary>
        private static string SonSegmentAdi(string kayitYolu)
        {
            if (string.IsNullOrWhiteSpace(kayitYolu)) return "";
            var parts = kayitYolu.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var last = parts[^1];
            var idx = last.IndexOf(':');
            if (idx >= 0 && idx < last.Length - 1)
                last = last[(idx + 1)..];
            return last;
        }

        /// <summary>
        /// Yeni CSV writer oluşturur.
        /// </summary>
        private static CsvWriter YeniCsvWriter(TextWriter writer)
        {
            var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                NewLine = "\n",
                TrimOptions = TrimOptions.Trim,
                Delimiter = ";"
            };
            return new CsvWriter(writer, cfg);
        }

        /// <summary>
        /// Metni snake_case'e çevirir.
        /// </summary>
        private static string MetniSnakeCaseYap(string girdi)
        {
            if (string.IsNullOrWhiteSpace(girdi)) return "tablo";

            var sb = new StringBuilder();
            for (int i = 0; i < girdi.Length; i++)
            {
                char ch = girdi[i];
                if (char.IsUpper(ch) && i > 0 && sb.Length > 0 && sb[sb.Length - 1] != '_')
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(ch));
            }

            return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "[^a-z0-9_]+", "_").Trim('_');
        }

        /// <summary>
        /// CSV yazıcı paketi.
        /// </summary>
        private class CsvYaziciPaketi
        {
            public CsvWriter Yazici { get; set; } = null!;
            public DuzlestirilmisTabloYapisi TabloYapisi { get; set; } = null!;
        }
    }
}

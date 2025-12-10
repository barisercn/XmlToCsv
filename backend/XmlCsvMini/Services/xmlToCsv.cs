// Services/XmlToCsvExporter.cs - STREAMING, TÜM KAYITLARI İŞLER

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
    public static class XmlToCsvExporter
    {
        /// <summary>
        /// Keşif sonucu oluşan hiyerarşiye göre XML'i CSV'lere dönüştürür.
        /// - XML hiç XDocument ile komple RAM'e alınmaz.
        /// - Her ana kayıt yolu (Person, Company, Event ...) için dosya streaming olarak
        ///   baştan sona bir kez taranır ve bütün kayıtlar işlenir.
        /// </summary>
        public static void CalistirHiyerarsik(
            string inputXmlPath,
            List<TabloHiyerarsisi> hiyerarsiListesi,
            string ciktiKlasoru,
            KesifRaporu rapor)
        {
            var eslemeSozlugu = new Dictionary<string, EslemeYapilandirmasi>();
            var csvYazicilari = new Dictionary<string, CsvWriter>();
            var anaTabloYazilacakSutunlar = new Dictionary<string, List<KolonEsleme>>();

            // Aynı fiziksel CSV dosyası için tek writer kullanmak adına
            var dosyaYoluWriterMap = new Dictionary<string, CsvWriter>(StringComparer.OrdinalIgnoreCase);
            var kaynaklar = new List<IDisposable>();

            try
            {
                Directory.CreateDirectory(ciktiKlasoru);

                // ================================
                // 1) CSV dosyalarını ve HEADER'ları hazırla
                // ================================
                foreach (var hiyerarsi in hiyerarsiListesi)
                {
                    var anaAday = hiyerarsi.AnaTablo;

                    // Ana tablo dosya adı
                    string anaTabloDosyaAdi = TableNameBuilder.BuildTableName(
                        anaAday.KayitYolu,
                        anaAday.KayitYolu
                    );

                    // Esleme üret
                    var anaEsleme = EslemeBaslatici.DuzEslemeOlustur(
                        rapor,
                        rapor.AdayKayitlar.IndexOf(anaAday)
                    );
                    eslemeSozlugu[anaAday.KayitYolu] = anaEsleme;

                    var anaCsvYolu = Path.Combine(ciktiKlasoru, $"{anaTabloDosyaAdi}.csv");
                    CsvWriter anaCsv;
                    bool anaYeniOlustu = false;

                    if (!dosyaYoluWriterMap.TryGetValue(anaCsvYolu, out anaCsv!))
                    {
                        Console.WriteLine($"  -> Ana tablo için CSV oluşturuluyor: {Path.GetFileName(anaCsvYolu)}");
                        var anaWriter = new StreamWriter(anaCsvYolu, false, new UTF8Encoding(true));
                        anaCsv = YeniCsvWriter(
                            anaWriter,
                            anaEsleme.Cikti?.Ayirici,
                            anaEsleme.Cikti?.TumAlanlariTirnakla ?? false
                        );

                        dosyaYoluWriterMap[anaCsvYolu] = anaCsv;
                        csvYazicilari[anaAday.KayitYolu] = anaCsv;
                        kaynaklar.Add(anaCsv);
                        kaynaklar.Add(anaWriter);
                        anaYeniOlustu = true;
                    }
                    else
                    {
                        csvYazicilari[anaAday.KayitYolu] = anaCsv;
                    }

                    // Alt tablolara ait alanları ana tablodan çıkar
                    var altTabloRelativePaths = new HashSet<string>(
                        hiyerarsi.AltTablolar.Select(alt =>
                            "." + alt.KayitYolu.Substring(anaAday.KayitYolu.Length))
                    );

                    var gecerliAnaSutunlar = anaEsleme.Sutunlar
                        .Where(col => !altTabloRelativePaths.Contains(col.Kaynak))
                        .ToList();

                    anaTabloYazilacakSutunlar[anaAday.KayitYolu] = gecerliAnaSutunlar;

                    // HEADER (sadece dosya ilk kez oluşturulmuşsa)
                    if (anaYeniOlustu)
                    {
                        foreach (var col in gecerliAnaSutunlar)
                            anaCsv.WriteField(col.Csv);
                        anaCsv.NextRecord();
                    }

                    // ---- ALT TABLOLAR ----
                    foreach (var altAday in hiyerarsi.AltTablolar)
                    {
                        string altTabloDosyaAdi = TableNameBuilder.BuildTableName(
                            anaAday.KayitYolu,
                            altAday.KayitYolu
                        );

                        var altEsleme = EslemeBaslatici.DuzEslemeOlustur(
                            rapor,
                            rapor.AdayKayitlar.IndexOf(altAday)
                        );
                        eslemeSozlugu[altAday.KayitYolu] = altEsleme;

                        var altCsvYolu = Path.Combine(ciktiKlasoru, $"{altTabloDosyaAdi}.csv");
                        CsvWriter altCsv;
                        bool altYeniOlustu = false;

                        if (!dosyaYoluWriterMap.TryGetValue(altCsvYolu, out altCsv!))
                        {
                            Console.WriteLine($"    -> Alt tablo için CSV oluşturuluyor: {Path.GetFileName(altCsvYolu)}");
                            var altWriter = new StreamWriter(altCsvYolu, false, new UTF8Encoding(true));
                            altCsv = YeniCsvWriter(
                                altWriter,
                                altEsleme.Cikti?.Ayirici,
                                altEsleme.Cikti?.TumAlanlariTirnakla ?? false
                            );

                            dosyaYoluWriterMap[altCsvYolu] = altCsv;
                            csvYazicilari[altAday.KayitYolu] = altCsv;
                            kaynaklar.Add(altCsv);
                            kaynaklar.Add(altWriter);
                            altYeniOlustu = true;
                        }
                        else
                        {
                            csvYazicilari[altAday.KayitYolu] = altCsv;
                        }

                        if (altYeniOlustu)
                        {
                            // fk kolonu: ana tablo adı + "_fk"
                            altCsv.WriteField($"{anaTabloDosyaAdi}_fk");
                            foreach (var col in altEsleme.Sutunlar)
                                altCsv.WriteField(col.Csv);
                            altCsv.NextRecord();
                        }
                    }
                }

                // ================================
                // 2) HER HİYERARŞİ İÇİN: XML'İ BAŞTAN SONA STREAM ET
                //    ReadToFollowing ile TÜM kayıtları yakala
                // ================================
                var ayarlar = new XmlReaderSettings
                {
                    IgnoreWhitespace = true,
                    IgnoreComments = true,
                    DtdProcessing = DtdProcessing.Ignore,
                    CloseInput = true
                };

                foreach (var hiyerarsi in hiyerarsiListesi)
                {
                    var anaAday = hiyerarsi.AnaTablo;

                    // KayitYolu: "/Root/PersonList/Person" gibi → son segment "Person"
                    var anaElemanAdi = SonSegmentAdi(anaAday.KayitYolu); // sadece LocalName

                    int kayitIndex = 0;

                    using var reader = EncodingAwareXml.CreateAutoXmlReader(inputXmlPath, ayarlar);

                    // Belgedeki tüm <Person> / <Company> / <Event> kayıtlarını sırayla dolaş
                    while (reader.ReadToFollowing(anaElemanAdi))
                    {
                        kayitIndex++;

                        // Bu elemanın alt ağacını tek seferlik XElement olarak oku
                        using var subTree = reader.ReadSubtree();
                        var recordEl = XElement.Load(subTree);

                        ProcessRecordForHierarchy(
                            hiyerarsi,
                            recordEl,
                            kayitIndex,
                            eslemeSozlugu,
                            csvYazicilari,
                            anaTabloYazilacakSutunlar
                        );
                    }
                }
            }
            finally
            {
                foreach (var k in kaynaklar)
                {
                    try { k?.Dispose(); } catch { }
                }

                kaynaklar.Clear();
                dosyaYoluWriterMap.Clear();
            }
        }

        /// <summary>
        /// Tek bir ana kayıt XElement'i için:
        ///  - Ana tablo satırını,
        ///  - Tüm alt tablo satırlarını yazar.
        /// </summary>
        private static void ProcessRecordForHierarchy(
            TabloHiyerarsisi hiyerarsi,
            XElement recordEl,
            int kayitIndex,
            Dictionary<string, EslemeYapilandirmasi> eslemeSozlugu,
            Dictionary<string, CsvWriter> csvYazicilari,
            Dictionary<string, List<KolonEsleme>> anaTabloYazilacakSutunlar)
        {
            var anaAday = hiyerarsi.AnaTablo;
            var anaCsv = csvYazicilari[anaAday.KayitYolu];
            var anaSutunlar = anaTabloYazilacakSutunlar[anaAday.KayitYolu];

            // --- ANA TABLO SATIRI ---
            foreach (var col in anaSutunlar)
            {
                var values = GetValuesFromElement(recordEl, col.Kaynak);
                string yazilacak;

                if (values.Count == 0)
                    yazilacak = "";
                else if (values.Count == 1 || string.IsNullOrEmpty(col.Birlestir))
                    yazilacak = values[0];
                else
                    yazilacak = string.Join(col.Birlestir, values);

                anaCsv.WriteField(yazilacak);
            }
            anaCsv.NextRecord();

            // --- ALT TABLOLAR SATIRLARI ---
            foreach (var altAday in hiyerarsi.AltTablolar)
            {
                var altEsleme = eslemeSozlugu[altAday.KayitYolu];
                var altCsv = csvYazicilari[altAday.KayitYolu];

                // Ana kayıt yolundan alt kayıt yoluna relative path: örn: ./Addresses/Address
                var altRelativePath = "." + altAday.KayitYolu.Substring(anaAday.KayitYolu.Length);
                var altElemanlar = FindElementsByRelativePath(recordEl, altRelativePath);

                foreach (var altEl in altElemanlar)
                {
                    // fk: ana kaydın sıra numarası (1,2,3,...)
                    altCsv.WriteField(kayitIndex.ToString(CultureInfo.InvariantCulture));

                    foreach (var col in altEsleme.Sutunlar)
                    {
                        var values = GetValuesFromElement(altEl, col.Kaynak);
                        string yazilacak;

                        if (values.Count == 0)
                            yazilacak = "";
                        else if (values.Count == 1 || string.IsNullOrEmpty(col.Birlestir))
                            yazilacak = values[0];
                        else
                            yazilacak = string.Join(col.Birlestir, values);

                        altCsv.WriteField(yazilacak);
                    }
                    altCsv.NextRecord();
                }
            }
        }

        // =========================
        // Yardımcı metotlar
        // =========================

        /// <summary>
        /// "/Root/PersonList/Person" → "Person"
        /// "/c:Root/c:PersonList/c:Person" → "Person"
        /// </summary>
        private static string SonSegmentAdi(string kayitYolu)
        {
            if (string.IsNullOrWhiteSpace(kayitYolu))
                return "";

            var parts = kayitYolu.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var last = parts[^1];

            var colonIndex = last.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < last.Length - 1)
                last = last[(colonIndex + 1)..];

            return last;
        }

        private static CsvWriter YeniCsvWriter(TextWriter writer, string? delimiter, bool quoteAll)
        {
            var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                NewLine = "\n",
                TrimOptions = TrimOptions.Trim,
                Delimiter = delimiter ?? ";"
            };
            if (quoteAll) { cfg.ShouldQuote = _ => true; }
            return new CsvWriter(writer, cfg);
        }

        private static List<XElement> FindElementsByRelativePath(XElement context, string relPath)
        {
            var sonuc = new List<XElement>();
            if (context == null || string.IsNullOrWhiteSpace(relPath)) return sonuc;
            if (!relPath.StartsWith("./")) return sonuc;

            var path = relPath.Substring(2);
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return sonuc;

            IEnumerable<XElement> current = new[] { context };
            foreach (var part in parts)
            {
                current = current.SelectMany(x => x.Elements()
                    .Where(e => e.Name.LocalName == part));
            }

            return current.ToList();
        }

        private static List<string> GetValuesFromElement(XElement element, string selectExpr)
        {
            var result = new List<string>();
            if (element == null || string.IsNullOrWhiteSpace(selectExpr))
                return result;

            selectExpr = selectExpr.Trim();

            // 1) Sadece text seçimleri
            if (selectExpr == "./text()" || selectExpr == "text()" || selectExpr == ".")
            {
                var v = (element.Value ?? "").Trim();
                if (!string.IsNullOrEmpty(v)) result.Add(v);
                return result;
            }

            // 2) Sadece attribute seçimleri: ./@attr veya @attr
            if (selectExpr.StartsWith("./@", StringComparison.Ordinal) ||
                selectExpr.StartsWith("@", StringComparison.Ordinal))
            {
                string attrName = selectExpr.StartsWith("./@")
                    ? selectExpr.Substring(3)
                    : selectExpr.Substring(1);

                var attr = element.Attribute(attrName);
                if (attr != null)
                {
                    var v = (attr.Value ?? "").Trim();
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
                return result;
            }

            // 3) Çocuk element altında attribute: ./Child/@attr vb.
            if (selectExpr.Contains("/@"))
            {
                // Örn: ./Child/@type  ->  Child, attr=type
                var withoutDot = selectExpr.StartsWith("./")
                    ? selectExpr.Substring(2)
                    : selectExpr;

                var parts = withoutDot.Split('/', StringSplitOptions.RemoveEmptyEntries);
                // Son parça @attr, ondan öncesi element path
                var attrPart = parts[^1];
                if (attrPart.StartsWith("@"))
                {
                    string attrName = attrPart.Substring(1);
                    var elementPath = string.Join('/', parts[..^1]);

                    // önce child elementleri bul
                    IEnumerable<XElement> current = new[] { element };
                    var elParts = elementPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in elParts)
                    {
                        current = current.SelectMany(x => x.Elements()
                            .Where(e => e.Name.LocalName == p));
                    }

                    foreach (var el in current)
                    {
                        var attr = el.Attribute(attrName);
                        if (attr != null)
                        {
                            var v = (attr.Value ?? "").Trim();
                            if (!string.IsNullOrEmpty(v)) result.Add(v);
                        }
                    }

                    return result;
                }
            }

            // 4) Kalan her şey: senin mevcut child-element text mantığın
            // ./Child/SubChild vb.
            string path = selectExpr.StartsWith("./") ? selectExpr.Substring(2) : selectExpr;
            var parts2 = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts2.Length == 0) return result;

            IEnumerable<XElement> current2 = new[] { element };
            foreach (var part in parts2)
            {
                current2 = current2.SelectMany(x => x.Elements()
                    .Where(e => e.Name.LocalName == part));
            }

            foreach (var el in current2)
            {
                var v = el.Value?.Trim();
                if (!string.IsNullOrEmpty(v)) result.Add(v);
            }

            return result;
        }

    }
}

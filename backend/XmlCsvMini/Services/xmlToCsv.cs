// XmlToCsvExporter.cs — ALT TABLOLARDA FK YOK, GLOBAL ID YOK.

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
        public static void CalistirHiyerarsik(
            string inputXmlPath,
            List<TabloHiyerarsisi> hiyerarsiListesi,
            string ciktiKlasoru,
            KesifRaporu rapor)
        {
            var eslemeSozlugu = new Dictionary<string, EslemeYapilandirmasi>();
            var csvYazicilari = new Dictionary<string, CsvWriter>();
            var anaTabloYazilacakSutunlar = new Dictionary<string, List<KolonEsleme>>();

            var dosyaYoluWriterMap = new Dictionary<string, CsvWriter>(StringComparer.OrdinalIgnoreCase);
            var kaynaklar = new List<IDisposable>();

            try
            {
                Directory.CreateDirectory(ciktiKlasoru);

                //====================================
                //  CSV DOSYALARINI OLUŞTUR
                //====================================
                foreach (var hiyerarsi in hiyerarsiListesi)
                {
                    var anaAday = hiyerarsi.AnaTablo;

                    string anaTabloDosyaAdi = TableNameBuilder.BuildTableName(anaAday.KayitYolu, anaAday.KayitYolu);

                    var anaEsleme = EslemeBaslatici.DuzEslemeOlustur(
                        rapor,
                        rapor.AdayKayitlar.IndexOf(anaAday)
                    );

                    eslemeSozlugu[anaAday.KayitYolu] = anaEsleme;

                    var anaCsvYolu = Path.Combine(ciktiKlasoru, $"{anaTabloDosyaAdi}.csv");

                    CsvWriter anaCsv;
                    bool anaYeni = false;

                    if (!dosyaYoluWriterMap.TryGetValue(anaCsvYolu, out anaCsv!))
                    {
                        var writer = new StreamWriter(anaCsvYolu, false, new UTF8Encoding(true));
                        anaCsv = YeniCsvWriter(writer, anaEsleme.Cikti?.Ayirici, anaEsleme.Cikti?.TumAlanlariTirnakla ?? false);

                        dosyaYoluWriterMap[anaCsvYolu] = anaCsv;
                        csvYazicilari[anaAday.KayitYolu] = anaCsv;

                        kaynaklar.Add(anaCsv);
                        kaynaklar.Add(writer);

                        anaYeni = true;
                    }
                    else
                    {
                        csvYazicilari[anaAday.KayitYolu] = anaCsv;
                    }

                    // Ana tablodan alt tablo kaynaklarını ayır
                    var altPathSet = new HashSet<string>(
                        hiyerarsi.AltTablolar.Select(alt => "." + alt.KayitYolu.Substring(anaAday.KayitYolu.Length))
                    );

                    var gecerliSutunlar = anaEsleme.Sutunlar
                        .Where(col => !altPathSet.Contains(col.Kaynak))
                        .ToList();

                    anaTabloYazilacakSutunlar[anaAday.KayitYolu] = gecerliSutunlar;

                    if (anaYeni)
                    {
                        foreach (var col in gecerliSutunlar)
                            anaCsv.WriteField(col.Csv);

                        anaCsv.NextRecord();
                    }

                    // ALT TABLOLARI OLUŞTUR
                    foreach (var altAday in hiyerarsi.AltTablolar)
                    {
                        string altDosyaAdi = TableNameBuilder.BuildTableName(anaAday.KayitYolu, altAday.KayitYolu);

                        var altEsleme = EslemeBaslatici.DuzEslemeOlustur(
                            rapor,
                            rapor.AdayKayitlar.IndexOf(altAday)
                        );

                        eslemeSozlugu[altAday.KayitYolu] = altEsleme;

                        var altCsvYolu = Path.Combine(ciktiKlasoru, $"{altDosyaAdi}.csv");

                        CsvWriter altCsv;
                        bool altYeni = false;

                        if (!dosyaYoluWriterMap.TryGetValue(altCsvYolu, out altCsv!))
                        {
                            var writer = new StreamWriter(altCsvYolu, false, new UTF8Encoding(true));
                            altCsv = YeniCsvWriter(writer, altEsleme.Cikti?.Ayirici, altEsleme.Cikti?.TumAlanlariTirnakla ?? false);

                            dosyaYoluWriterMap[altCsvYolu] = altCsv;
                            csvYazicilari[altAday.KayitYolu] = altCsv;

                            kaynaklar.Add(altCsv);
                            kaynaklar.Add(writer);

                            altYeni = true;
                        }
                        else
                        {
                            csvYazicilari[altAday.KayitYolu] = altCsv;
                        }

                        if (altYeni)
                        {
                            // ❗ ALT TABLO HEADER HİÇ FK İÇERMEZ
                            foreach (var col in altEsleme.Sutunlar)
                                altCsv.WriteField(col.Csv);

                            altCsv.NextRecord();
                        }
                    }
                }

                //====================================
                //  XML STREAM → CSV YAZ
                //====================================
                var settings = new XmlReaderSettings
                {
                    IgnoreWhitespace = true,
                    IgnoreComments = true,
                    DtdProcessing = DtdProcessing.Ignore,
                    CloseInput = true
                };

                foreach (var hiyerarsi in hiyerarsiListesi)
                {
                    var anaAday = hiyerarsi.AnaTablo;

                    var anaElemanAdi = SonSegmentAdi(anaAday.KayitYolu);

                    int kayitIndex = 0;

                    using var reader = EncodingAwareXml.CreateAutoXmlReader(inputXmlPath, settings);

                    while (reader.ReadToFollowing(anaElemanAdi))
                    {
                        kayitIndex++;

                        using var subTree = reader.ReadSubtree();
                        var recordEl = XElement.Load(subTree);

                        ProcessRecordForHierarchy(
                            hiyerarsi,
                            recordEl,
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

                dosyaYoluWriterMap.Clear();
            }
        }

        private static void ProcessRecordForHierarchy(
            TabloHiyerarsisi hiyerarsi,
            XElement recordEl,
            Dictionary<string, EslemeYapilandirmasi> eslemeSozlugu,
            Dictionary<string, CsvWriter> csvYazicilari,
            Dictionary<string, List<KolonEsleme>> anaTabloYazilacakSutunlar)
        {
            var anaAday = hiyerarsi.AnaTablo;
            var anaCsv = csvYazicilari[anaAday.KayitYolu];
            var anaSutunlar = anaTabloYazilacakSutunlar[anaAday.KayitYolu];
            var anaEsleme = eslemeSozlugu[anaAday.KayitYolu];

            // ANA TABLO SATIRI
            foreach (var col in anaSutunlar)
            {
                var vals = GetValuesFromElement(recordEl, col.Kaynak);
                string v = vals.Count switch
                {
                    0 => "",
                    1 => vals[0],
                    _ => string.Join(col.Birlestir, vals)
                };

                anaCsv.WriteField(v);
            }
            anaCsv.NextRecord();

            // ALT TABLOLAR
            foreach (var altAday in hiyerarsi.AltTablolar)
            {
                var altCsv = csvYazicilari[altAday.KayitYolu];
                var altEsleme = eslemeSozlugu[altAday.KayitYolu];

                var relPath = "." + altAday.KayitYolu.Substring(anaAday.KayitYolu.Length);
                var altElemanlar = FindElementsByRelativePath(recordEl, relPath);

                foreach (var altEl in altElemanlar)
                {
                    // ❗ FK YOK – yalnızca kolonları yaz
                    foreach (var col in altEsleme.Sutunlar)
                    {
                        var vals = GetValuesFromElement(altEl, col.Kaynak);
                        string v = vals.Count switch
                        {
                            0 => "",
                            1 => vals[0],
                            _ => string.Join(col.Birlestir, vals)
                        };

                        altCsv.WriteField(v);
                    }

                    altCsv.NextRecord();
                }
            }
        }

        // Yardımcı fonksiyonlar (SonSegmentAdi, YeniCsvWriter, etc.) aynı
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

        private static CsvWriter YeniCsvWriter(TextWriter writer, string? delimiter, bool quoteAll)
        {
            var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                NewLine = "\n",
                TrimOptions = TrimOptions.Trim,
                Delimiter = delimiter ?? ";"
            };
            if (quoteAll) cfg.ShouldQuote = _ => true;
            return new CsvWriter(writer, cfg);
        }

        private static List<XElement> FindElementsByRelativePath(XElement context, string relPath)
        {
            var list = new List<XElement>();
            if (context == null || string.IsNullOrWhiteSpace(relPath)) return list;
            if (!relPath.StartsWith("./")) return list;

            var path = relPath.Substring(2);
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            IEnumerable<XElement> current = new[] { context };
            foreach (var part in parts)
            {
                current = current.SelectMany(x => x.Elements()
                    .Where(e => e.Name.LocalName == part));
            }
            return current.ToList();
        }

        private static List<string> GetValuesFromElement(XElement el, string expr)
        {
            var result = new List<string>();
            if (el == null || string.IsNullOrWhiteSpace(expr)) return result;

            expr = expr.Trim();

            if (expr == "text()" || expr == "./text()" || expr == ".")
            {
                var v = el.Value?.Trim();
                if (!string.IsNullOrEmpty(v)) result.Add(v);
                return result;
            }

            if (expr.StartsWith("@") || expr.StartsWith("./@"))
            {
                var attr = expr.StartsWith("./@")
                    ? expr.Substring(3)
                    : expr.Substring(1);

                var a = el.Attribute(attr);
                if (a != null)
                {
                    var v = a.Value?.Trim();
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
                return result;
            }

            string path = expr.StartsWith("./") ? expr.Substring(2) : expr;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            IEnumerable<XElement> current = new[] { el };
            foreach (var part in parts)
            {
                current = current.SelectMany(x =>
                    x.Elements().Where(e => e.Name.LocalName == part));
            }

            foreach (var x in current)
            {
                var v = x.Value?.Trim();
                if (!string.IsNullOrEmpty(v)) result.Add(v);
            }

            return result;
        }
    }
}

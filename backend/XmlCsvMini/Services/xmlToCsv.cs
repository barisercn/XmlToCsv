// Services/XmlToCsvExporter.cs - "Eksik Veri" ve "Double Dispose" Hataları Giderilmiş Nihai Sürüm

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using CsvHelper;
using CsvHelper.Configuration;
using XmlCsvMini.Altyapi;
using XmlCsvMini.Models;


namespace XmlCsvMini.Services
{

    public static class XmlToCsvExporter
    {
        public static void CalistirHiyerarsik(
             string inputXmlPath, List<TabloHiyerarsisi> hiyerarsiListesi, string ciktiKlasoru, KesifRaporu rapor)
        {
            var eslemeSozlugu = new Dictionary<string, EslemeYapilandirmasi>();
            var csvYazicilari = new Dictionary<string, CsvWriter>();
            var kaynaklar = new List<IDisposable>();
            var hiyerarsiAramaTablosu = new Dictionary<string, TabloHiyerarsisi>();

            // DÜZELTME: Her ana tablo için hangi sütunların yazılacağını önceden hesaplamak için yeni bir sözlük.
            var anaTabloYazilacakSutunlar = new Dictionary<string, List<KolonEsleme>>();

            try
            {
                // --- 1. Hazırlık: CSV başlıklarını akıllıca oluştur ---
                foreach (var hiyerarsi in hiyerarsiListesi)
                {
                    var anaAday = hiyerarsi.AnaTablo;
                    string anaTabloDosyaAdi = XmlToCsvExporter.YoldanDosyaAdiUret(anaAday.KayitYolu);
                    var anaEsleme = EslemeBaslatici.DuzEslemeOlustur(rapor, rapor.AdayKayitlar.IndexOf(anaAday));
                    eslemeSozlugu[anaAday.KayitYolu] = anaEsleme;

                    string anaKayitAdi = anaAday.KayitYolu.Split('/').Last();
                    if (!hiyerarsiAramaTablosu.ContainsKey(anaKayitAdi)) hiyerarsiAramaTablosu[anaKayitAdi] = hiyerarsi;

                    var anaCsvYolu = Path.Combine(ciktiKlasoru, $"{anaTabloDosyaAdi}.csv");
                    Console.WriteLine($"  -> Ana tablo için CSV oluşturuluyor: {Path.GetFileName(anaCsvYolu)}");
                    var anaWriter = new StreamWriter(anaCsvYolu, false, new UTF8Encoding(true));
                    var anaCsv = YeniCsvWriter(anaWriter, anaEsleme.Cikti?.Ayirici, anaEsleme.Cikti?.TumAlanlariTirnakla ?? false);
                    kaynaklar.Add(anaCsv);
                    csvYazicilari[anaAday.KayitYolu] = anaCsv;

                    // DÜZELTME: Ana tabloya yazılacak sütunları akıllıca filtrele.
                    // Alt tablolara dönüşecek karmaşık alanları ana tablodan çıkar.
                    var altTabloRelativePaths = new HashSet<string>(hiyerarsi.AltTablolar.Select(alt => "." + alt.KayitYolu.Substring(anaAday.KayitYolu.Length)));
                    var gecerliAnaSutunlar = anaEsleme.Sutunlar.Where(col => !altTabloRelativePaths.Contains(col.Kaynak)).ToList();
                    anaTabloYazilacakSutunlar[anaAday.KayitYolu] = gecerliAnaSutunlar;

                    foreach (var col in gecerliAnaSutunlar) anaCsv.WriteField(col.Csv);
                    anaCsv.NextRecord();

                    foreach (var altAday in hiyerarsi.AltTablolar)
                    {
                        string altTabloDosyaAdi = XmlToCsvExporter.YoldanDosyaAdiUret(altAday.KayitYolu);
                        var altEsleme = EslemeBaslatici.DuzEslemeOlustur(rapor, rapor.AdayKayitlar.IndexOf(altAday));
                        eslemeSozlugu[altAday.KayitYolu] = altEsleme;

                        var altCsvYolu = Path.Combine(ciktiKlasoru, $"{altTabloDosyaAdi}.csv");
                        Console.WriteLine($"    -> Alt tablo için CSV oluşturuluyor: {Path.GetFileName(altCsvYolu)}");
                        var altWriter = new StreamWriter(altCsvYolu, false, new UTF8Encoding(true));
                        var altCsv = YeniCsvWriter(altWriter, altEsleme.Cikti?.Ayirici, altEsleme.Cikti?.TumAlanlariTirnakla ?? false);
                        kaynaklar.Add(altCsv);
                        csvYazicilari[altAday.KayitYolu] = altCsv;

                        altCsv.WriteField($"{anaTabloDosyaAdi}_fk");
                        foreach (var col in altEsleme.Sutunlar) altCsv.WriteField(col.Csv);
                        altCsv.NextRecord();
                    }
                }

                // --- 2. Ana İşlem: XML'i Tara, Anahtar'ı Yakala ve Miras Bırak ---
                var nsMgr = new XmlNamespaceManager(new NameTable());
                if (rapor.AdUzaylari != null)
                {
                    foreach (var ns in rapor.AdUzaylari)
                    {
                        nsMgr.AddNamespace(ns.Key, ns.Value);
                    }
                }
                var xrSettings = new XmlReaderSettings
                {
                    ConformanceLevel = ConformanceLevel.Fragment,
                    IgnoreWhitespace = true,
                    IgnoreComments = true,
                    DtdProcessing = DtdProcessing.Ignore,
                    CloseInput = true    // ✨ kritik: alttaki stream'i de kapat!
                };
                using (var okuyucu = EncodingAwareXml.CreateAutoXmlReader(inputXmlPath, xrSettings))
                {
                    while (okuyucu.Read())
                    {
                        if (okuyucu.NodeType != XmlNodeType.Element) continue;

                        if (hiyerarsiAramaTablosu.TryGetValue(okuyucu.LocalName, out var bulunanHiyerarsi))
                        {
                            using var altOkuyucu = okuyucu.ReadSubtree();
                            XElement anaElement = XElement.Load(altOkuyucu, LoadOptions.None);

                            var anaAday = bulunanHiyerarsi.AnaTablo;
                            var anaCsv = csvYazicilari[anaAday.KayitYolu];

                            // DÜZELTME: Yazılacak sütunların filtrelenmiş listesini al.
                            var gecerliAnaSutunlar = anaTabloYazilacakSutunlar[anaAday.KayitYolu];

                            string anaKayitBusinessKey = anaElement.Attribute("id")?.Value ??
                                                         anaElement.Attribute("ID")?.Value ??
                                                         string.Empty;

                            // DÜZELTME: Sadece filtrelenmiş "düz" sütunları ana tabloya yaz.
                            foreach (var col in gecerliAnaSutunlar)
                            {
                                var values = SelectValues(anaElement, col.Kaynak, nsMgr);
                                anaCsv.WriteField(values.FirstOrDefault() ?? col.Varsayilan ?? "");
                            }
                            anaCsv.NextRecord();

                            // Bu ana kaydın alt tablolarını işle (Bu kısım doğru çalışıyor)
                            foreach (var altAday in bulunanHiyerarsi.AltTablolar)
                            {
                                var altEsleme = eslemeSozlugu[altAday.KayitYolu];
                                var altCsv = csvYazicilari[altAday.KayitYolu];

                                string goreliYol = "." + altAday.KayitYolu.Substring(anaAday.KayitYolu.Length);
                                var altDugumler = anaElement.XPathSelectElements(goreliYol, nsMgr);

                                foreach (var altDugum in altDugumler)
                                {
                                    altCsv.WriteField(anaKayitBusinessKey);

                                    foreach (var col in altEsleme.Sutunlar)
                                    {
                                        var values = SelectValues(altDugum, col.Kaynak, nsMgr);
                                        string cell = string.Join(col.Birlestir ?? altEsleme.Diziler?.Birlestirici ?? " | ", values);
                                        altCsv.WriteField(cell);
                                    }
                                    altCsv.NextRecord();
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                // DÜZELTME: CsvWriter'ları dispose etmek StreamWriter'ı da dispose eder.
                // Bu yüzden sadece CsvWriter'ları içeren listeyi dispose etmeliyiz.
                // Bu, "double dispose" hatasını önler.
                foreach (var csvWriter in csvYazicilari.Values)
                {
                    csvWriter.Dispose();
                }
            }
        }

        // --- YARDIMCI METOTLAR (DEĞİŞİKLİK YOK) ---
        private static CsvWriter YeniCsvWriter(TextWriter writer, string? delimiter, bool quoteAll)
        {
            var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                NewLine = "\n",
                TrimOptions = TrimOptions.Trim,
                Delimiter = delimiter ?? ","
            };
            if (quoteAll) { cfg.ShouldQuote = _ => true; }
            return new CsvWriter(writer, cfg);
        }

        private static List<string> SelectValues(XElement element, string selectExpr, IXmlNamespaceResolver nsMgr)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(selectExpr)) return results;
            try
            {
                var nodes = element.XPathEvaluate(selectExpr, nsMgr) as IEnumerable<object>;
                if (nodes == null) return results;

                foreach (var n in nodes)
                {
                    switch (n)
                    {
                        case XAttribute a: Ekle(a.Value); break;
                        case XElement e: Ekle(e.Value); break;
                        case XText t: Ekle(t.Value); break;
                        default: Ekle(n.ToString()); break;
                    }
                }
            }
            catch (Exception) { /* XPath hatası durumunda boş liste döner, program çökmez */ }
            return results;

            void Ekle(string? s)
            {
                if (!string.IsNullOrWhiteSpace(s)) results.Add(s.Trim());
            }
        }
        public static string YoldanDosyaAdiUret(string yol)
        {
            if (string.IsNullOrEmpty(yol)) return "bilinmeyen_kayit";

            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Genellikle sondan 1 veya 2 parça en anlamlı olanlardır

            var anlamliParcalar = parcalar.Skip(Math.Max(0, parcalar.Length - 2)).Take(2);

            return string.Join('_', anlamliParcalar).Replace(":", "_").ToLowerInvariant();
        }


    }
}

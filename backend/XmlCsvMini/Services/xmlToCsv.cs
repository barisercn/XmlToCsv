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
        string inputXmlPath,
        List<TabloHiyerarsisi> hiyerarsiListesi,
        string ciktiKlasoru,
        KesifRaporu rapor)
        {
            var eslemeSozlugu = new Dictionary<string, EslemeYapilandirmasi>();

            // Aynı dosyayı iki kere açmamak için
            var dosyaYoluWriterMap = new Dictionary<string, CsvWriter>();

            var csvYazicilari = new Dictionary<string, CsvWriter>();
            var kaynaklar = new List<IDisposable>();
            var hiyerarsiAramaTablosu = new Dictionary<string, TabloHiyerarsisi>();
            var anaTabloYazilacakSutunlar = new Dictionary<string, List<KolonEsleme>>();

            try
            {
                // Çıktı klasörü yoksa oluştur
                Directory.CreateDirectory(ciktiKlasoru);

                // XML’i bir kez yükle
                var xdoc = XDocument.Load(inputXmlPath);
                var root = xdoc.Root ?? throw new InvalidOperationException("XML kök elemanı bulunamadı.");

                // ================================
                // 1. AŞAMA: CSV dosyalarını ve HEADER’ları hazırla
                // ================================
                foreach (var hiyerarsi in hiyerarsiListesi)
                {
                    var anaAday = hiyerarsi.AnaTablo;
                    string anaTabloDosyaAdi = TableNameBuilder.BuildTableName(
     anaAday.KayitYolu,   // rootPath
     anaAday.KayitYolu    // recordPath (root ile aynı)
 );
                    var anaEsleme = EslemeBaslatici.DuzEslemeOlustur(rapor, rapor.AdayKayitlar.IndexOf(anaAday));
                    eslemeSozlugu[anaAday.KayitYolu] = anaEsleme;

                    string anaKayitAdi = anaAday.KayitYolu.Split('/').Last();
                    if (!hiyerarsiAramaTablosu.ContainsKey(anaKayitAdi))
                        hiyerarsiAramaTablosu[anaKayitAdi] = hiyerarsi;

                    var anaCsvYolu = Path.Combine(ciktiKlasoru, $"{anaTabloDosyaAdi}.csv");
                    CsvWriter anaCsv;
                    bool anaYeniOlustu = false;

                    // Eğer bu dosya yolu için zaten açık bir yazıcı varsa onu kullan, yoksa yeni oluştur.
                    if (dosyaYoluWriterMap.ContainsKey(anaCsvYolu))
                    {
                        anaCsv = dosyaYoluWriterMap[anaCsvYolu];
                    }
                    else
                    {
                        Console.WriteLine($"  -> Ana tablo için CSV oluşturuluyor: {Path.GetFileName(anaCsvYolu)}");
                        var anaWriter = new StreamWriter(anaCsvYolu, false, new UTF8Encoding(true));
                        anaCsv = YeniCsvWriter(anaWriter, anaEsleme.Cikti?.Ayirici, anaEsleme.Cikti?.TumAlanlariTirnakla ?? false);

                        kaynaklar.Add(anaCsv);
                        dosyaYoluWriterMap[anaCsvYolu] = anaCsv;
                        anaYeniOlustu = true;
                    }

                    csvYazicilari[anaAday.KayitYolu] = anaCsv;

                    // Alt tabloların yollarını ana kayda göre relative path olarak çıkar
                    var altTabloRelativePaths = new HashSet<string>(
                        hiyerarsi.AltTablolar.Select(alt => "." + alt.KayitYolu.Substring(anaAday.KayitYolu.Length)));

                    // Ana tabloda yazılacak sütunlar: alt tablolara ait olanlar hariç
                    var gecerliAnaSutunlar = anaEsleme.Sutunlar
                        .Where(col => !altTabloRelativePaths.Contains(col.Kaynak))
                        .ToList();

                    anaTabloYazilacakSutunlar[anaAday.KayitYolu] = gecerliAnaSutunlar;

                    // HEADER sadece dosya yeni oluşturulduğunda yazılsın
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
      anaAday.KayitYolu,     // rootPath: Person / Company / Event
      altAday.KayitYolu      // recordPath: Addresses/Address, Geo/Lat, Employees/EmployeeRef, ...
  );
                        var altEsleme = EslemeBaslatici.DuzEslemeOlustur(rapor, rapor.AdayKayitlar.IndexOf(altAday));
                        eslemeSozlugu[altAday.KayitYolu] = altEsleme;

                        var altCsvYolu = Path.Combine(ciktiKlasoru, $"{altTabloDosyaAdi}.csv");
                        CsvWriter altCsv;
                        bool altYeniOlustu = false;

                        if (dosyaYoluWriterMap.ContainsKey(altCsvYolu))
                        {
                            altCsv = dosyaYoluWriterMap[altCsvYolu];
                        }
                        else
                        {
                            Console.WriteLine($"    -> Alt tablo için CSV oluşturuluyor: {Path.GetFileName(altCsvYolu)}");
                            var altWriter = new StreamWriter(altCsvYolu, false, new UTF8Encoding(true));
                            altCsv = YeniCsvWriter(altWriter, altEsleme.Cikti?.Ayirici, altEsleme.Cikti?.TumAlanlariTirnakla ?? false);

                            kaynaklar.Add(altCsv);
                            dosyaYoluWriterMap[altCsvYolu] = altCsv;
                            altYeniOlustu = true;
                        }

                        csvYazicilari[altAday.KayitYolu] = altCsv;

                        // HEADER sadece dosya yeni oluşturulduysa yaz:
                        if (altYeniOlustu)
                        {
                            // fk kolon adı: ilk oluşturan ana tabloya göre adlandırılır
                            altCsv.WriteField($"{anaTabloDosyaAdi}_fk");
                            foreach (var col in altEsleme.Sutunlar)
                                altCsv.WriteField(col.Csv);
                            altCsv.NextRecord();
                        }
                    }
                }

                // ================================
                // 2. AŞAMA: XML'den verileri okuyup satır yaz
                // ================================
                foreach (var hiyerarsi in hiyerarsiListesi)
                {
                    var anaAday = hiyerarsi.AnaTablo;
                    var anaEsleme = eslemeSozlugu[anaAday.KayitYolu];
                    var anaCsv = csvYazicilari[anaAday.KayitYolu];
                    var anaSutunlar = anaTabloYazilacakSutunlar[anaAday.KayitYolu];

                    // Bu ana kayıt yolu için XML'de hangi elemanlar var? (/Root/Person gibi)
                    var anaKayitElemanlari = FindElementsByAbsolutePath(root, anaAday.KayitYolu);

                    int kayitIndex = 1; // Alt tablolara fk vermek için basit bir sıra numarası

                    foreach (var recordEl in anaKayitElemanlari)
                    {
                        // --- ANA TABLO SATIRI ---
                        foreach (var col in anaSutunlar)
                        {
                            var values = GetValuesFromElement(recordEl, col.Kaynak);
                            string yazilacak;

                            if (values.Count == 0)
                            {
                                yazilacak = "";
                            }
                            else if (values.Count == 1 || string.IsNullOrEmpty(col.Birlestir))
                            {
                                yazilacak = values[0];
                            }
                            else
                            {
                                yazilacak = string.Join(col.Birlestir, values);
                            }

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
                                // fk kolonu: şimdilik ana tablodaki sıra numarasını yazıyoruz (1,2,3,...)
                                altCsv.WriteField(kayitIndex.ToString(CultureInfo.InvariantCulture));

                                foreach (var col in altEsleme.Sutunlar)
                                {
                                    var values = GetValuesFromElement(altEl, col.Kaynak);
                                    string yazilacak;

                                    if (values.Count == 0)
                                    {
                                        yazilacak = "";
                                    }
                                    else if (values.Count == 1 || string.IsNullOrEmpty(col.Birlestir))
                                    {
                                        yazilacak = values[0];
                                    }
                                    else
                                    {
                                        yazilacak = string.Join(col.Birlestir, values);
                                    }

                                    altCsv.WriteField(yazilacak);
                                }
                                altCsv.NextRecord();
                            }
                        }

                        kayitIndex++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Hata oluştu: " + ex.Message);
                throw; // Hatayı yukarı fırlat ki controller yakalasın
            }
            finally
            {
                // --- DOSYALARI KAPAT ---
                foreach (var kaynak in kaynaklar)
                {
                    if (kaynak != null)
                        kaynak.Dispose();
                }
                kaynaklar.Clear();
                dosyaYoluWriterMap.Clear();
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
            if (string.IsNullOrEmpty(yol))
                return "bilinmeyen_kayit";

            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parcalar.Length == 0)
                return "bilinmeyen_kayit";

            // Amaç:
            // /Root/PersonList/Person/Addresses/Address/City
            //          ↓      ↓       ↓        ↓        ↓
            // parcalar: [Root, PersonList, Person, Addresses, Address, City]
            // son 4:    [Person, Addresses, Address, City]
            // -> person_addresses_address_city

            int take = parcalar.Length >= 4 ? 4 : parcalar.Length;

            var anlamliParcalar = parcalar
                .Skip(parcalar.Length - take)
                .Take(take);

            return string.Join('_', anlamliParcalar)
                .Replace(":", "_")
                .ToLowerInvariant();
        }

        /// <summary>
        /// Örn: "/Root/Person" yoluna göre XML'de ilgili elemanları bulur.
        /// LocalName bazlı gider; namespace sorun çıkarmaz.
        /// </summary>
        private static List<XElement> FindElementsByAbsolutePath(XElement root, string kayitYolu)
        {
            var sonuc = new List<XElement>();
            if (root == null || string.IsNullOrWhiteSpace(kayitYolu)) return sonuc;

            var parcalar = kayitYolu.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parcalar.Length == 0) return sonuc;

            IEnumerable<XElement> current = new[] { root };

            // İlk parça genelde kök ("Root"), root zaten orada, onu atlayabiliriz
            foreach (var part in parcalar.Skip(1))
            {
                current = current.SelectMany(x => x.Elements()
                    .Where(e => e.Name.LocalName == part));
            }

            return current.ToList();
        }

        /// <summary>
        /// Örn: "./Addresses/Address" gibi bir relative path'i,
        /// verilen context (örneğin Person elementi) üzerinden çözer.
        /// </summary>
        private static List<XElement> FindElementsByRelativePath(XElement context, string relPath)
        {
            var sonuc = new List<XElement>();
            if (context == null || string.IsNullOrWhiteSpace(relPath)) return sonuc;
            if (!relPath.StartsWith("./")) return sonuc;

            var path = relPath.Substring(2); // "./" at
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

        /// <summary>
        /// AlanBilgisi.Yol veya KolonEsleme.Kaynak gibi bir ifadeyi (./@id, ./FirstName, ./Birth/Date, ./text())
        /// verili XElement üzerinden değerlendirir ve değer listesini döner.
        /// </summary>
        private static List<string> GetValuesFromElement(XElement element, string selectExpr)
        {
            var result = new List<string>();
            if (element == null || string.IsNullOrWhiteSpace(selectExpr))
                return result;

            selectExpr = selectExpr.Trim();

            // ./text()
            if (selectExpr == "./text()" || selectExpr == "text()" || selectExpr == ".")
            {
                var v = (element.Value ?? "").Trim();
                if (!string.IsNullOrEmpty(v)) result.Add(v);
                return result;
            }

            // ./@attr veya @attr
            if (selectExpr.StartsWith("./@"))
            {
                var attrName = selectExpr.Substring(3);
                var attr = element.Attribute(attrName);
                if (attr != null)
                {
                    var v = attr.Value?.Trim();
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
                return result;
            }

            if (selectExpr.StartsWith("@"))
            {
                var attrName = selectExpr.Substring(1);
                var attr = element.Attribute(attrName);
                if (attr != null)
                {
                    var v = attr.Value?.Trim();
                    if (!string.IsNullOrEmpty(v)) result.Add(v);
                }
                return result;
            }

            // Kalan her şeyi relative element path olarak yorumla: ./FirstName, ./Birth/Date vb.
            string path = selectExpr.StartsWith("./") ? selectExpr.Substring(2) : selectExpr;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return result;

            IEnumerable<XElement> current = new[] { element };
            foreach (var part in parts)
            {
                current = current.SelectMany(x => x.Elements()
                    .Where(e => e.Name.LocalName == part));
            }

            foreach (var el in current)
            {
                var v = el.Value?.Trim();
                if (!string.IsNullOrEmpty(v)) result.Add(v);
            }

            return result;
        }

    }
}

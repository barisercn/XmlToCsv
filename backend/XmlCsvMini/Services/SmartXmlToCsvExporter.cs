// SmartXmlToCsvExporter.cs - Akıllı XML'den CSV'ye dönüştürücü v2
// Tüm XML'i yükler, yapıyı analiz eder, düzleştirir

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using XmlCsvMini.Altyapi;

namespace XmlCsvMini.Services
{
    public static class SmartXmlToCsvExporter
    {
        /// <summary>
        /// XML dosyasını akıllı şekilde CSV'lere dönüştürür.
        /// </summary>
        public static void Calistir(string xmlYolu, string ciktiKlasoru)
        {
            Directory.CreateDirectory(ciktiKlasoru);

            Console.WriteLine($"XML yükleniyor: {xmlYolu}");

            // XML'i yükle
            XDocument doc;
            var settings = new System.Xml.XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true,
                DtdProcessing = System.Xml.DtdProcessing.Ignore,
                CloseInput = true  // Dosyayı düzgün kapat
            };
            using (var reader = EncodingAwareXml.CreateAutoXmlReader(xmlYolu, settings))
            {
                doc = XDocument.Load(reader);
            }

            // GC'yi zorla çalıştır - dosya kilidini serbest bırak
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (doc.Root == null)
            {
                Console.WriteLine("HATA: XML root element bulunamadı!");
                return;
            }

            Console.WriteLine($"XML yüklendi. Root: {doc.Root.Name.LocalName}");

            // 1. ADIM: Tekrarlayan elemanları bul
            var tekrarlayanlar = TekrarlayanElemanlariTespit(doc.Root);

            Console.WriteLine($"\nTekrarlayan elemanlar ({tekrarlayanlar.Count}):");
            foreach (var t in tekrarlayanlar.Take(20))
            {
                Console.WriteLine($"  - {t}");
            }

            // 2. ADIM: Her tekrarlayan eleman için CSV oluştur
            var islenmisTablolar = new HashSet<string>();
            var olusturulanTabloAdlari = new HashSet<string>();

            foreach (var yol in tekrarlayanlar.OrderBy(y => y.Length))
            {
                // Tabloyu işle
                var tabloAdi = TabloAdiOlustur(yol);

                // Aynı isimde tablo varsa, tam yoldan benzersiz isim oluştur
                if (olusturulanTabloAdlari.Contains(tabloAdi))
                {
                    tabloAdi = string.Join("_", yol.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Skip(1).Select(SnakeCase));
                }
                var csvYolu = Path.Combine(ciktiKlasoru, $"{tabloAdi}.csv");

                Console.WriteLine($"\nTablo oluşturuluyor: {tabloAdi}");

                // Bu yoldaki elemanları bul
                var elemanlar = BulElemanlar(doc.Root, yol);

                if (elemanlar.Count == 0)
                {
                    Console.WriteLine("  UYARI: Hiç eleman bulunamadı!");
                    continue;
                }

                Console.WriteLine($"  {elemanlar.Count} kayıt bulundu");

                // Sütunları belirle (ilk elemandan)
                var sutunlar = SutunlariBelirle(elemanlar.First(), tekrarlayanlar, yol);

                // Entity ID sütunu ekle (Entity veya SpecialEntity altındaysa)
                var ilkEleman = elemanlar.First();
                bool entityAltinda = ilkEleman.Ancestors().Any(a =>
                    a.Name.LocalName.Equals("Entity", StringComparison.OrdinalIgnoreCase) ||
                    a.Name.LocalName.Equals("SpecialEntity", StringComparison.OrdinalIgnoreCase) ||
                    a.Name.LocalName.Equals("Person", StringComparison.OrdinalIgnoreCase));

                if (entityAltinda)
                {
                    sutunlar.Insert(0, new SutunBilgisi { Ad = "entity_id", Tip = SutunTipi.EntityId });
                }

                // Parent ID sütunu ekle (parent'ın id'si varsa ve Entity değilse)
                var (parentYol, parentElement) = ParentBul(ilkEleman, tekrarlayanlar);
                if (!string.IsNullOrEmpty(parentYol) && parentElement != null)
                {
                    // Parent'ın kendisi Entity/SpecialEntity değilse
                    var parentAdi = parentYol.Split('/').Last();
                    bool parentIsEntity = parentAdi.Equals("Entity", StringComparison.OrdinalIgnoreCase) ||
                                          parentAdi.Equals("SpecialEntity", StringComparison.OrdinalIgnoreCase) ||
                                          parentAdi.Equals("Person", StringComparison.OrdinalIgnoreCase);

                    // Parent'ın gerçekten id veya code attribute'u var mı kontrol et
                    bool parentHasId = parentElement.Attributes().Any(a =>
                        a.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                        a.Name.LocalName.Equals("code", StringComparison.OrdinalIgnoreCase));

                    if (!parentIsEntity && parentHasId)
                    {
                        sutunlar.Insert(0, new SutunBilgisi { Ad = "parent_id", Tip = SutunTipi.ParentId });
                    }
                }

                Console.WriteLine($"  Sütunlar: {string.Join(", ", sutunlar.Select(s => s.Ad))}");

                // CSV'ye yaz
                using var sw = new StreamWriter(csvYolu, false, new UTF8Encoding(true));
                using var csv = new CsvWriter(sw, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    Delimiter = ";",
                    HasHeaderRecord = true
                });

                // Header
                foreach (var sutun in sutunlar)
                {
                    csv.WriteField(sutun.Ad);
                }
                csv.NextRecord();

                // Veriler
                int yazilan = 0;
                foreach (var el in elemanlar)
                {
                    var parentInfo = ParentBul(el, tekrarlayanlar);
                    foreach (var sutun in sutunlar)
                    {
                        var deger = DegerCek(el, sutun, parentInfo);
                        csv.WriteField(deger);
                    }
                    csv.NextRecord();
                    yazilan++;
                }

                Console.WriteLine($"  {yazilan} satır yazıldı");

                islenmisTablolar.Add(yol);
                olusturulanTabloAdlari.Add(tabloAdi);
            }

            Console.WriteLine($"\nToplam {islenmisTablolar.Count} tablo oluşturuldu.");
        }

        /// <summary>
        /// Tekrarlayan elemanları tespit eder.
        /// Bir eleman tekrarlıyordur eğer:
        /// 1. Parent içinde birden fazla kardeşi varsa
        /// 2. Parent'ı "List", "Details", "Types", "References" gibi container ise
        /// </summary>
        private static HashSet<string> TekrarlayanElemanlariTespit(XElement root)
        {
            var sonuc = new HashSet<string>();

            // Container pattern'leri - bunların child'ları tekrarlayan sayılır
            var containerSuffixes = new[] { "List", "Types", "References" };

            void Tara(XElement el, string yol)
            {
                var cocukGruplari = el.Elements()
                    .GroupBy(c => c.Name.LocalName)
                    .ToList();

                // Parent bir container mı? (CountryList, OccupationList, vb.)
                bool parentIsContainer = containerSuffixes.Any(s =>
                    el.Name.LocalName.EndsWith(s, StringComparison.OrdinalIgnoreCase));

                foreach (var grup in cocukGruplari)
                {
                    var cocukYol = yol + "/" + grup.Key;

                    // Bu çocuk tekrarlıyor mu?
                    bool tekrarliyor = false;

                    // Kural 1: Birden fazla kardeş
                    if (grup.Count() > 1)
                    {
                        tekrarliyor = true;
                    }
                    // Kural 2: Parent bir container (List, Types, References ile biter)
                    else if (parentIsContainer)
                    {
                        tekrarliyor = true;
                    }

                    if (tekrarliyor)
                    {
                        sonuc.Add(cocukYol);
                    }

                    // Alt elemanları tara
                    foreach (var cocuk in grup)
                    {
                        Tara(cocuk, cocukYol);
                    }
                }
            }

            Tara(root, "/" + root.Name.LocalName);

            // Yaprak elemanları çıkar (sadece text içeren, child element'i ve attribute'ı olmayan)
            var yapraklar = sonuc.Where(yol =>
            {
                var ornek = BulIlkEleman(root, yol);
                if (ornek == null) return false;

                // Hiç child elementi yoksa ve attribute'ı da yoksa yaprak - tablo yapma
                return !ornek.HasElements && !ornek.HasAttributes;
            }).ToList();

            foreach (var yaprak in yapraklar)
            {
                sonuc.Remove(yaprak);
            }

            return sonuc;
        }

        /// <summary>
        /// Belirli bir yoldaki ilk elemanı bulur.
        /// </summary>
        private static XElement? BulIlkEleman(XElement root, string yol)
        {
            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();

            XElement? mevcut = root;
            foreach (var parca in parcalar)
            {
                mevcut = mevcut?.Elements().FirstOrDefault(e => e.Name.LocalName == parca);
                if (mevcut == null) break;
            }

            return mevcut;
        }

        /// <summary>
        /// Belirli bir yoldaki tüm elemanları bulur.
        /// </summary>
        private static List<XElement> BulElemanlar(XElement root, string yol)
        {
            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();

            IEnumerable<XElement> mevcut = new[] { root };

            foreach (var parca in parcalar)
            {
                mevcut = mevcut.SelectMany(e => e.Elements().Where(c => c.Name.LocalName == parca));
            }

            return mevcut.ToList();
        }

        /// <summary>
        /// Bir eleman için sütunları belirler.
        /// Yaprak child'lar ve attribute'lar sütun olur.
        /// Tekrarlayan child'lar sütun olmaz (ayrı tablo olacaklar).
        /// </summary>
        private static List<SutunBilgisi> SutunlariBelirle(XElement ornek, HashSet<string> tekrarlayanlar, string tabloYolu)
        {
            var sutunlar = new List<SutunBilgisi>();

            // 1. Kendi attribute'ları
            foreach (var attr in ornek.Attributes().OrderBy(a => a.Name.LocalName))
            {
                if (attr.Name.LocalName.StartsWith("xmlns"))
                    continue;

                sutunlar.Add(new SutunBilgisi
                {
                    Ad = SnakeCase(attr.Name.LocalName),
                    Tip = SutunTipi.Attribute,
                    Kaynak = attr.Name.LocalName
                });
            }

            // 2. Child elemanları tara
            var cocuklar = ornek.Elements()
                .GroupBy(e => e.Name.LocalName)
                .ToList();

            foreach (var grup in cocuklar.OrderBy(g => g.Key))
            {
                var cocukYol = tabloYolu + "/" + grup.Key;

                // Bu child tekrarlayan bir tablo mu?
                if (tekrarlayanlar.Contains(cocukYol))
                    continue; // Ayrı tablo olacak, sütun olarak ekleme

                var ilkCocuk = grup.First();

                // Child'ın attribute'ları
                foreach (var attr in ilkCocuk.Attributes().OrderBy(a => a.Name.LocalName))
                {
                    if (attr.Name.LocalName.StartsWith("xmlns"))
                        continue;

                    sutunlar.Add(new SutunBilgisi
                    {
                        Ad = SnakeCase(grup.Key + "_" + attr.Name.LocalName),
                        Tip = SutunTipi.ChildAttribute,
                        Kaynak = grup.Key,
                        AltKaynak = attr.Name.LocalName
                    });
                }

                // Child'ın text değeri veya alt child'ları
                if (ilkCocuk.HasElements)
                {
                    // Alt child'ları recursive ekle (2 seviye)
                    foreach (var altGrup in ilkCocuk.Elements().GroupBy(e => e.Name.LocalName).OrderBy(g => g.Key))
                    {
                        var altCocukYol = cocukYol + "/" + altGrup.Key;

                        // Bu da tekrarlayan mı?
                        if (tekrarlayanlar.Contains(altCocukYol))
                            continue;

                        var ilkAlt = altGrup.First();

                        // Alt child attribute'ları
                        foreach (var attr in ilkAlt.Attributes().OrderBy(a => a.Name.LocalName))
                        {
                            if (attr.Name.LocalName.StartsWith("xmlns"))
                                continue;

                            sutunlar.Add(new SutunBilgisi
                            {
                                Ad = SnakeCase(grup.Key + "_" + altGrup.Key + "_" + attr.Name.LocalName),
                                Tip = SutunTipi.NestedAttribute,
                                Kaynak = grup.Key + "/" + altGrup.Key,
                                AltKaynak = attr.Name.LocalName
                            });
                        }

                        // Alt child text değeri
                        if (!ilkAlt.HasElements)
                        {
                            sutunlar.Add(new SutunBilgisi
                            {
                                Ad = SnakeCase(grup.Key + "_" + altGrup.Key),
                                Tip = SutunTipi.NestedText,
                                Kaynak = grup.Key + "/" + altGrup.Key
                            });
                        }
                    }
                }
                else
                {
                    // Yaprak child - text değeri
                    sutunlar.Add(new SutunBilgisi
                    {
                        Ad = SnakeCase(grup.Key),
                        Tip = SutunTipi.ChildText,
                        Kaynak = grup.Key
                    });
                }
            }

            // 3. Kendi text değeri (child element yoksa)
            if (!ornek.HasElements && !string.IsNullOrWhiteSpace(ornek.Value))
            {
                sutunlar.Add(new SutunBilgisi
                {
                    Ad = "value",
                    Tip = SutunTipi.Text
                });
            }

            return sutunlar;
        }

        /// <summary>
        /// Parent tablo yolunu ve o tabloya ait en yakın üst elemanı bulur.
        /// </summary>
        private static (string? parentYol, XElement? parentElement) ParentBul(XElement mevcutEleman, HashSet<string> tekrarlayanlar)
        {
            var mevcutYolParcalari = mevcutEleman.AncestorsAndSelf().Select(a => a.Name.LocalName).Reverse().ToList();

            for (int i = mevcutYolParcalari.Count - 2; i >= 0; i--)
            {
                var potansiyelParentYol = "/" + string.Join("/", mevcutYolParcalari.Take(i + 1));
                if (tekrarlayanlar.Contains(potansiyelParentYol))
                {
                    // Bu yola uyan en yakın parent elementi bul
                    var parentElement = mevcutEleman.Parent;
                    while (parentElement != null)
                    {
                        var parentYolParcalari = parentElement.AncestorsAndSelf().Select(a => a.Name.LocalName).Reverse().ToList();
                        var parentYol = "/" + string.Join("/", parentYolParcalari);

                        if (parentYol == potansiyelParentYol)
                        {
                            return (potansiyelParentYol, parentElement);
                        }
                        parentElement = parentElement.Parent;
                    }
                }
            }

            return (null, null);
        }

        /// <summary>
        /// Elemandan değer çeker.
        /// </summary>
        private static string DegerCek(XElement el, SutunBilgisi sutun, (string? parentYol, XElement? parentElement) parentInfo)
        {
            switch (sutun.Tip)
            {
                case SutunTipi.ParentId:
                    // Hiyerarşide yukarı çıkarak id'si olan ilk ancestor'u bul (Entity/SpecialEntity hariç)
                    var parentAncestor = el.Parent;
                    while (parentAncestor != null)
                    {
                        // Entity/SpecialEntity'ye ulaştıysak dur (entity_id onu alacak)
                        if (parentAncestor.Name.LocalName.Equals("Entity", StringComparison.OrdinalIgnoreCase) ||
                            parentAncestor.Name.LocalName.Equals("SpecialEntity", StringComparison.OrdinalIgnoreCase) ||
                            parentAncestor.Name.LocalName.Equals("Person", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        // "id", "Id", "ID" ara
                        var idAttr = parentAncestor.Attributes()
                            .FirstOrDefault(a => a.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase));
                        if (idAttr != null) return idAttr.Value;

                        // "code" ara
                        var codeAttr = parentAncestor.Attributes()
                            .FirstOrDefault(a => a.Name.LocalName.Equals("code", StringComparison.OrdinalIgnoreCase));
                        if (codeAttr != null) return codeAttr.Value;

                        parentAncestor = parentAncestor.Parent;
                    }
                    return "";

                case SutunTipi.EntityId:
                    // Entity veya SpecialEntity seviyesine kadar çık ve id'sini al
                    var entityAncestor = el.Parent;
                    while (entityAncestor != null)
                    {
                        if (entityAncestor.Name.LocalName.Equals("Entity", StringComparison.OrdinalIgnoreCase) ||
                            entityAncestor.Name.LocalName.Equals("SpecialEntity", StringComparison.OrdinalIgnoreCase) ||
                            entityAncestor.Name.LocalName.Equals("Person", StringComparison.OrdinalIgnoreCase))
                        {
                            var idAttr = entityAncestor.Attributes()
                                .FirstOrDefault(a => a.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase));
                            if (idAttr != null) return idAttr.Value;
                        }
                        entityAncestor = entityAncestor.Parent;
                    }
                    return "";

                case SutunTipi.Attribute:
                    return el.Attribute(sutun.Kaynak)?.Value ?? "";

                case SutunTipi.Text:
                    return el.Value?.Trim() ?? "";

                case SutunTipi.ChildText:
                    var child = el.Element(sutun.Kaynak);
                    return child?.Value?.Trim() ?? "";

                case SutunTipi.ChildAttribute:
                    var childEl = el.Element(sutun.Kaynak);
                    return childEl?.Attribute(sutun.AltKaynak)?.Value ?? "";

                case SutunTipi.NestedText:
                    var parts = sutun.Kaynak.Split('/');
                    XElement? current = el;
                    foreach (var part in parts)
                    {
                        current = current?.Element(part);
                    }
                    return current?.Value?.Trim() ?? "";

                case SutunTipi.NestedAttribute:
                    var nestedParts = sutun.Kaynak.Split('/');
                    XElement? nestedCurrent = el;
                    foreach (var part in nestedParts)
                    {
                        nestedCurrent = nestedCurrent?.Element(part);
                    }
                    return nestedCurrent?.Attribute(sutun.AltKaynak)?.Value ?? "";

                default:
                    return "";
            }
        }

        /// <summary>
        /// Tablo adı oluşturur.
        /// </summary>
        private static string TabloAdiOlustur(string yol)
        {
            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parcalar.Length <= 2)
            {
                return SnakeCase(parcalar.Last());
            }

            // "List" ile biten container'ları atla
            var anlamliParcalar = parcalar
                .Skip(1) // Root'u atla
                .Where(p => !p.EndsWith("List", StringComparison.OrdinalIgnoreCase) &&
                           !p.EndsWith("Details", StringComparison.OrdinalIgnoreCase) &&
                           !p.EndsWith("Types", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (anlamliParcalar.Count == 0)
            {
                anlamliParcalar = parcalar.Skip(1).ToList();
            }

            // Son 2 anlamlı parçayı al
            var sonParcalar = anlamliParcalar.TakeLast(2).ToList();

            return string.Join("_", sonParcalar.Select(SnakeCase));
        }

        /// <summary>
        /// Snake_case'e çevirir.
        /// </summary>
        private static string SnakeCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "column";

            // Akronimleri önce küçük harfe çevir (ID, URL, XML, API vb.)
            // "IDType" -> "IdType", "UserID" -> "UserId", "URL" -> "Url"
            input = Regex.Replace(input, @"([A-Z]+)([A-Z][a-z])", "$1_$2"); // "IDType" -> "ID_Type"
            input = Regex.Replace(input, @"([a-z])([A-Z])", "$1_$2");       // "userId" -> "user_Id"

            // Tümünü küçük harfe çevir
            input = input.ToLowerInvariant();

            // Birden fazla alt çizgiyi teke indir ve temizle
            input = Regex.Replace(input, @"_+", "_");
            input = Regex.Replace(input, @"[^a-z0-9_]+", "_");

            return input.Trim('_');
        }

        #region Yardımcı Sınıflar

        private class SutunBilgisi
        {
            public string Ad { get; set; } = "";
            public SutunTipi Tip { get; set; }
            public string Kaynak { get; set; } = "";
            public string? AltKaynak { get; set; }
        }

        private enum SutunTipi
        {
            Attribute,
            Text,
            ChildText,
            ChildAttribute,
            NestedText,
            NestedAttribute,
            ParentId,
            EntityId
        }

        #endregion
    }
}
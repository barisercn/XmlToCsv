// SmartXmlToCsvExporter.cs - v6 (Nihai "Flattening" Yöntemi)
// Kullanıcının son revizyonuna göre, en detaylı tekrarlayan öğeyi temel alarak ve hem üst hem de
// karmaşık alt öğelerden bağlamı "miras alarak" tek bir birleşik tabloya dönüştürür.

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
        public static void Calistir(string xmlYolu, string ciktiKlasoru)
        {
            Directory.CreateDirectory(ciktiKlasoru);
            Console.WriteLine($"XML yükleniyor: {xmlYolu}");

            XDocument doc;
            using (var reader = EncodingAwareXml.CreateAutoXmlReader(xmlYolu, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore }))
            {
                doc = XDocument.Load(reader);
            }

            if (doc.Root == null)
            {
                Console.WriteLine("HATA: XML root element bulunamadı!");
                return;
            }

            Console.WriteLine($"XML yüklendi. Root: {doc.Root.Name.LocalName}");

            var (tekrarlayanlar, gercekContainerAdlari, wrapperAdlari) = TekrarlayanElemanlariTespit(doc.Root);
            Console.WriteLine($"\nOluşturulacak tablolar ({tekrarlayanlar.Count}):");
            foreach (var t in tekrarlayanlar.OrderBy(p => p).Take(50)) Console.WriteLine($"  - {t}");
            Console.WriteLine($"\nGerçek container'lar: {string.Join(", ", gercekContainerAdlari.OrderBy(x => x))}");
            Console.WriteLine($"Otomatik wrapper'lar: {string.Join(", ", wrapperAdlari.OrderBy(x => x))}");

            var olusturulanTabloAdlari = new HashSet<string>();

            foreach (var yol in tekrarlayanlar.OrderBy(y => y.Length))
            {
                var elemanlar = BulElemanlar(doc.Root, yol);
                if (elemanlar.Count == 0) continue;

                var tabloAdi = TabloAdiOlustur(yol, olusturulanTabloAdlari, gercekContainerAdlari, wrapperAdlari);
                var csvYolu = Path.Combine(ciktiKlasoru, $"{tabloAdi}.csv");

                Console.WriteLine($"\nTablo oluşturuluyor: {tabloAdi}");
                Console.WriteLine($"  {elemanlar.Count} kayıt bulundu");

                var sutunlar = SutunlariBelirle(elemanlar, tekrarlayanlar);
                EntityIdSutunuEkle(sutunlar, elemanlar.First());

                var siraliSutunlar = sutunlar.OrderBy(s => s.Ad).ToList();

                Console.WriteLine($"  Sütunlar: {string.Join(", ", siraliSutunlar.Select(s => s.Ad))}");

                YazCsv(csvYolu, elemanlar, siraliSutunlar);

                olusturulanTabloAdlari.Add(tabloAdi);
            }

            Console.WriteLine($"\nToplam {olusturulanTabloAdlari.Count} tablo oluşturuldu.");
        }

        /// <summary>
        /// Tekrarlayan elemanları tespit eder ve hangi eleman adlarının gerçek container olduğunu belirler.
        /// Gerçek container: Çocuklarının TAMAMI karmaşık element olan sarmalayıcı (NameDetails, DateDetails vb.)
        /// Veri taşıyan: Çocuklarında yaprak metin elemanı bulunan (CompanyDetails → AddressLine gibi)
        /// </summary>
        private static (HashSet<string> tekrarlayanlar, HashSet<string> gercekContainerAdlari, HashSet<string> wrapperAdlari) TekrarlayanElemanlariTespit(XElement root)
        {
            var sonuc = new HashSet<string>();
            var containerSuffixes = new[] { "List", "Types", "References", "Details" };

            // Container suffix'i olan elemanların gerçekten container mı yoksa veri taşıyan mı
            // olduğunu tespit etmek için: çocuklarında yaprak-metin elemanı var mı kontrol et.
            var veriTasiyorMu = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // Wrapper tespiti: Attribute'sız, text'siz, tek tip çocuğu olan elemanlar.
            // Örn: Records (sadece Entity içerir), Associations (sadece SpecialEntity),
            //       Descriptions (sadece Description), SourceDescription (sadece Source)
            // Bu elemanlar yapısal sarmalayıcıdır, veri taşımazlar.
            // Key: eleman adı, Value: hâlâ wrapper adayı mı?
            var wrapperAdayi = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            void Tara(XElement el, string yol)
            {
                var elAdi = el.Name.LocalName;

                // ── Wrapper tespit ──
                if (!wrapperAdayi.ContainsKey(elAdi))
                    wrapperAdayi[elAdi] = true;

                if (wrapperAdayi[elAdi])
                {
                    // Attribute varsa → wrapper değil
                    if (el.HasAttributes)
                        wrapperAdayi[elAdi] = false;
                    // Kendi text içeriği varsa → wrapper değil
                    else if (el.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                        wrapperAdayi[elAdi] = false;
                    // Çocuk elementi yoksa (yaprak) → wrapper değil
                    else if (!el.HasElements)
                        wrapperAdayi[elAdi] = false;
                    // Birden fazla farklı çocuk tipi varsa → wrapper değil
                    else if (el.Elements().Select(c => c.Name.LocalName).Distinct().Count() > 1)
                        wrapperAdayi[elAdi] = false;
                }

                // ── Container suffix kontrolü ──
                if (containerSuffixes.Any(s => elAdi.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!veriTasiyorMu.ContainsKey(elAdi))
                        veriTasiyorMu[elAdi] = false;

                    bool yaprakCocuguVar = el.Elements().Any(c => !c.HasElements && !c.HasAttributes);
                    if (yaprakCocuguVar)
                        veriTasiyorMu[elAdi] = true;
                }

                // Veri taşıyan container-suffix elemanları her zaman ayrı tablo adayı yap (Entity altındaysa)
                if (containerSuffixes.Any(s => elAdi.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                    && veriTasiyorMu.TryGetValue(elAdi, out var tasiyor) && tasiyor)
                {
                    if (el.Ancestors().Any(a => a.Name.LocalName.Equals("Entity", StringComparison.OrdinalIgnoreCase)))
                    {
                        sonuc.Add(yol);
                    }
                }

                // NameValue'yu her zaman ayrı tablo olarak değerlendir
                if (elAdi == "NameValue")
                {
                    if (el.Ancestors().Any(a => a.Name.LocalName == "Entity"))
                    {
                        sonuc.Add(yol);
                    }
                }

                // Üst düzey referans/lookup tabloları: Root altındaki *List container'larının
                // çocuklarını tekrar sayısına bakmadan ekle (Description1Name, Description2Name vb.)
                // Bu elemanlar az kayıt içerebilir ama tabloya dönüşmeleri gerekir.
                if (el.HasAttributes && el.Parent != null
                    && containerSuffixes.Any(s => el.Parent.Name.LocalName.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                    && !el.Ancestors().Any(a => a.Name.LocalName.Equals("Entity", StringComparison.OrdinalIgnoreCase)
                                             || a.Name.LocalName.Equals("SpecialEntity", StringComparison.OrdinalIgnoreCase)))
                {
                    sonuc.Add(yol);
                }

                var cocukGruplari = el.Elements().GroupBy(c => c.Name.LocalName).ToList();
                bool parentIsContainer = el.Parent != null && containerSuffixes.Any(s => el.Parent.Name.LocalName.EndsWith(s, StringComparison.OrdinalIgnoreCase));

                foreach (var grup in cocukGruplari)
                {
                    var cocukYol = yol + "/" + grup.Key;
                    if (grup.Count() > 1)
                    {
                        sonuc.Add(cocukYol);
                    }
                    else if (parentIsContainer)
                    {
                        if (grup.Any(e => e.HasElements || e.HasAttributes))
                        {
                            sonuc.Add(cocukYol);
                        }
                    }

                    foreach (var cocuk in grup)
                    {
                        Tara(cocuk, cocukYol);
                    }
                }
            }

            Tara(root, "/" + root.Name.LocalName);

            // ── Gerçek container adlarını belirle ──
            var gercekContainerAdlari = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in veriTasiyorMu)
            {
                if (!kvp.Value)
                    gercekContainerAdlari.Add(kvp.Key);
            }

            // ── Wrapper adlarını belirle ──
            // Container suffix olanları wrapper'dan çıkar (zaten container olarak ele alınıyor)
            var wrapperAdlari = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in wrapperAdayi)
            {
                if (kvp.Value && !gercekContainerAdlari.Contains(kvp.Key)
                              && !containerSuffixes.Any(s => kvp.Key.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                    wrapperAdlari.Add(kvp.Key);
            }

            // ═══════════════════════════════════════════════════════════════
            // BUDAMA AŞAMASI 1: Gerçek container'ları (NameDetails, DateDetails vb.) sil
            // ═══════════════════════════════════════════════════════════════
            var atilacaklar = new HashSet<string>();
            foreach (var yol in sonuc)
            {
                if (yol.Count(c => c == '/') < 2) continue;
                var parentPath = yol.Substring(0, yol.LastIndexOf('/'));
                if (sonuc.Contains(parentPath))
                {
                    var parentName = parentPath.Split('/').Last();
                    if (gercekContainerAdlari.Contains(parentName))
                    {
                        // Gerçek container: kendisini sil, çocuğu kalsın
                        atilacaklar.Add(parentPath);
                    }
                    // else: Veri taşıyan parent → ikisini de koru
                }
            }
            sonuc.ExceptWith(atilacaklar);

            // ═══════════════════════════════════════════════════════════════
            // BUDAMA AŞAMASI 2: Anchor tablo absorpsiyonu
            // Anchor tablo = veriyi tek noktada birleştiren eleman (NameValue gibi).
            // - Çocuklarını absorbe eder → sütun olarak kalır (EntityName, OriginalScriptName, Suffix)
            // - Parent'ını absorbe eder → attribute olarak kalır (Name → NameType)
            //   (Sadece parent'ın yaprak-metin çocuğu yoksa, yani saf wrapper ise)
            // ═══════════════════════════════════════════════════════════════
            var anchorAtilacaklar = new HashSet<string>();
            foreach (var yol in sonuc.ToList())
            {
                var elAdi = yol.Split('/').Last();
                if (elAdi != "NameValue") continue; // Şimdilik NameValue anchor

                // 2a. Çocuk path'leri sil → sütun olarak kalacaklar
                foreach (var digerYol in sonuc)
                {
                    if (digerYol.StartsWith(yol + "/"))
                        anchorAtilacaklar.Add(digerYol);
                }

                // 2b. Parent path'i sil → attribute olarak NameValue'ya geçecek
                // (Sadece parent'ın kendi yaprak-metin çocuğu yoksa.
                //  Örn: Name sadece NameValue içerir, kendi text'i yok → wrapper → sil)
                var parentPath = yol.Substring(0, yol.LastIndexOf('/'));
                if (sonuc.Contains(parentPath))
                {
                    var parentElemanlar = BulElemanlar(root, parentPath);
                    // Parent'ın yaprak-metin çocuğu var mı? (attribute'sız, element'siz, sadece text)
                    bool parentSadeceWrapper = !parentElemanlar.Any(pe =>
                        pe.Elements().Any(c => !c.HasElements && !c.HasAttributes
                                            && !string.IsNullOrWhiteSpace(c.Value)));
                    if (parentSadeceWrapper)
                    {
                        anchorAtilacaklar.Add(parentPath);
                        Console.WriteLine($"  [Anchor] {elAdi} parent'ı absorbe etti: {parentPath.Split('/').Last()}");
                    }
                }
            }
            sonuc.ExceptWith(anchorAtilacaklar);

            // ═══════════════════════════════════════════════════════════════
            // BUDAMA AŞAMASI 3: Parent-child duplicate tespiti
            // Sadece parent ve child AYNI tablo adına resolve oluyorsa parent'ı sil.
            // Örn: Date ve DateValue ikisi de "entity_date" → Date silinir.
            // Ama Entity ve CompanyDetails farklı tablolar → Entity korunur.
            // ═══════════════════════════════════════════════════════════════
            var parentSilinecekler = new HashSet<string>();
            var geciciAdlar = new HashSet<string>(); // çakışma kontrolü için boş set
            foreach (var yol in sonuc.ToList())
            {
                if (yol.Count(c => c == '/') < 2) continue;
                var parentPath = yol.Substring(0, yol.LastIndexOf('/'));
                if (sonuc.Contains(parentPath))
                {
                    // İkisi de aynı tablo adına mı dönüşüyor?
                    var childTabloAdi = TabloAdiOlustur(yol, geciciAdlar, gercekContainerAdlari, wrapperAdlari);
                    var parentTabloAdi = TabloAdiOlustur(parentPath, geciciAdlar, gercekContainerAdlari, wrapperAdlari);
                    if (string.Equals(childTabloAdi, parentTabloAdi, StringComparison.OrdinalIgnoreCase))
                    {
                        parentSilinecekler.Add(parentPath);
                    }
                }
            }
            sonuc.ExceptWith(parentSilinecekler);

            return (sonuc, gercekContainerAdlari, wrapperAdlari);
        }

        private static List<SutunBilgisi> SutunlariBelirle(List<XElement> elemanlar, HashSet<string> tekrarlayanlar)
        {
            var sutunlar = new List<SutunBilgisi>();
            if (!elemanlar.Any()) return sutunlar;

            // 1. Üst elementlerin niteliklerini topla (örn: Name'den NameType'ı almak için)
            // Sadece parent'ın kendisi ayrı bir tablo DEĞİLSE parent attribute'larını al
            var parentYol = GetElementPath(elemanlar.First().Parent);
            bool parentAyriTablo = tekrarlayanlar.Contains(parentYol);

            if (!parentAyriTablo)
            {
                var tumUstNitelikler = elemanlar.Select(el => el.Parent).Where(p => p != null)
                    .SelectMany(p => p.Attributes())
                    .Where(a => !a.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.Name.LocalName).Distinct();
                foreach (var attrName in tumUstNitelikler)
                {
                    sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(attrName), Tip = SutunTipi.ParentAttribute, Kaynak = attrName });
                }
            }

            // 2. Kendi niteliklerini topla
            var tumKendiNitelikleri = elemanlar.SelectMany(el => el.Attributes()).Select(a => a.Name.LocalName).Distinct();
            foreach (var attrName in tumKendiNitelikleri)
            {
                sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(attrName), Tip = SutunTipi.Attribute, Kaynak = attrName });
            }
            
            // 3. Çocuk elementleri analiz et
            var tumCocukGruplari = elemanlar.SelectMany(el => el.Elements()).GroupBy(c => c.Name.LocalName);
            foreach (var grup in tumCocukGruplari)
            {
                var cocukAdi = grup.Key;
                var ornekCocuk = grup.First();

                // Eğer bu çocuk grubu, başka bir tablo olarak zaten işlenecekse, onu burada düzleştirme.
                if (tekrarlayanlar.Contains(GetElementPath(ornekCocuk)))
                {
                    continue;
                }
                
                // 3a. Çocuk yaprak ise (içinde başka element yoksa), metin değerini sütun yap. Örn: <EntityName>
                if (!ornekCocuk.HasElements)
                {
                    sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(cocukAdi), Tip = SutunTipi.ChildText, Kaynak = cocukAdi });
                }
                // 3b. Çocuk karmaşık ama ayrı bir tablo değilse, niteliklerini sütun yap. Örn: <DateValue> 
                else
                {
                    var tumCocukNitelikleri = grup.SelectMany(c => c.Attributes()).Select(a => a.Name.LocalName).Distinct();
                    foreach (var attrName in tumCocukNitelikleri)
                    {
                        sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(cocukAdi + "_" + attrName), Tip = SutunTipi.ChildAttribute, Kaynak = cocukAdi, AltKaynak = attrName });
                    }
                }
            }

            // 4. Kendi metin değeri (eğer hiç çocuk elementi yoksa. örn: <IDValue>)
            if (!elemanlar.First().HasElements && elemanlar.Any(e => !string.IsNullOrWhiteSpace(e.Value)))
            {
                sutunlar.Add(new SutunBilgisi { Ad = "value", Tip = SutunTipi.Text });
            }
            
            return sutunlar.GroupBy(s => s.Ad).Select(g => g.First()).ToList(); // Aynı isimde sütunları tekilleştir.
        }

        private static void YazCsv(string csvYolu, List<XElement> elemanlar, List<SutunBilgisi> sutunlar)
        {
            using var sw = new StreamWriter(csvYolu, false, new UTF8Encoding(true));
            using var csv = new CsvWriter(sw, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";", HasHeaderRecord = true });

            foreach (var sutun in sutunlar) csv.WriteField(sutun.Ad);
            csv.NextRecord();

            foreach (var el in elemanlar)
            {
                foreach (var sutun in sutunlar)
                {
                    csv.WriteField(DegerCek(el, sutun));
                }
                csv.NextRecord();
            }
        }
        
        private static string DegerCek(XElement el, SutunBilgisi sutun)
        {
            switch (sutun.Tip)
            {
                case SutunTipi.EntityId:
                    var ancestor = el.Ancestors().FirstOrDefault(a => a.Name.LocalName.Equals("Entity", StringComparison.OrdinalIgnoreCase) ||
                                                                    a.Name.LocalName.Equals("SpecialEntity", StringComparison.OrdinalIgnoreCase));
                    return ancestor?.Attribute("id")?.Value ?? "";
                case SutunTipi.ParentAttribute:
                    return el.Parent?.Attribute(sutun.Kaynak)?.Value ?? "";
                case SutunTipi.Attribute:
                    return el.Attribute(sutun.Kaynak)?.Value ?? "";
                case SutunTipi.Text:
                    return el.Value?.Trim() ?? "";
                case SutunTipi.ChildText:
                    return el.Element(sutun.Kaynak)?.Value?.Trim() ?? "";
                case SutunTipi.ChildAttribute:
                    return el.Element(sutun.Kaynak)?.Attribute(sutun.AltKaynak)?.Value ?? "";
                default:
                    return "";
            }
        }

        #region Yardımcı Metotlar

        private static string GetElementPath(XElement el)
        {
            if (el == null) return "";
            return "/" + string.Join("/", el.AncestorsAndSelf().Select(a => a.Name.LocalName).Reverse());
        }

        private static List<XElement> BulElemanlar(XElement root, string yol)
        {
            IEnumerable<XElement> mevcut = new[] { root };
            foreach (var parca in yol.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                mevcut = mevcut.SelectMany(e => e.Elements(parca));
            }
            return mevcut.ToList();
        }

        /// <summary>
        /// Evrensel tablo adı oluşturma kuralları:
        /// 1. Root'u atla
        /// 2. Wrapper'ları filtrele (Records, Descriptions gibi otomatik tespit edilenler)
        /// 3. Container'ları kök adıyla değiştir (IDNumberTypes → IDNumber, DateDetails → Date)
        /// 4. "Value" son ekini kaldır (NameValue → Name, DateValue → Date)
        /// 5. Ardışık tekrar eden kökleri tekilleştir (Date/Date → Date)
        /// 6. Son 3 anlamlı parçadan snake_case ad oluştur
        /// 7. Çakışma varsa tüm parçaları kullan
        /// </summary>
        private static string TabloAdiOlustur(string yol, HashSet<string> mevcutAdlar,
            HashSet<string> gercekContainerAdlari, HashSet<string> wrapperAdlari)
        {
            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Adım 1-3: Root atla, wrapper filtrele, container → kök adı
            var anlamliParcalar = new List<string>();
            foreach (var p in parcalar.Skip(1)) // Root'u atla
            {
                if (wrapperAdlari.Contains(p))
                    continue; // Wrapper: tamamen atla

                if (gercekContainerAdlari.Contains(p))
                {
                    // Container: suffix'leri sıyırıp kök adını al (IDNumberTypes → IDNumber)
                    anlamliParcalar.Add(ContainerKokAdi(p));
                    continue;
                }

                anlamliParcalar.Add(p);
            }

            // Adım 4: "Value" son ekini kaldır
            for (int i = 0; i < anlamliParcalar.Count; i++)
            {
                var p = anlamliParcalar[i];
                if (p.Length > 5 && p.EndsWith("Value", StringComparison.Ordinal))
                    anlamliParcalar[i] = p.Substring(0, p.Length - 5);
            }

            // Adım 5: Ardışık tekrar eden kökleri tekilleştir
            // Örn: [Entity, Date, Date] → [Entity, Date]
            // Örn: [Entity, IDNumber, ID] → [Entity, IDNumber] (IDNumber, ID'yi kapsar)
            var tekilParcalar = new List<string>();
            foreach (var p in anlamliParcalar)
            {
                if (tekilParcalar.Count > 0)
                {
                    var onceki = tekilParcalar[^1];
                    // Tamamen aynı → atla
                    if (string.Equals(onceki, p, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Önceki, yeniyi kapsar (IDNumber > ID) → yeniyi atla
                    if (onceki.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Yeni, öncekini kapsar (CountryName > Country) → öncekini güncelle
                    if (p.StartsWith(onceki, StringComparison.OrdinalIgnoreCase))
                    {
                        tekilParcalar[^1] = p;
                        continue;
                    }
                }
                tekilParcalar.Add(p);
            }

            // Adım 6: Son 3 parçadan ad oluştur
            var tabloAdi = string.Join("_", tekilParcalar.TakeLast(3).Select(SnakeCase));
            if (string.IsNullOrEmpty(tabloAdi)) tabloAdi = SnakeCase(parcalar.Last());

            // Adım 7: Çakışma kontrolü → tüm parçaları kullan
            if (mevcutAdlar.Contains(tabloAdi))
            {
                tabloAdi = string.Join("_", tekilParcalar.Select(SnakeCase));
            }
            return tabloAdi;
        }

        /// <summary>
        /// Container eleman adından yapısal suffix'leri sıyırarak kök adını çıkarır.
        /// IDNumberTypes → IDNumber, SanctionsReferencesList → Sanctions,
        /// DateDetails → Date, NameTypeList → NameType
        /// </summary>
        private static string ContainerKokAdi(string containerAdi)
        {
            var suffixes = new[] { "List", "Types", "References", "Details" };
            var sonuc = containerAdi;
            // Birden fazla suffix olabilir (SanctionsReferencesList → Sanctions)
            bool degisti;
            do
            {
                degisti = false;
                foreach (var s in suffixes)
                {
                    if (sonuc.Length > s.Length && sonuc.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                    {
                        sonuc = sonuc.Substring(0, sonuc.Length - s.Length);
                        degisti = true;
                    }
                }
            } while (degisti);

            return string.IsNullOrEmpty(sonuc) ? containerAdi : sonuc;
        }

        private static void EntityIdSutunuEkle(List<SutunBilgisi> sutunlar, XElement ilkEleman)
        {
            if (ilkEleman == null) return;
            bool entityAltinda = ilkEleman.Ancestors().Any(a =>
                a.Name.LocalName.Equals("Entity", StringComparison.OrdinalIgnoreCase) ||
                a.Name.LocalName.Equals("SpecialEntity", StringComparison.OrdinalIgnoreCase));
            if (entityAltinda && !sutunlar.Any(s => s.Ad == "entity_id"))
            {
                sutunlar.Insert(0, new SutunBilgisi { Ad = "entity_id", Tip = SutunTipi.EntityId });
            }
        }

        private static string SnakeCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "column";
            string pattern = @"([A-Z]+)([A-Z][a-z])|([a-z0-9])([A-Z])";
            return Regex.Replace(input, pattern, "$1$3_$2$4").ToLowerInvariant().Replace("__", "_");
        }
        #endregion

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
            Attribute, Text, ChildText, ParentAttribute, ChildAttribute, EntityId
        }
        #endregion
    }
}
// SmartXmlToCsvExporter.cs - v7 (Evrensel XML-to-Relational Mapper)
// Hardcoded Dow Jones referansları kaldırılmış, herhangi bir XML dosyasını
// otomatik olarak ilişkisel tablolara dönüştürebilen evrensel yapı.

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

                var sutunlar = SutunlariBelirle(elemanlar, tekrarlayanlar, wrapperAdlari);
                ForeignKeySutunuEkle(sutunlar, elemanlar.First(), tekrarlayanlar);

                var siraliSutunlar = sutunlar.OrderBy(s => s.Ad).ToList();

                Console.WriteLine($"  Sütunlar: {string.Join(", ", siraliSutunlar.Select(s => s.Ad))}");

                YazCsv(csvYolu, elemanlar, siraliSutunlar);

                olusturulanTabloAdlari.Add(tabloAdi);
            }

            Console.WriteLine($"\nToplam {olusturulanTabloAdlari.Count} tablo oluşturuldu.");
        }

        /// <summary>
        /// Bir elemanın yapısal olarak container olup olmadığını belirler (cache'li).
        /// Container: Çocuk elementi var, kendi text içeriği yok, tüm çocukları karmaşık (HasElements || HasAttributes).
        /// </summary>
        private static bool YapisalContainerMi(XElement el, Dictionary<XElement, bool> cache)
        {
            if (cache.TryGetValue(el, out var sonucCached))
                return sonucCached;

            bool sonucVal;
            if (!el.HasElements)
                sonucVal = false;
            else if (el.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                sonucVal = false;
            else
                sonucVal = el.Elements().All(c => c.HasElements || c.HasAttributes);

            cache[el] = sonucVal;
            return sonucVal;
        }

        /// <summary>
        /// Tekrarlayan elemanları tespit eder ve hangi eleman adlarının gerçek container olduğunu belirler.
        /// </summary>
        private static (HashSet<string> tekrarlayanlar, HashSet<string> gercekContainerAdlari, HashSet<string> wrapperAdlari) TekrarlayanElemanlariTespit(XElement root)
        {
            var sonuc = new HashSet<string>();
            var containerCache = new Dictionary<XElement, bool>(ReferenceEqualityComparer.Instance);

            // Yapısal container tespiti: eleman adı → veri taşıyor mu?
            var containerVeriDurumu = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // Wrapper tespiti
            var wrapperAdayi = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            void Tara(XElement el, string yol, bool parentContainer)
            {
                var elAdi = el.Name.LocalName;

                // ── Wrapper tespit ──
                if (!wrapperAdayi.ContainsKey(elAdi))
                    wrapperAdayi[elAdi] = true;

                if (wrapperAdayi[elAdi])
                {
                    if (el.HasAttributes)
                        wrapperAdayi[elAdi] = false;
                    else if (el.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                        wrapperAdayi[elAdi] = false;
                    else if (!el.HasElements)
                        wrapperAdayi[elAdi] = false;
                    else if (el.Elements().Select(c => c.Name.LocalName).Distinct().Count() > 1)
                        wrapperAdayi[elAdi] = false;
                }

                // ── Yapısal container kontrolü ──
                bool elContainer = YapisalContainerMi(el, containerCache);
                if (elContainer)
                {
                    if (!containerVeriDurumu.ContainsKey(elAdi))
                        containerVeriDurumu[elAdi] = false;

                    // Bazı örnekleri veri taşıyabilir
                    bool veriTasiyor = el.Elements().Any(c => !c.HasElements && !c.HasAttributes);
                    if (veriTasiyor)
                        containerVeriDurumu[elAdi] = true;
                }

                // Veri taşıyan container elemanları → ayrı tablo adayı
                if (containerVeriDurumu.TryGetValue(elAdi, out var tasiyor) && tasiyor)
                {
                    sonuc.Add(yol);
                }

                // Üst düzey referans/lookup tabloları
                if (el.HasAttributes && el.Parent != null && parentContainer)
                {
                    sonuc.Add(yol);
                }

                var cocukGruplari = el.Elements().GroupBy(c => c.Name.LocalName).ToList();
                foreach (var grup in cocukGruplari)
                {
                    var cocukYol = yol + "/" + grup.Key;
                    if (grup.Count() > 1)
                    {
                        sonuc.Add(cocukYol);
                    }
                    else if (parentContainer || elContainer)
                    {
                        if (grup.Any(e => e.HasElements || e.HasAttributes))
                        {
                            sonuc.Add(cocukYol);
                        }
                    }

                    foreach (var cocuk in grup)
                    {
                        Tara(cocuk, cocukYol, elContainer);
                    }
                }
            }

            Tara(root, "/" + root.Name.LocalName, false);

            // ── Gerçek container adlarını belirle (yapısal container + veri taşımayan) ──
            var gercekContainerAdlari = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in containerVeriDurumu)
            {
                if (!kvp.Value)
                    gercekContainerAdlari.Add(kvp.Key);
            }

            // ── Wrapper adlarını belirle ──
            var wrapperAdlari = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in wrapperAdayi)
            {
                if (kvp.Value && !gercekContainerAdlari.Contains(kvp.Key))
                    wrapperAdlari.Add(kvp.Key);
            }

            // ═══════════════════════════════════════════════════════════════
            // META-WRAPPER: Tüm çocukları container veya wrapper olan elemanları da wrapper yap
            // ═══════════════════════════════════════════════════════════════
            bool degisti;
            do
            {
                degisti = false;
                foreach (var kvp in wrapperAdayi.ToList())
                {
                    if (kvp.Value) continue; // zaten wrapper adayı
                    if (gercekContainerAdlari.Contains(kvp.Key)) continue;
                    // Bu eleman adının tüm örneklerini kontrol et
                    var ornekler = root.Descendants().Where(d => d.Name.LocalName.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (ornekler.Count == 0) continue;
                    bool hepsiMetaWrapper = ornekler.All(el =>
                        el.HasElements && !el.HasAttributes
                        && !el.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))
                        && el.Elements().All(c =>
                            gercekContainerAdlari.Contains(c.Name.LocalName)
                            || wrapperAdlari.Contains(c.Name.LocalName)));
                    if (hepsiMetaWrapper)
                    {
                        wrapperAdlari.Add(kvp.Key);
                        wrapperAdayi[kvp.Key] = true;
                        degisti = true;
                    }
                }
            } while (degisti);

            // ═══════════════════════════════════════════════════════════════
            // BUDAMA AŞAMASI 1: Gerçek container'ları sil
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
                        atilacaklar.Add(parentPath);
                    }
                }
            }
            sonuc.ExceptWith(atilacaklar);

            // ═══════════════════════════════════════════════════════════════
            // BUDAMA AŞAMASI 3: Parent-child duplicate tespiti
            // Wrapper child ile parent aynı tablo adı üretirse → child silinir
            // (parent daha zengin veri taşır). Diğer durumlarda → parent silinir.
            // ═══════════════════════════════════════════════════════════════
            var duplicateSilinecekler = new HashSet<string>();
            var geciciAdlar = new HashSet<string>();
            foreach (var yol in sonuc.ToList())
            {
                if (yol.Count(c => c == '/') < 2) continue;
                var parentPath = yol.Substring(0, yol.LastIndexOf('/'));
                if (sonuc.Contains(parentPath))
                {
                    var childTabloAdi = TabloAdiOlustur(yol, geciciAdlar, gercekContainerAdlari, wrapperAdlari);
                    var parentTabloAdi = TabloAdiOlustur(parentPath, geciciAdlar, gercekContainerAdlari, wrapperAdlari);
                    if (string.Equals(childTabloAdi, parentTabloAdi, StringComparison.OrdinalIgnoreCase))
                    {
                        // Child'ın son segmenti wrapper ise → child gereksiz, parent'ı koru
                        var childLastSegment = yol.Split('/').Last();
                        if (wrapperAdlari.Contains(childLastSegment))
                            duplicateSilinecekler.Add(yol);       // wrapper child'ı sil
                        else
                            duplicateSilinecekler.Add(parentPath); // orijinal davranış
                    }
                }
            }
            sonuc.ExceptWith(duplicateSilinecekler);

            // ═══════════════════════════════════════════════════════════════
            // BUDAMA AŞAMASI 4: 1:1 Flatten — sadece leaf çocukları olan,
            // parent'ı da tablo olan, parent başına en fazla 1 kez görünen tabloları
            // parent'a flatten et (ayrı tablo olmasın).
            // ═══════════════════════════════════════════════════════════════
            var flattenSilinecekler = new HashSet<string>();
            foreach (var yol in sonuc.ToList())
            {
                if (yol.Count(c => c == '/') < 2) continue;
                var parentPath = yol.Substring(0, yol.LastIndexOf('/'));

                // Parent de tablo olmalı
                if (!sonuc.Contains(parentPath)) continue;

                var elAdi = yol.Split('/').Last();

                // Elemanları bul
                var elemanlar = BulElemanlar(root, yol);
                if (elemanlar.Count == 0) continue;

                // Sadece leaf çocukları olmalı (karmaşık alt eleman yok)
                bool sadeceLeaf = elemanlar.All(el =>
                    !el.Elements().Any(c => c.HasElements));
                if (!sadeceLeaf) continue;

                // Parent başına en fazla 1 kez görünmeli
                var parentElemanlar = BulElemanlar(root, parentPath);
                bool tekOrnek = parentElemanlar.All(pe =>
                    pe.Elements().Count(c => c.Name.LocalName.Equals(elAdi, StringComparison.OrdinalIgnoreCase)) <= 1);
                if (!tekOrnek) continue;

                flattenSilinecekler.Add(yol);
                Console.WriteLine($"  [Flatten] {elAdi} → parent'a flatten edildi");
            }
            sonuc.ExceptWith(flattenSilinecekler);

            return (sonuc, gercekContainerAdlari, wrapperAdlari);
        }

        private static List<SutunBilgisi> SutunlariBelirle(List<XElement> elemanlar, HashSet<string> tekrarlayanlar, HashSet<string> wrapperAdlari)
        {
            var sutunlar = new List<SutunBilgisi>();
            if (!elemanlar.Any()) return sutunlar;

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

            var tumKendiNitelikleri = elemanlar.SelectMany(el => el.Attributes()).Select(a => a.Name.LocalName).Distinct();
            foreach (var attrName in tumKendiNitelikleri)
            {
                sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(attrName), Tip = SutunTipi.Attribute, Kaynak = attrName });
            }

            var tumCocukGruplari = elemanlar.SelectMany(el => el.Elements()).GroupBy(c => c.Name.LocalName);
            foreach (var grup in tumCocukGruplari)
            {
                var cocukAdi = grup.Key;
                var ornekCocuk = grup.First();

                if (tekrarlayanlar.Contains(GetElementPath(ornekCocuk)))
                {
                    continue;
                }

                if (!ornekCocuk.HasElements)
                {
                    sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(cocukAdi), Tip = SutunTipi.ChildText, Kaynak = cocukAdi });
                }
                else if (wrapperAdlari.Contains(cocukAdi))
                {
                    // ── Wrapper çocuk: unwrap et, torun adlarını doğrudan kullan ──
                    // Örn: NationalityList/Nationality → sütun adı "nationality" (prefix yok)
                    var torunAdlari = grup.SelectMany(c => c.Elements())
                        .Where(t => !t.HasElements)
                        .Select(t => t.Name.LocalName).Distinct();
                    foreach (var torunAdi in torunAdlari)
                    {
                        sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(torunAdi), Tip = SutunTipi.GrandchildText, Kaynak = cocukAdi, AltKaynak = torunAdi });
                    }
                }
                else
                {
                    // Karmaşık çocuk ayrı tablo değil → attribute ve leaf torunlarını sütun yap
                    var tumCocukNitelikleri = grup.SelectMany(c => c.Attributes()).Select(a => a.Name.LocalName).Distinct();
                    foreach (var attrName in tumCocukNitelikleri)
                    {
                        sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(cocukAdi + "_" + attrName), Tip = SutunTipi.ChildAttribute, Kaynak = cocukAdi, AltKaynak = attrName });
                    }

                    // Leaf torun elemanlarını da sütun yap (GrandchildText)
                    var torunAdlari = grup.SelectMany(c => c.Elements())
                        .Where(t => !t.HasElements)
                        .Select(t => t.Name.LocalName).Distinct();
                    foreach (var torunAdi in torunAdlari)
                    {
                        sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(cocukAdi + "_" + torunAdi), Tip = SutunTipi.GrandchildText, Kaynak = cocukAdi, AltKaynak = torunAdi });
                    }
                }
            }

            if (!elemanlar.First().HasElements && elemanlar.Any(e => !string.IsNullOrWhiteSpace(e.Value)))
            {
                sutunlar.Add(new SutunBilgisi { Ad = "value", Tip = SutunTipi.Text });
            }

            return sutunlar.GroupBy(s => s.Ad).Select(g => g.First()).ToList();
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
                case SutunTipi.ForeignKey:
                    // Kaynak = ancestor element name, AltKaynak = id resolution method
                    var ata = el.Ancestors().FirstOrDefault(a =>
                        a.Name.LocalName.Equals(sutun.Kaynak, StringComparison.OrdinalIgnoreCase));
                    if (ata == null) return "";
                    return AtaIdDegeriBul(ata);
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
                case SutunTipi.GrandchildText:
                    return el.Element(sutun.Kaynak)?.Element(sutun.AltKaynak)?.Value?.Trim() ?? "";
                default:
                    return "";
            }
        }

        /// <summary>
        /// Bir ata elemanın ID değerini bulur:
        /// 1. "id" attribute
        /// 2. {ElemanAdı}Id child element
        /// 3. İlk *Id child element
        /// </summary>
        private static string AtaIdDegeriBul(XElement ata)
        {
            // 1. "id" attribute
            var idAttr = ata.Attributes()
                .FirstOrDefault(a => a.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase));
            if (idAttr != null) return idAttr.Value;

            var ataAdi = ata.Name.LocalName;

            // 2. {ElemanAdı}Id child
            var ozelIdChild = ata.Elements()
                .FirstOrDefault(c => c.Name.LocalName.Equals(ataAdi + "Id", StringComparison.OrdinalIgnoreCase)
                                  || c.Name.LocalName.Equals(ataAdi + "_id", StringComparison.OrdinalIgnoreCase));
            if (ozelIdChild != null && !ozelIdChild.HasElements) return ozelIdChild.Value?.Trim() ?? "";

            // 3. İlk *Id child
            var ilkIdChild = ata.Elements()
                .FirstOrDefault(c => c.Name.LocalName.EndsWith("Id", StringComparison.Ordinal)
                                  || c.Name.LocalName.EndsWith("_id", StringComparison.Ordinal));
            if (ilkIdChild != null && !ilkIdChild.HasElements) return ilkIdChild.Value?.Trim() ?? "";

            return "";
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
        /// 2. Wrapper'ları filtrele
        /// 3. Container'ları kök adıyla değiştir
        /// 4. "Value"/"Item"/"Data" son ekini kaldır
        /// 5. Ardışık tekrar eden kökleri tekilleştir
        /// 6. Son 3 anlamlı parçadan snake_case ad oluştur
        /// 7. Çakışma varsa tüm parçaları kullan
        /// </summary>
        private static string TabloAdiOlustur(string yol, HashSet<string> mevcutAdlar,
            HashSet<string> gercekContainerAdlari, HashSet<string> wrapperAdlari)
        {
            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var anlamliParcalar = new List<string>();
            foreach (var p in parcalar.Skip(1))
            {
                if (wrapperAdlari.Contains(p))
                    continue;

                if (gercekContainerAdlari.Contains(p))
                {
                    anlamliParcalar.Add(ContainerKokAdi(p));
                    continue;
                }

                anlamliParcalar.Add(p);
            }

            // Adım 4: "Value", "Item", "Data" son ekini kaldır
            var valueSuffixes = new[] { "Value", "Item", "Data" };
            for (int i = 0; i < anlamliParcalar.Count; i++)
            {
                var p = anlamliParcalar[i];
                foreach (var suffix in valueSuffixes)
                {
                    if (p.Length > suffix.Length && p.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        anlamliParcalar[i] = p.Substring(0, p.Length - suffix.Length);
                        break;
                    }
                }
            }

            // Adım 5: Ardışık tekrar eden kökleri tekilleştir
            var tekilParcalar = new List<string>();
            foreach (var p in anlamliParcalar)
            {
                if (tekilParcalar.Count > 0)
                {
                    var onceki = tekilParcalar[^1];
                    if (string.Equals(onceki, p, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (onceki.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (p.StartsWith(onceki, StringComparison.OrdinalIgnoreCase))
                    {
                        tekilParcalar[^1] = p;
                        continue;
                    }
                }
                tekilParcalar.Add(p);
            }

            var tabloAdi = string.Join("_", tekilParcalar.TakeLast(3).Select(SnakeCase));
            if (string.IsNullOrEmpty(tabloAdi)) tabloAdi = SnakeCase(parcalar.Last());

            if (mevcutAdlar.Contains(tabloAdi))
            {
                tabloAdi = string.Join("_", tekilParcalar.Select(SnakeCase));
            }
            return tabloAdi;
        }

        /// <summary>
        /// Container eleman adından kök adını çıkarır.
        /// Önce yapısal yöntem: container'ın en yaygın çocuk eleman adını kullan.
        /// Fallback: bilinen suffix'leri sıyır.
        /// </summary>
        private static string ContainerKokAdi(string containerAdi)
        {
            // Suffix-tabanlı fallback (güvenlik ağı olarak korunuyor)
            var suffixes = new[] { "List", "Types", "References", "Details" };
            var sonuc = containerAdi;
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

        /// <summary>
        /// En yakın ata-tabloyu bulur ve onun ID'sini FK olarak ekler.
        /// </summary>
        private static void ForeignKeySutunuEkle(List<SutunBilgisi> sutunlar, XElement ilkEleman, HashSet<string> tekrarlayanlar)
        {
            if (ilkEleman == null) return;

            // Ata elemanları yukarı doğru tara, ilk tablo olan atayı bul
            var ata = ilkEleman.Parent;
            while (ata != null)
            {
                var ataYol = GetElementPath(ata);
                if (tekrarlayanlar.Contains(ataYol))
                {
                    var ataAdi = ata.Name.LocalName;
                    var sutunAdi = SnakeCase(ataAdi) + "_id";
                    if (!sutunlar.Any(s => s.Ad == sutunAdi))
                    {
                        sutunlar.Insert(0, new SutunBilgisi { Ad = sutunAdi, Tip = SutunTipi.ForeignKey, Kaynak = ataAdi });
                    }
                    break;
                }
                ata = ata.Parent;
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
            Attribute, Text, ChildText, ParentAttribute, ChildAttribute, ForeignKey, GrandchildText
        }
        #endregion
    }
}

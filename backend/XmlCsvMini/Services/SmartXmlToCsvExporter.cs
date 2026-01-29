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

            // Anchor Entity tespiti: root altında (wrapper'lar atlanarak) en çok tekrar eden karmaşık eleman
            var anchorInfo = AnchorEntityBul(doc.Root);
            if (anchorInfo.ad != null)
                Console.WriteLine($"Anchor Entity: {anchorInfo.ad} (id attr: {anchorInfo.idAttr ?? "yok"})");

            var (tekrarlayanlar, gercekContainerAdlari, wrapperAdlari) = TekrarlayanElemanlariTespit(doc.Root, anchorInfo.ad);
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
                AnchorIdSutunuEkle(sutunlar, elemanlar.First(), anchorInfo.ad, anchorInfo.idAttr);

                var siraliSutunlar = sutunlar.OrderBy(s => s.Ad).ToList();

                Console.WriteLine($"  Sütunlar: {string.Join(", ", siraliSutunlar.Select(s => s.Ad))}");

                YazCsv(csvYolu, elemanlar, siraliSutunlar, anchorInfo.ad, anchorInfo.idAttr);

                olusturulanTabloAdlari.Add(tabloAdi);
            }

            Console.WriteLine($"\nToplam {olusturulanTabloAdlari.Count} tablo oluşturuldu.");
        }

        /// <summary>
        /// Root altında (wrapper'lar atlanarak) en çok tekrar eden karmaşık elemanı bulur.
        /// Bu eleman "Anchor Entity" olur ve alt tablolara id sütunu olarak eklenir.
        /// </summary>
        private static (string? ad, string? idAttr) AnchorEntityBul(XElement root)
        {
            // Root'un doğrudan çocuklarını tara, wrapper olanları atla
            var adaylar = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            void TaraAday(XElement el)
            {
                foreach (var cocuk in el.Elements())
                {
                    var ad = cocuk.Name.LocalName;
                    if (cocuk.HasElements || cocuk.HasAttributes)
                    {
                        // Karmaşık eleman
                        if (!adaylar.ContainsKey(ad))
                            adaylar[ad] = 0;
                        adaylar[ad]++;
                    }
                }
            }

            // Önce root'un doğrudan çocuklarına bak
            foreach (var cocuk in root.Elements())
            {
                var ad = cocuk.Name.LocalName;
                if (cocuk.HasElements || cocuk.HasAttributes)
                {
                    if (!adaylar.ContainsKey(ad))
                        adaylar[ad] = 0;
                    adaylar[ad]++;
                }
            }

            // Eğer root altında çok tekrar eden yoksa, bir seviye daha in (wrapper atla)
            if (!adaylar.Any(kv => kv.Value > 1))
            {
                adaylar.Clear();
                foreach (var cocuk in root.Elements())
                {
                    // Bu bir wrapper mı? (attribute'sız, text'siz, çocukları var)
                    if (!cocuk.HasAttributes && cocuk.HasElements
                        && !cocuk.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                    {
                        TaraAday(cocuk);
                    }
                }
            }

            if (!adaylar.Any(kv => kv.Value > 1))
                return (null, null);

            var enCokTekrar = adaylar.OrderByDescending(kv => kv.Value).First();
            var anchorAd = enCokTekrar.Key;

            // id attribute adını bul: ilk örneğe bak
            string? idAttr = null;
            var ornekEl = root.Descendants()
                .FirstOrDefault(d => d.Name.LocalName.Equals(anchorAd, StringComparison.OrdinalIgnoreCase));
            if (ornekEl != null)
            {
                // "id" adlı attribute'u ara
                var idA = ornekEl.Attributes()
                    .FirstOrDefault(a => a.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase));
                idAttr = idA?.Name.LocalName;
            }

            return (anchorAd, idAttr);
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
        private static (HashSet<string> tekrarlayanlar, HashSet<string> gercekContainerAdlari, HashSet<string> wrapperAdlari) TekrarlayanElemanlariTespit(XElement root, string? anchorEntityAdi)
        {
            var sonuc = new HashSet<string>();
            var containerCache = new Dictionary<XElement, bool>(ReferenceEqualityComparer.Instance);

            // Yapısal container tespiti: eleman adı → veri taşıyor mu?
            var containerVeriDurumu = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // Wrapper tespiti
            var wrapperAdayi = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // Anchor value tespiti
            var anchorValueAdlari = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                // Veri taşıyan container elemanları → anchor entity altındaysa ayrı tablo adayı
                if (containerVeriDurumu.TryGetValue(elAdi, out var tasiyor) && tasiyor)
                {
                    if (anchorEntityAdi != null &&
                        el.Ancestors().Any(a => a.Name.LocalName.Equals(anchorEntityAdi, StringComparison.OrdinalIgnoreCase)))
                    {
                        sonuc.Add(yol);
                    }
                }

                // ── Anchor Value tespiti ──
                if (elAdi.EndsWith("Value", StringComparison.Ordinal)
                    || elAdi.EndsWith("Item", StringComparison.Ordinal)
                    || elAdi.EndsWith("Data", StringComparison.Ordinal))
                {
                    if (anchorEntityAdi != null &&
                        el.Ancestors().Any(a => a.Name.LocalName.Equals(anchorEntityAdi, StringComparison.OrdinalIgnoreCase)))
                    {
                        sonuc.Add(yol);
                        anchorValueAdlari.Add(elAdi);
                    }
                }

                // Üst düzey referans/lookup tabloları
                if (el.HasAttributes && el.Parent != null
                    && parentContainer
                    && anchorEntityAdi != null
                    && !el.Ancestors().Any(a => a.Name.LocalName.Equals(anchorEntityAdi, StringComparison.OrdinalIgnoreCase)))
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
                    else if (parentContainer)
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
            // BUDAMA AŞAMASI 2: Anchor tablo absorpsiyonu
            // Anchor tablo = *Value, *Item, *Data ile biten veya tek karmaşık çocuğu olan parent'ın child'ı
            // ═══════════════════════════════════════════════════════════════
            var anchorAtilacaklar = new HashSet<string>();
            foreach (var yol in sonuc.ToList())
            {
                var elAdi = yol.Split('/').Last();
                if (!anchorValueAdlari.Contains(elAdi)) continue;

                // 2a. Çocuk path'leri sil → sütun olarak kalacaklar
                foreach (var digerYol in sonuc)
                {
                    if (digerYol.StartsWith(yol + "/"))
                        anchorAtilacaklar.Add(digerYol);
                }

                // 2b. Parent path'i sil → attribute olarak anchor'a geçecek
                var parentPath = yol.Substring(0, yol.LastIndexOf('/'));
                if (sonuc.Contains(parentPath))
                {
                    var parentElemanlar = BulElemanlar(root, parentPath);
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
            // ═══════════════════════════════════════════════════════════════
            var parentSilinecekler = new HashSet<string>();
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
                else
                {
                    var tumCocukNitelikleri = grup.SelectMany(c => c.Attributes()).Select(a => a.Name.LocalName).Distinct();
                    foreach (var attrName in tumCocukNitelikleri)
                    {
                        sutunlar.Add(new SutunBilgisi { Ad = SnakeCase(cocukAdi + "_" + attrName), Tip = SutunTipi.ChildAttribute, Kaynak = cocukAdi, AltKaynak = attrName });
                    }
                }
            }

            if (!elemanlar.First().HasElements && elemanlar.Any(e => !string.IsNullOrWhiteSpace(e.Value)))
            {
                sutunlar.Add(new SutunBilgisi { Ad = "value", Tip = SutunTipi.Text });
            }

            return sutunlar.GroupBy(s => s.Ad).Select(g => g.First()).ToList();
        }

        private static void YazCsv(string csvYolu, List<XElement> elemanlar, List<SutunBilgisi> sutunlar, string? anchorEntityAdi, string? anchorIdAttr)
        {
            using var sw = new StreamWriter(csvYolu, false, new UTF8Encoding(true));
            using var csv = new CsvWriter(sw, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = ";", HasHeaderRecord = true });

            foreach (var sutun in sutunlar) csv.WriteField(sutun.Ad);
            csv.NextRecord();

            foreach (var el in elemanlar)
            {
                foreach (var sutun in sutunlar)
                {
                    csv.WriteField(DegerCek(el, sutun, anchorEntityAdi, anchorIdAttr));
                }
                csv.NextRecord();
            }
        }

        private static string DegerCek(XElement el, SutunBilgisi sutun, string? anchorEntityAdi, string? anchorIdAttr)
        {
            switch (sutun.Tip)
            {
                case SutunTipi.AnchorId:
                    if (anchorEntityAdi == null || anchorIdAttr == null) return "";
                    var ancestor = el.Ancestors().FirstOrDefault(a =>
                        a.Name.LocalName.Equals(anchorEntityAdi, StringComparison.OrdinalIgnoreCase));
                    return ancestor?.Attribute(anchorIdAttr)?.Value ?? "";
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

        private static void AnchorIdSutunuEkle(List<SutunBilgisi> sutunlar, XElement ilkEleman, string? anchorEntityAdi, string? anchorIdAttr)
        {
            if (ilkEleman == null || anchorEntityAdi == null || anchorIdAttr == null) return;

            bool anchorAltinda = ilkEleman.Ancestors().Any(a =>
                a.Name.LocalName.Equals(anchorEntityAdi, StringComparison.OrdinalIgnoreCase));

            var sutunAdi = SnakeCase(anchorEntityAdi) + "_id";
            if (anchorAltinda && !sutunlar.Any(s => s.Ad == sutunAdi))
            {
                sutunlar.Insert(0, new SutunBilgisi { Ad = sutunAdi, Tip = SutunTipi.AnchorId });
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
            Attribute, Text, ChildText, ParentAttribute, ChildAttribute, AnchorId
        }
        #endregion
    }
}

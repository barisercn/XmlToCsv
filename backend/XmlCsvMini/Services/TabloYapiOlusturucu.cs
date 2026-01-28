using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using XmlCsvMini.Models;

namespace XmlCsvMini.Services
{
    /// <summary>
    /// XML keşif raporundan düzleştirilmiş tablo yapısı oluşturur.
    /// Tekil (0..1) alt elemanları ana tabloya düzleştirir,
    /// Çoklu (0..N) alt elemanları ayrı tablo olarak tutar.
    /// </summary>
    public class TabloYapiOlusturucu
    {
        private readonly KesifRaporu _rapor;
        private readonly Dictionary<string, AdayKayit> _adayMap;

        public TabloYapiOlusturucu(KesifRaporu rapor)
        {
            _rapor = rapor ?? throw new ArgumentNullException(nameof(rapor));

            // Tüm adayları tut (filtreleme yapmadan)
            _adayMap = rapor.AdayKayitlar?.ToDictionary(a => a.KayitYolu) ?? new();
        }

        /// <summary>
        /// Bir AdayKayit'ın gerçek tablo adayı olup olmadığını belirler.
        /// Yaprak elemanlar (sadece text içeren) tablo adayı değildir.
        /// </summary>
        private bool GercekTabloAdayiMi(AdayKayit aday)
        {
            if (aday.Alanlar == null || aday.Alanlar.Count == 0)
                return false;

            // Eğer sadece text() veya tek bir attribute varsa, bu yaprak elemandır
            var alanlar = aday.Alanlar;

            // Alt element alanları var mı? (./SomeElement şeklinde, @ veya text() olmayan)
            var altElementVar = alanlar.Any(a =>
                a.Tur == DugumTuru.Element &&
                !a.Yol.Contains("text()") &&
                !a.Yol.Contains("@") &&
                a.Yol.StartsWith("./") &&
                a.Yol.Count(c => c == '/') >= 1);

            // Birden fazla attribute var mı?
            var attributeSayisi = alanlar.Count(a => a.Tur == DugumTuru.Attribute || a.Yol.Contains("@"));

            // Alt element varsa VEYA birden fazla attribute varsa gerçek aday
            if (altElementVar)
                return true;

            // Sadece text() varsa ve hiç alt element yoksa yaprak eleman
            var sadeceTextVeAttribute = alanlar.All(a =>
                a.Yol.Contains("text()") ||
                a.Yol.Contains("@") ||
                a.Tur == DugumTuru.Attribute ||
                a.Tur == DugumTuru.Text);

            // Eğer sadece text ve attribute varsa ama attribute sayısı >= 2 ise gerçek aday
            // (örn: <Country code="TR" name="Turkey"/> gibi)
            if (sadeceTextVeAttribute && attributeSayisi >= 2)
                return true;

            // Eğer sadece text ve 1 veya 0 attribute varsa yaprak eleman
            if (sadeceTextVeAttribute)
                return false;

            return true;
        }

        /// <summary>
        /// Tüm düzleştirilmiş tablo yapılarını oluşturur.
        /// </summary>
        public List<DuzlestirilmisTabloYapisi> TabloYapisiOlustur()
        {
            var sonuc = new List<DuzlestirilmisTabloYapisi>();

            // 1. Kök tabloları bul (başka bir adayın altında olmayan)
            var kokAdaylar = BulKokAdaylar();

            // 2. Her kök aday için düzleştirilmiş yapı oluştur
            foreach (var kokAday in kokAdaylar)
            {
                var tabloYapisi = TabloYapisiOlusturRecursive(kokAday, null, "");
                if (tabloYapisi != null)
                {
                    sonuc.Add(tabloYapisi);
                }
            }

            return sonuc;
        }

        /// <summary>
        /// Kök adayları bulur - başka bir adayın altında olmayan adaylar.
        /// </summary>
        private List<AdayKayit> BulKokAdaylar()
        {
            var tumAdaylar = _rapor.AdayKayitlar ?? new List<AdayKayit>();

            // Başka bir adayın prefix'i altında olmayan adayları bul
            var kokAdaylar = tumAdaylar.Where(aday =>
                !tumAdaylar.Any(diger =>
                    aday != diger &&
                    aday.KayitYolu.StartsWith(diger.KayitYolu + "/", StringComparison.Ordinal)
                )
            ).ToList();

            // Eğer tek kök varsa ve sadece 1 kaydı varsa (wrapper element),
            // onun altındaki ilk seviye adayları kök olarak al
            if (kokAdaylar.Count == 1 && kokAdaylar[0].TahminiKayitSayisi == 1)
            {
                var wrapper = kokAdaylar[0];
                var altAdaylar = tumAdaylar
                    .Where(a => a != wrapper && a.KayitYolu.StartsWith(wrapper.KayitYolu + "/"))
                    .ToList();

                // Alt adaylar arasından gerçek kökleri bul
                var gercekKokler = altAdaylar.Where(aday =>
                    !altAdaylar.Any(diger =>
                        aday != diger &&
                        aday.KayitYolu.StartsWith(diger.KayitYolu + "/", StringComparison.Ordinal)
                    )
                ).ToList();

                if (gercekKokler.Count > 0)
                {
                    return gercekKokler;
                }
            }

            return kokAdaylar;
        }

        /// <summary>
        /// Recursive olarak tablo yapısı oluşturur.
        /// </summary>
        private DuzlestirilmisTabloYapisi? TabloYapisiOlusturRecursive(
            AdayKayit aday,
            string? ustTabloYolu,
            string prefix)
        {
            var tabloYapisi = new DuzlestirilmisTabloYapisi
            {
                TabloAdi = TabloAdiOlustur(aday.KayitYolu),
                KayitYolu = aday.KayitYolu,
                UstTabloYolu = ustTabloYolu
            };

            // Bu adayın doğrudan alanlarını ekle
            var dogrudan = aday.Alanlar?
                .Where(a => !AlanBirAltAdayMi(aday.KayitYolu, a.Yol))
                .ToList() ?? new List<AlanBilgisi>();

            foreach (var alan in dogrudan)
            {
                var sutun = SutunOlustur(alan, prefix);
                tabloYapisi.Sutunlar.Add(sutun);
            }

            // Alt adayları bul ve kardinaliteye göre karar ver
            var altAdaylar = BulDogrudenAltAdaylar(aday.KayitYolu);

            foreach (var altAday in altAdaylar)
            {
                // Bu alt adayın üst aday içindeki kardinalitesini belirle
                var kardinalite = AltAdayKardinalitesiBelirle(aday, altAday);

                if (kardinalite == "0..1")
                {
                    // Tekil: Düzleştir - alt adayın alanlarını prefix ile ekle
                    var altPrefix = PrefixOlustur(prefix, altAday.KayitYolu, aday.KayitYolu);
                    DuzlestirAltAdayRecursive(tabloYapisi, altAday, altPrefix);
                }
                else
                {
                    // Çoklu: Ayrı tablo oluştur
                    var altTabloYapisi = TabloYapisiOlusturRecursive(altAday, aday.KayitYolu, "");
                    if (altTabloYapisi != null)
                    {
                        tabloYapisi.AltTablolar.Add(altTabloYapisi);
                    }
                }
            }

            return tabloYapisi;
        }

        /// <summary>
        /// Tekil alt adayı ana tabloya düzleştirir (recursive).
        /// </summary>
        private void DuzlestirAltAdayRecursive(
            DuzlestirilmisTabloYapisi hedefTablo,
            AdayKayit altAday,
            string prefix)
        {
            // Alt adayın doğrudan alanlarını ekle
            var dogrudan = altAday.Alanlar?
                .Where(a => !AlanBirAltAdayMi(altAday.KayitYolu, a.Yol))
                .ToList() ?? new List<AlanBilgisi>();

            foreach (var alan in dogrudan)
            {
                var sutun = SutunOlustur(alan, prefix);
                hedefTablo.Sutunlar.Add(sutun);
            }

            // Alt adayın alt adaylarını da kontrol et
            var altAltAdaylar = BulDogrudenAltAdaylar(altAday.KayitYolu);

            foreach (var altAltAday in altAltAdaylar)
            {
                var kardinalite = AltAdayKardinalitesiBelirle(altAday, altAltAday);
                var yeniPrefix = PrefixOlustur(prefix, altAltAday.KayitYolu, altAday.KayitYolu);

                if (kardinalite == "0..1")
                {
                    // Tekil: Düzleştirmeye devam et
                    DuzlestirAltAdayRecursive(hedefTablo, altAltAday, yeniPrefix);
                }
                else
                {
                    // Çoklu: Ayrı tablo oluştur
                    var altTabloYapisi = TabloYapisiOlusturRecursive(altAltAday, hedefTablo.KayitYolu, "");
                    if (altTabloYapisi != null)
                    {
                        hedefTablo.AltTablolar.Add(altTabloYapisi);
                    }
                }
            }
        }

        /// <summary>
        /// Bir adayın doğrudan alt adaylarını bulur (bir seviye altındaki).
        /// Sadece gerçek tablo adaylarını döndürür (yaprak elemanları hariç tutar).
        /// </summary>
        private List<AdayKayit> BulDogrudenAltAdaylar(string ustYol)
        {
            var tumAdaylar = _rapor.AdayKayitlar ?? new List<AdayKayit>();
            var prefix = ustYol + "/";

            // Bu yolun altında olan adayları bul
            var altAdaylar = tumAdaylar
                .Where(a => a.KayitYolu.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            // Sadece doğrudan alt olanları filtrele (başka bir alt adayın altında olmayanlar)
            var dogrudan = altAdaylar.Where(aday =>
                !altAdaylar.Any(diger =>
                    aday != diger &&
                    aday.KayitYolu.StartsWith(diger.KayitYolu + "/", StringComparison.Ordinal)
                )
            ).ToList();

            // Sadece gerçek tablo adaylarını döndür (yaprak elemanları hariç tut)
            return dogrudan.Where(a => GercekTabloAdayiMi(a)).ToList();
        }

        /// <summary>
        /// Bir alanın başka bir aday kayıtla eşleşip eşleşmediğini kontrol eder.
        /// </summary>
        private bool AlanBirAltAdayMi(string ustYol, string alanYolu)
        {
            // Alan yolunu tam yola çevir
            var tamYol = alanYolu.StartsWith("./")
                ? ustYol + "/" + alanYolu.Substring(2).Split('/')[0]
                : ustYol + "/" + alanYolu.Split('/')[0];

            // text() veya attribute ise aday olamaz
            if (alanYolu.Contains("text()") || alanYolu.Contains("@"))
                return false;

            return _adayMap.ContainsKey(tamYol);
        }

        /// <summary>
        /// Alt adayın üst aday içindeki kardinalitesini belirler.
        /// </summary>
        private string AltAdayKardinalitesiBelirle(AdayKayit ustAday, AdayKayit altAday)
        {
            // Alt adayın üst aday içindeki göreli yolunu bul
            var goreliYol = "./" + altAday.KayitYolu.Substring(ustAday.KayitYolu.Length + 1);

            // Eğer goreliYol içinde / varsa, sadece ilk segmenti al
            var ilkSegment = goreliYol.Split('/')[1]; // "./Segment" -> "Segment"
            var aramaYolu = "./" + ilkSegment;

            // Üst adayın alanları arasında bu yolu ara
            var alan = ustAday.Alanlar?.FirstOrDefault(a =>
                a.Yol == aramaYolu ||
                a.Yol == "./" + ilkSegment ||
                a.Yol.StartsWith("./" + ilkSegment + "/"));

            if (alan != null)
            {
                return alan.Kardinalite ?? "0..1";
            }

            // Tahmini kayıt sayısına göre karar ver
            // Eğer alt adayın kayıt sayısı, üst adayın kayıt sayısından fazlaysa 0..N
            if (altAday.TahminiKayitSayisi > ustAday.TahminiKayitSayisi)
            {
                return "0..N";
            }

            return "0..1";
        }

        /// <summary>
        /// Alan bilgisinden sütun oluşturur.
        /// </summary>
        private DuzSutun SutunOlustur(AlanBilgisi alan, string prefix)
        {
            var sutunAdi = SutunAdiOlustur(alan.Yol, prefix);

            return new DuzSutun
            {
                SutunAdi = sutunAdi,
                KaynakYol = alan.Yol,
                VeriTuru = alan.CikarilanTur,
                Birlestirici = alan.Kardinalite?.Contains("N") == true ? " | " : null,
                AttributeMi = alan.Tur == DugumTuru.Attribute,
                TextMi = alan.Tur == DugumTuru.Text || alan.Yol.Contains("text()")
            };
        }

        /// <summary>
        /// Sütun adı oluşturur (snake_case, prefix ile).
        /// </summary>
        private string SutunAdiOlustur(string alanYolu, string prefix)
        {
            // Yoldan anlamlı son parçayı al
            var parcalar = (alanYolu ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            string sonParca = "sutun";

            for (int i = parcalar.Length - 1; i >= 0; i--)
            {
                var p = parcalar[i];
                if (p is "." or "text()") continue;
                sonParca = p.StartsWith("@") ? p.Substring(1) : p;
                break;
            }

            // Prefix varsa ekle
            var tamAd = string.IsNullOrEmpty(prefix) ? sonParca : $"{prefix}_{sonParca}";

            // Snake_case'e çevir
            return MetniSnakeCaseYap(tamAd);
        }

        /// <summary>
        /// Prefix oluşturur.
        /// </summary>
        private string PrefixOlustur(string mevcutPrefix, string altYol, string ustYol)
        {
            // Alt yoldan üst yolu çıkar ve ilk segmenti al
            var goreli = altYol.Substring(ustYol.Length + 1);
            var ilkSegment = goreli.Split('/')[0];

            // Mevcut prefix varsa birleştir
            var yeniPrefix = string.IsNullOrEmpty(mevcutPrefix)
                ? ilkSegment
                : $"{mevcutPrefix}_{ilkSegment}";

            return MetniSnakeCaseYap(yeniPrefix);
        }

        /// <summary>
        /// Tablo adı oluşturur.
        /// </summary>
        private string TabloAdiOlustur(string kayitYolu)
        {
            var parcalar = kayitYolu.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parcalar.Length == 0) return "tablo";

            // Son segmenti al
            var ad = parcalar[^1];

            return MetniSnakeCaseYap(ad);
        }

        /// <summary>
        /// Metni snake_case'e çevirir.
        /// </summary>
        private static string MetniSnakeCaseYap(string girdi)
        {
            if (string.IsNullOrWhiteSpace(girdi)) return "sutun";

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

            return Regex.Replace(sb.ToString(), "[^a-z0-9_]+", "_").Trim('_');
        }
    }
}

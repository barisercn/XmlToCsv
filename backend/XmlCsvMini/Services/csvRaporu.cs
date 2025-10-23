// Services/EslemeBaslatici.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using XmlCsvMini.Models;

namespace XmlCsvMini.Services
{
    public static class EslemeBaslatici
    {
        public sealed class Secenekler
        {
            public string RaporYolu { get; set; } = "";
            public string CiktiEslemeYolu { get; set; } = "mapping/otomatik.esleme.json";
            public int AdayIndeksi { get; set; } = 0;
            public string Birlestirici { get; set; } = " | ";
            public int MaksSutunSayisi { get; set; } = 120;
        }
        public static EslemeYapilandirmasi DuzEslemeOlustur(KesifRaporu rapor, int adayIndeksi)
        {
            var aday = rapor.AdayKayitlar[adayIndeksi];
            var alanlar = aday.Alanlar ?? new List<AlanBilgisi>();

            var esleme = new EslemeYapilandirmasi
            {
                KayitYolu = aday.KayitYolu,
                AdUzaylari = rapor.AdUzaylari != null ? new SozlukAdUzayi(rapor.AdUzaylari) : null,
                Diziler = new DiziStratejisi(),
                Sutunlar = alanlar.Select(alan => new KolonEsleme
                {
                    Csv = MetniSnakeCaseYap(AnlamliSonParca(alan.Yol)),
                    Kaynak = alan.Yol,
                    Birlestir = (alan.Kardinalite ?? "").Contains('N') ? " | " : null
                }).ToList()
            };
            return esleme;
        }

        public static int DuzEslemeOlustur(Secenekler secenek)
        {
            var raporMetni = File.ReadAllText(secenek.RaporYolu);
            var rapor = JsonSerializer.Deserialize<KesifRaporu>(raporMetni, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Keşif raporu JSON deserialize edilemedi.");

            if (rapor.AdayKayitlar == null || rapor.AdayKayitlar.Count == 0)
                throw new InvalidOperationException("Raporda hiç aday kayıt bulunamadı.");
            if (secenek.AdayIndeksi < 0 || secenek.AdayIndeksi >= rapor.AdayKayitlar.Count)
                throw new ArgumentOutOfRangeException(nameof(secenek.AdayIndeksi), "Geçersiz aday indeksi.");

            var aday = rapor.AdayKayitlar[secenek.AdayIndeksi];

            var alanlar = (aday.Alanlar ?? new List<AlanBilgisi>())
                .OrderBy(a => a.Tur switch { DugumTuru.Element => 0, DugumTuru.Attribute => 1, _ => 2 })
                .ThenBy(a => a.Yol, StringComparer.Ordinal)
                .Take(secenek.MaksSutunSayisi)
                .ToList();

            var esleme = new EslemeYapilandirmasi
            {
                KayitYolu = aday.KayitYolu,
                AdUzaylari = rapor.AdUzaylari != null ? new SozlukAdUzayi(rapor.AdUzaylari) : null,
                Diziler = new DiziStratejisi { Birlestirici = secenek.Birlestirici },
                Sutunlar = alanlar.Select(alan => new KolonEsleme
                {
                    Csv = MetniSnakeCaseYap(AnlamliSonParca(alan.Yol)),
                    Kaynak = alan.Yol,
                    Zorunlu = false,
                    Birlestir = (alan.Kardinalite ?? "").Contains('N', StringComparison.OrdinalIgnoreCase) ? secenek.Birlestirici : null
                }).ToList()
            };

            Directory.CreateDirectory(Path.GetDirectoryName(secenek.CiktiEslemeYolu)!);
            var jsonAyar = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var json = JsonSerializer.Serialize(esleme, jsonAyar);
            File.WriteAllText(secenek.CiktiEslemeYolu, json, new UTF8Encoding(false));

            Console.WriteLine($"-> Eşleme dosyası oluşturuldu: {Path.GetFullPath(secenek.CiktiEslemeYolu)}");
            return esleme.Sutunlar.Count;
        }

        public static EslemeYapilandirmasi FiltreliEslemeOlustur(KesifRaporu rapor, int adayIndeksi, string alanOnEki)
        {
            var aday = rapor.AdayKayitlar[adayIndeksi];
            var filtrelenmisAlanlar = (aday.Alanlar ?? new List<AlanBilgisi>())
                .Where(a => (a.Yol.Split('_').FirstOrDefault() ?? "").TrimStart('.', '/') == alanOnEki || !a.Yol.Contains('_'))
                .ToList();

            if (!filtrelenmisAlanlar.Any(a => a.Yol.Contains('_')))
            {
                filtrelenmisAlanlar = (aday.Alanlar ?? new List<AlanBilgisi>()).ToList();
            }

            var yeniEsleme = new EslemeYapilandirmasi
            {
                KayitYolu = $"{aday.KayitYolu}[{filtrelenmisAlanlar.FirstOrDefault(a => a.Yol.Contains('_'))?.Yol ?? "1=1"}]",
                AdUzaylari = rapor.AdUzaylari != null ? new SozlukAdUzayi(rapor.AdUzaylari) : null,
                Diziler = new DiziStratejisi(),
                Sutunlar = filtrelenmisAlanlar.Select(alan => new KolonEsleme
                {
                    Csv = MetniSnakeCaseYap(AnlamliSonParca(alan.Yol)),
                    Kaynak = alan.Yol,
                    Zorunlu = false,
                    Birlestir = (alan.Kardinalite ?? "").Contains('N', StringComparison.OrdinalIgnoreCase) ? " | " : null
                }).ToList()
            };
            return yeniEsleme;
        }

        // DÜZELTME: Bu metotlar 'public' yapıldı, böylece başka sınıflar da erişebilir.
        public static string AnlamliSonParca(string yol)
        {
            var parcalar = yol.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = parcalar.Length - 1; i >= 0; i--)
            {
                var p = parcalar[i];
                if (p is "." or "text()") continue;
                return p.StartsWith("@", StringComparison.Ordinal) ? p.Substring(1) : p;
            }
            return "sutun";
        }

        public static string MetniSnakeCaseYap(string girdi)
        {
            if (string.IsNullOrWhiteSpace(girdi)) return "sutun";
            var sb = new StringBuilder();
            for (int i = 0; i < girdi.Length; i++)
            {
                char ch = girdi[i];
                if (char.IsUpper(ch) && i > 0 && char.IsLetterOrDigit(sb[sb.Length - 1]))
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(ch));
            }
            return Regex.Replace(sb.ToString(), "[^a-z0-9_]+", "_").Trim('_');
        }
    }
}
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using XmlCsvMini.Models;

namespace XmlCsvMini.Services
{
    /// <summary>
    /// İşleri (job) uygulama çalıştığı sürece bellekte tutan basit yönetici.
    /// İlk aşamada DB kullanmıyoruz; gerekirse sonra ekleyebiliriz.
    /// </summary>
    public sealed class InMemoryIslemeGoreviYonetimi : IIslemeGoreviYonetimi
    {
        private readonly ConcurrentDictionary<string, IslemeGorevi> _gorevler =
            new ConcurrentDictionary<string, IslemeGorevi>(StringComparer.OrdinalIgnoreCase);

        public IslemeGorevi YeniGorevOlustur(string girdiXmlYolu, string orijinalDosyaAdi)
        {
            var gorev = new IslemeGorevi
            {
                Id = Guid.NewGuid().ToString("N"),
                DosyaYolu = girdiXmlYolu,
                OrijinalDosyaAdi = orijinalDosyaAdi,
                Durum = "Pending",
                Mesaj = "İş kuyruğa alındı.",
                OlusturmaZamaniUtc = DateTime.UtcNow
            };

            _gorevler[gorev.Id] = gorev;
            return gorev;
        }

        public IslemeGorevi? GorevGetir(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return _gorevler.TryGetValue(id, out var gorev) ? gorev : null;
        }

        public IReadOnlyCollection<IslemeGorevi> TumGorevler()
        {
            // ConcurrentDictionary.Values, ICollection döner.
            // Biz IReadOnlyCollection istiyoruz; ToList().AsReadOnly() ile uyumlu hale getiriyoruz.
            return _gorevler.Values.ToList().AsReadOnly();
        }

        public void GorevDurumGuncelle(string id, string durum, string? mesaj = null, string? ciktiZipAdi = null)
        {
            if (!_gorevler.TryGetValue(id, out var gorev))
                return;

            gorev.Durum = durum;
            if (!string.IsNullOrWhiteSpace(mesaj))
                gorev.Mesaj = mesaj;

            if (!string.IsNullOrWhiteSpace(ciktiZipAdi))
                gorev.CiktiZipAdi = ciktiZipAdi;

            gorev.SonGuncellemeZamaniUtc = DateTime.UtcNow;
        }
    }
}

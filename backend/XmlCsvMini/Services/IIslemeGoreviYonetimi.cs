using System.Collections.Generic;
using XmlCsvMini.Models;

namespace XmlCsvMini.Services
{
    /// <summary>
    /// Uzun süreli XML işleme görevlerinin (Job) durumunu takip eden basit servis.
    /// Şimdilik her şey bellek içinde (in-memory) tutulacak.
    /// </summary>
    public interface IIslemeGoreviYonetimi
    {
        /// <summary>Yeni bir iş oluşturur ve kaydeder.</summary>
        IslemeGorevi YeniGorevOlustur(string girdiXmlYolu, string orijinalDosyaAdi);

        /// <summary>Verilen ID’ye ait işi döner, yoksa null döner.</summary>
        IslemeGorevi? GorevGetir(string id);

        /// <summary>Tüm işleri sadece okuma amaçlı listeler.</summary>
        IReadOnlyCollection<IslemeGorevi> TumGorevler();

        /// <summary>Bir işin durumunu ve isteğe bağlı mesaj/çıktı dosya adını günceller.</summary>
        void GorevDurumGuncelle(string id, string durum, string? mesaj = null, string? ciktiZipAdi = null);
    }
}

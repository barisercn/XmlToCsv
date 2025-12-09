namespace XmlCsvMini.Models
{
    /// <summary>
    /// Uzun süren XML işleme görevlerini (job) temsil eder.
    /// </summary>
    public class IslemeGorevi
    {
        /// <summary>Job ID (kullanıcıya döndüğümüz değer).</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Sunucuda kaydedilen geçici XML dosyasının tam yolu.</summary>
        public string DosyaYolu { get; set; } = string.Empty;

        /// <summary>Kullanıcının yüklediği dosyanın orijinal adı (ör. complex_sample.xml).</summary>
        public string OrijinalDosyaAdi { get; set; } = string.Empty;

        /// <summary>Job durumu: Pending, Running, Completed, Failed.</summary>
        public string Durum { get; set; } = "Pending";

        /// <summary>Kullanıcıya gösterilebilecek açıklama / durum mesajı.</summary>
        public string? Mesaj { get; set; }

        /// <summary>İşleme başarılı olursa oluşan ZIP dosyasının adı (sadece dosya adı).</summary>
        public string? CiktiZipAdi { get; set; }

        /// <summary>İş kaydının oluşturulduğu zaman (UTC).</summary>
        public DateTime OlusturmaZamaniUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Son durum güncelleme zamanı (UTC).</summary>
        public DateTime? SonGuncellemeZamaniUtc { get; set; }
    }
}
